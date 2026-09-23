using System.Collections.Concurrent;
using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

// Clipboard rendering can block in another app. Keep it off the raw-input STA.
internal sealed class DesktopClipboard : IDisposable
{
    private readonly ConcurrentQueue<DesktopMessage> commands = new();
    private volatile bool disposed, permitted;
    private volatile uint permittedEpoch;
    private volatile FileClipboardSession? fileSession;
    public DesktopClipboard(Action<DesktopMessage> send)
    {
        var thread = new Thread(() => Run(send)) { IsBackground = true, Name = "DeusKVM clipboard" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
    public void Post(DesktopMessage message)
    {
        if (disposed) return;
        if (message.Kind == "clipboard-file-data") { if (message.Clipboard is { } data) fileSession?.Receive(data); return; }
        if (message.Epoch == permittedEpoch && message.Kind is "clipboard-file-clear" or "clipboard-file-offer" or "clipboard-apply") fileSession?.Dispose();
        if (message.Kind == "clipboard-start")
        {
            fileSession?.Dispose();
            permitted = false; permittedEpoch = message.Epoch; permitted = message.Ok;
            // A lifecycle command must never be lost behind obsolete copies.
            while (commands.Count >= 16 && commands.TryDequeue(out _)) { }
        }
        if (commands.Count < 16) commands.Enqueue(message);
    }
    public void Dispose() { disposed = true; fileSession?.Dispose(); while (commands.TryDequeue(out _)) { } }
    private void Run(Action<DesktopMessage> send)
    {
        Application.OleRequired();
        using var context = new ApplicationContext();
        var window = new NativeWindow();
        window.CreateHandle(new CreateParams { Caption = "DeusKVM clipboard", Parent = new IntPtr(-3) });
        using var timer = new System.Windows.Forms.Timer { Interval = 200 };
        bool? available = null;
        var enabled = false; var priming = false; var yield = false;
        uint epoch = 0;
        var versions = new ClipboardRevision();
        DesktopMessage? pending = null, pendingFile = null;
        VirtualFileClipboard? virtualFile = null;
        void ClearFile()
        {
            fileSession?.Dispose(); fileSession = null;
            if (virtualFile?.Revoke() == true) versions.Imported(ClipboardNative.GetClipboardSequenceNumber());
            virtualFile = null; pendingFile = null;
        }
        long applyDeadline = 0;
        bool CanAccess() => !disposed && permitted && permittedEpoch == epoch && DesktopNative.IsDefaultDesktop();
        timer.Tick += (_, _) =>
        {
            if (disposed) { context.ExitThread(); return; }
            var accessible = DesktopNative.IsDefaultDesktop();
            if (available != accessible)
            {
                ClearFile(); available = accessible; enabled = false; versions.Reset(); pending = null;
                send(new DesktopMessage("clipboard-availability", Ok: accessible));
            }
            while (commands.TryDequeue(out var message))
            {
                if (message.Kind == "clipboard-start")
                {
                    ClearFile(); epoch = message.Epoch; enabled = message.Ok && accessible;
                    priming = true; versions.Reset(); pending = null; yield = false;
                }
                else if (enabled && message.Epoch == epoch)
                {
                    if (message.Kind == "clipboard-file-clear") ClearFile();
                    if (message.Kind == "clipboard-file-offer") { ClearFile(); pendingFile = message; applyDeadline = Environment.TickCount64 + 2000; }
                    if (message.Kind == "clipboard-yield") yield = true;
                    if (message.Kind == "clipboard-apply" && message.Clipboard is { Length: <= ClipboardTransfer.MaximumBytes })
                    { ClearFile(); pending = message; applyDeadline = Environment.TickCount64 + 2000; }
                }
            }
            if (!enabled || !accessible || !CanAccess()) return;
            var revision = ClipboardNative.GetClipboardSequenceNumber();
            if (versions.Observed != revision)
            {
                ClearFile(); pending = null;
                if (!ClipboardNative.Read(window.Handle, out revision, out var bytes)) return;
                if (!CanAccess()) return;
                versions.Capture(revision);
                send(new DesktopMessage("clipboard-copy", Epoch: epoch, Revision: revision, Clipboard: bytes, Ok: !priming));
                priming = false;
            }
            if (yield) { yield = false; send(new DesktopMessage("clipboard-yield", Epoch: epoch)); }
            if (pendingFile is { } fileUpdate)
            {
                if (!versions.CanApply(fileUpdate.Revision, revision) || Environment.TickCount64 > applyDeadline) { pendingFile = null; return; }
                var offer = FileClipboardOffer.Parse(fileUpdate.Clipboard!);
                var fileEpoch = epoch;
                uint fileRevision = 0;
                var session = new FileClipboardSession(offer, request => send(new DesktopMessage("clipboard-file-get", Epoch: fileEpoch, Clipboard: request)));
                var dataObject = new VirtualFileClipboard(session, () => !disposed && permitted && permittedEpoch == fileEpoch &&
                    ReferenceEquals(fileSession, session) && (fileRevision == 0 || ClipboardNative.GetClipboardSequenceNumber() == fileRevision) && DesktopNative.IsDefaultDesktop());
                fileSession = session;
                if (dataObject.Install()) { fileRevision = ClipboardNative.GetClipboardSequenceNumber(); virtualFile = dataObject; versions.Imported(fileRevision); pendingFile = null; }
                else { session.Dispose(); fileSession = null; }
                return;
            }
            if (pending is not { } update) return;
            if (!versions.CanApply(update.Revision, revision) || Environment.TickCount64 > applyDeadline) { pending = null; return; }
            if (!CanAccess()) return;
            if (ClipboardNative.Write(window.Handle, update.Clipboard!, revision, CanAccess, out var written))
            { versions.Imported(written); pending = null; }
        };
        timer.Start();
        try { Application.Run(context); }
        finally { ClearFile(); window.DestroyHandle(); }
    }
}
