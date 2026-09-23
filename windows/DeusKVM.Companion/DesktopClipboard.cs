using System.Collections.Concurrent;
using System.Text.Json;
using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

// Clipboard rendering can block in another app. Keep it off the raw-input STA.
internal sealed class DesktopClipboard : IDisposable
{
    private readonly ConcurrentQueue<DesktopMessage> commands = new();
    private volatile bool disposed, permitted;
    private volatile uint permittedEpoch;
    private volatile FileClipboardSession? fileSession;
    private volatile CancellationTokenSource? sending;
    public DesktopClipboard(Action<DesktopMessage> send)
    {
        var thread = new Thread(() => Run(send)) { IsBackground = true, Name = "DeusKVM clipboard" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
    public void Post(DesktopMessage message)
    {
        if (disposed) return;
        if (message.Epoch == permittedEpoch && message.Kind is "clipboard-file-clear" or "clipboard-file-offer" or "clipboard-apply") fileSession?.Dispose();
        if (message.Kind == "clipboard-start")
        {
            fileSession?.Dispose(); sending?.Cancel();
            permitted = false; permittedEpoch = message.Epoch; permitted = message.Ok;
            // A lifecycle command must never be lost behind obsolete copies.
            while (commands.Count >= 16 && commands.TryDequeue(out _)) { }
        }
        if (commands.Count < 16) commands.Enqueue(message);
    }
    public void Dispose() { disposed = true; fileSession?.Dispose(); sending?.Cancel(); while (commands.TryDequeue(out _)) { } }
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
        ClipboardFileSource? source = null;
        FileClipboardOffer? sourceOffer = null;
        void ClearFile(bool clearSource = false)
        {
            fileSession?.Dispose(); fileSession = null;
            if (clearSource) { sending?.Cancel(); source = null; sourceOffer = null; }
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
                ClearFile(true); available = accessible; enabled = false; versions.Reset(); pending = null;
                send(new DesktopMessage("clipboard-availability", Ok: accessible));
            }
            while (commands.TryDequeue(out var message))
            {
                if (message.Kind == "clipboard-start")
                {
                    ClearFile(true); epoch = message.Epoch; enabled = message.Ok && accessible;
                    priming = true; versions.Reset(); pending = null; yield = false;
                }
                else if (enabled && message.Epoch == epoch)
                {
                    if (message.Kind == "clipboard-file-send" && source is { } current && sourceOffer is { } offered && message.Clipboard is { } payload)
                    {
                        var request = FileClipboardOffer.Parse(payload);
                        if (request.Epoch == offered.Epoch && request.Sequence == offered.Sequence && request.Size == offered.Size && request.Name == offered.Name)
                        {
                            sending?.Cancel(); var token = new CancellationTokenSource(); sending = token;
                            var fileEpoch = epoch; var fileRevision = offered.Sequence;
                            _ = Task.Run(async () => {
                                try {
                                    await NetworkFileSender.Send(request, (offset, count) => {
                                        bool Valid() => !disposed && permitted && permittedEpoch == fileEpoch &&
                                            ClipboardNative.GetClipboardSequenceNumber() == fileRevision && DesktopNative.IsDefaultDesktop();
                                        if (!Valid()) return null;
                                        var block = current.Read(offset, count); return Valid() ? block : null;
                                    }, token.Token);
                                } catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or OperationCanceledException or System.Security.Cryptography.CryptographicException) {
                                    FilePasteDiagnostics.Write($"send-ended hr=0x{error.HResult:X8}");
                                }
                            });
                        }
                    }
                    if (message.Kind == "clipboard-file-clear") ClearFile();
                    if (message.Kind == "clipboard-file-offer") { ClearFile(); pendingFile = message; applyDeadline = Environment.TickCount64 + 2000; }
                    if (message.Kind == "clipboard-yield") yield = true;
                    if (message.Kind == "clipboard-apply" && message.Clipboard is { Length: <= ClipboardTransfer.MaximumBytes })
                    { ClearFile(); pending = message; applyDeadline = Environment.TickCount64 + 2000; }
                }
            }
            if (!enabled || !accessible || !CanAccess()) return;
            var revision = ClipboardNative.GetClipboardSequenceNumber();
            // OLE delayed rendering may advance the sequence without a new copy.
            // Preserve the local token while our original data object still owns it.
            if (virtualFile?.IsCurrent == true) versions.Imported(revision);
            if (versions.Observed != revision)
            {
                ClearFile(true); pending = null;
                if (!ClipboardNative.Read(window.Handle, out revision, out var bytes, out var copiedFile)) return;
                if (!CanAccess()) return;
                versions.Capture(revision);
                send(new DesktopMessage("clipboard-copy", Epoch: epoch, Revision: revision, Clipboard: bytes, Ok: !priming));
                source = copiedFile;
                sourceOffer = source is null ? null : new FileClipboardOffer(epoch, revision, source.Name, source.Size);
                if (sourceOffer is not null) send(new DesktopMessage("clipboard-file-copy", Epoch: epoch, Revision: revision,
                    Clipboard: JsonSerializer.SerializeToUtf8Bytes(sourceOffer), Ok: !priming));
                priming = false;
            }
            if (yield) {
                yield = false; send(new DesktopMessage("clipboard-yield", Epoch: epoch));
                if (sourceOffer is not null) send(new DesktopMessage("clipboard-file-copy", Epoch: epoch, Revision: sourceOffer.Sequence,
                    Clipboard: JsonSerializer.SerializeToUtf8Bytes(sourceOffer), Ok: true));
            }
            if (pendingFile is { } fileUpdate)
            {
                if (!versions.CanApply(fileUpdate.Revision, revision) || Environment.TickCount64 > applyDeadline) { pendingFile = null; return; }
                var offer = FileClipboardOffer.Parse(fileUpdate.Clipboard!);
                sending?.Cancel(); source = null; sourceOffer = null;
                var fileEpoch = epoch;
                var session = new FileClipboardSession(offer, FilePasteDiagnostics.Write);
                var dataObject = new VirtualFileClipboard(session, () => !disposed && permitted && permittedEpoch == fileEpoch &&
                    ReferenceEquals(fileSession, session) && DesktopNative.IsDefaultDesktop());
                fileSession = session;
                if (dataObject.Install()) { virtualFile = dataObject; versions.Imported(ClipboardNative.GetClipboardSequenceNumber()); pendingFile = null; }
                else { session.Dispose(); fileSession = null; }
                return;
            }
            if (pending is not { } update) return;
            if (!versions.CanApply(update.Revision, revision) || Environment.TickCount64 > applyDeadline) { pending = null; return; }
            if (!CanAccess()) return;
            if (ClipboardNative.Write(window.Handle, update.Clipboard!, revision, CanAccess, out var written))
            { sending?.Cancel(); source = null; sourceOffer = null; versions.Imported(written); pending = null; }
        };
        timer.Start();
        try { Application.Run(context); }
        finally { ClearFile(true); window.DestroyHandle(); }
    }
}
