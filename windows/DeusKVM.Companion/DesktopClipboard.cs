using System.Collections.Concurrent;
using System.Runtime.InteropServices;
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
    private volatile CancellationTokenSource? receivingImage;
    // The clipboard thread's window, so other threads can wake it; zero until it exists.
    private IntPtr wakeWindow;
    public DesktopClipboard(Action<DesktopMessage> send)
    {
        var thread = new Thread(() => Run(send)) { IsBackground = true, Name = "DeusKVM clipboard" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
    public void Post(DesktopMessage message)
    {
        if (disposed) return;
        if (message.Epoch == permittedEpoch && message.Kind is "clipboard-file-clear" or "clipboard-file-offer" or "clipboard-apply")
        { receivingImage?.Cancel(); fileSession?.Dispose(); }
        if (message.Kind == "clipboard-start")
        {
            receivingImage?.Cancel(); fileSession?.Dispose(); sending?.Cancel();
            permitted = false; permittedEpoch = message.Epoch; permitted = message.Ok;
            // A lifecycle command must never be lost behind obsolete copies.
            while (commands.Count >= 16 && commands.TryDequeue(out _)) { }
        }
        if (commands.Count < 16) commands.Enqueue(message);
        Wake();
    }
    public void Dispose() { disposed = true; receivingImage?.Cancel(); fileSession?.Dispose(); sending?.Cancel(); while (commands.TryDequeue(out _)) { } Wake(); }
    private void Wake()
    {
        var window = Volatile.Read(ref wakeWindow);
        if (window != IntPtr.Zero) PostMessage(window, ClipboardWindow.WakeMessage, IntPtr.Zero, IntPtr.Zero);
    }
    private void Run(Action<DesktopMessage> send)
    {
        Application.OleRequired();
        using var context = new ApplicationContext();
        var window = new ClipboardWindow();
        window.CreateHandle(new CreateParams { Caption = "DeusKVM clipboard", Parent = new IntPtr(-3) });
        // Clipboard changes, posted commands and session lock changes wake this thread. A slow
        // timer keeps the desktop check; the short one runs only while an operation can still
        // fail transiently (an unread change, a pending write, file or image).
        using var idle = new System.Windows.Forms.Timer { Interval = 1000 };
        using var retry = new System.Windows.Forms.Timer { Interval = 200 };
        bool? available = null;
        var enabled = false; var priming = false; var yield = false;
        uint epoch = 0;
        var versions = new ClipboardRevision();
        DesktopMessage? pending = null, pendingFile = null;
        DesktopMessage? imageUpdate = null;
        Task<byte[]?>? incomingImage = null;
        long imageWriteDeadline = 0;
        VirtualFileClipboard? virtualFile = null;
        ClipboardFileSource? source = null;
        FileClipboardOffer? sourceOffer = null;
        void ClearFile(bool clearSource = false)
        {
            fileSession?.Dispose(); fileSession = null;
            receivingImage?.Cancel(); receivingImage = null;
            incomingImage = null; imageUpdate = null;
            imageWriteDeadline = 0;
            if (clearSource) { sending?.Cancel(); source = null; sourceOffer = null; }
            if (virtualFile?.Revoke() == true) versions.Imported(ClipboardNative.GetClipboardSequenceNumber());
            virtualFile = null; pendingFile = null;
        }
        long applyDeadline = 0;
        bool CanAccess() => !disposed && permitted && permittedEpoch == epoch && DesktopNative.IsDefaultDesktop();
        void Tick()
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
                        if (request.Epoch == offered.Epoch && request.Sequence == offered.Sequence && request.Size == offered.Size &&
                            request.Name == offered.Name && request.ContentType == offered.ContentType)
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
                    if (message.Kind == "clipboard-file-offer") { ClearFile(); pendingFile = message; applyDeadline = RuntimeCompat.TickCount64 + 2000; }
                    if (message.Kind == "clipboard-yield") yield = true;
                    if (message.Kind == "clipboard-apply" && message.Clipboard is { Length: <= ClipboardTransfer.MaximumBytes })
                    { ClearFile(); pending = message; applyDeadline = RuntimeCompat.TickCount64 + 2000; }
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
                sourceOffer = source is null ? null : new FileClipboardOffer(epoch, revision, source.Name, source.Size, ContentType: source.ContentType);
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
                if (!versions.CanApply(fileUpdate.Revision, revision) || RuntimeCompat.TickCount64 > applyDeadline) { pendingFile = null; return; }
                var offer = FileClipboardOffer.Parse(fileUpdate.Clipboard!);
                sending?.Cancel(); source = null; sourceOffer = null;
                var fileEpoch = epoch;
                var session = new FileClipboardSession(offer, FilePasteDiagnostics.Write);
                if (offer.IsImage)
                {
                    fileSession = session; imageUpdate = fileUpdate; pendingFile = null;
                    var token = new CancellationTokenSource(); receivingImage = token;
                    var imageRevision = revision;
                    incomingImage = Task.Run<byte[]?>(() => {
                        try {
                            return ClipboardImageContent.Read(session, () => !token.IsCancellationRequested && !disposed && permitted && permittedEpoch == fileEpoch &&
                                ReferenceEquals(fileSession, session) && ClipboardNative.GetClipboardSequenceNumber() == imageRevision &&
                                DesktopNative.IsDefaultDesktop());
                        } catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or OperationCanceledException or
                            System.Security.Cryptography.CryptographicException or ObjectDisposedException) { return null; }
                        finally { session.Dispose(); }
                    });
                    return;
                }
                var dataObject = new VirtualFileClipboard(session, () => !disposed && permitted && permittedEpoch == fileEpoch &&
                    ReferenceEquals(fileSession, session) && DesktopNative.IsDefaultDesktop());
                fileSession = session;
                if (dataObject.Install()) { virtualFile = dataObject; versions.Imported(ClipboardNative.GetClipboardSequenceNumber()); pendingFile = null; }
                else { session.Dispose(); fileSession = null; }
                return;
            }
            if (incomingImage?.IsCompleted == true && imageUpdate is { } imageMessage)
            {
                var bytes = incomingImage.GetAwaiter().GetResult();
                if (imageWriteDeadline == 0) imageWriteDeadline = RuntimeCompat.TickCount64 + 2000;
                if (bytes is null || !versions.CanApply(imageMessage.Revision, revision) ||
                    RuntimeCompat.TickCount64 > imageWriteDeadline) { ClearFile(); return; }
                var token = receivingImage;
                if (token is null || token.IsCancellationRequested) { ClearFile(); return; }
                var dib = ClipboardNative.ImageDib(bytes);
                if (dib is null) { ClearFile(); return; }
                var imageSession = ClipboardImageContent.CreateFileSession(FileClipboardOffer.Parse(imageMessage.Clipboard!), bytes);
                var imageEpoch = epoch;
                var dataObject = new VirtualFileClipboard(imageSession,
                    () => !disposed && permitted && permittedEpoch == imageEpoch && !token.IsCancellationRequested &&
                        ReferenceEquals(fileSession, imageSession) && DesktopNative.IsDefaultDesktop(), bytes, dib);
                fileSession = imageSession;
                if (CanAccess() && !token.IsCancellationRequested && ClipboardNative.GetClipboardSequenceNumber() == revision && dataObject.Install())
                {
                    virtualFile = dataObject; versions.Imported(ClipboardNative.GetClipboardSequenceNumber());
                    incomingImage = null; imageUpdate = null; imageWriteDeadline = 0;
                }
                else { imageSession.Dispose(); fileSession = null; }
                return;
            }
            if (pending is not { } update) return;
            if (!versions.CanApply(update.Revision, revision) || RuntimeCompat.TickCount64 > applyDeadline) { pending = null; return; }
            if (!CanAccess()) return;
            if (ClipboardNative.Write(window.Handle, update.Clipboard!, revision, CanAccess, out var written))
            { sending?.Cancel(); source = null; sourceOffer = null; versions.Imported(written); pending = null; }
        }
        // WM_CLIPBOARDUPDATE is sent, and OLE clipboard calls inside a tick can pump messages,
        // so our own write can wake this thread mid-tick. Queue one more pass instead of nesting.
        var ticking = false; var again = false;
        void Wakeup()
        {
            if (ticking) { again = true; return; }
            ticking = true;
            try { do { again = false; Tick(); } while (again && !disposed); }
            finally { ticking = false; }
            retry.Enabled = !disposed && ClipboardWake.NeedsRetry(enabled, available == true && CanAccess(),
                versions.Observed != ClipboardNative.GetClipboardSequenceNumber(), pending is not null, pendingFile is not null,
                imageUpdate is not null || incomingImage is not null, yield);
        }
        window.Wake = Wakeup;
        idle.Tick += (_, _) => Wakeup();
        retry.Tick += (_, _) => Wakeup();
        var listening = AddClipboardFormatListener(window.Handle);
        var sessions = WTSRegisterSessionNotification(window.Handle, 0); // NOTIFY_FOR_THIS_SESSION
        if (!listening) idle.Interval = 200; // the previous polling cadence
        Volatile.Write(ref wakeWindow, window.Handle);
        idle.Start();
        Wakeup();
        try { Application.Run(context); }
        finally
        {
            Volatile.Write(ref wakeWindow, IntPtr.Zero);
            if (listening) RemoveClipboardFormatListener(window.Handle);
            if (sessions) WTSUnRegisterSessionNotification(window.Handle);
            ClearFile(true); window.DestroyHandle();
        }
    }

    private sealed class ClipboardWindow : NativeWindow
    {
        public const uint WakeMessage = 0x8001; // WM_APP + 1
        public Action? Wake { get; set; }
        protected override void WndProc(ref Message message)
        {
            // WM_CLIPBOARDUPDATE, a posted command, or WM_WTSSESSION_CHANGE (lock/unlock).
            if (message.Msg is 0x031D or (int)WakeMessage or 0x02B1) Wake?.Invoke();
            base.WndProc(ref message);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);
    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
}
