using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

// Clipboard/OLE rendering can block in another app. One STA owns the clipboard; the
// pipe thread can independently revoke a selected source and close its network reader.
internal sealed class DesktopClipboard : IDisposable
{
    private readonly ConcurrentQueue<DesktopMessage> commands = new();
    private readonly object selectionGate = new();
    private RemoteClipboardLease? selection;
    private DesktopMessage? incomingSnapshot;
    private volatile bool disposed, permitted, snapshotsAllowed;
    private volatile uint permittedEpoch;
    private volatile FileClipboardSession? remoteSession;
    private volatile CancellationTokenSource? sending;
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
        if (message.Kind == "clipboard-select")
        {
            RemoteClipboardLease? old;
            lock (selectionGate)
            {
                old = selection;
                if (old is not null && (message.Ownership < old.Ownership ||
                    message.Ownership == old.Ownership && old.Epoch != 0 && message.Epoch == 0)) return;
                selection = new RemoteClipboardLease(message.Source, message.Ownership, message.Epoch);
            }
            old?.Revoke(); remoteSession?.Dispose(); sending?.Cancel();
            permitted = false; snapshotsAllowed = true;
            Interlocked.Exchange(ref incomingSnapshot, null);
            while (commands.TryDequeue(out _)) { }
            commands.Enqueue(message); Wake(); return;
        }
        var selected = Volatile.Read(ref selection);
        if (selected?.Matches(message.Source, message.Ownership, message.Epoch) != true) return;
        if (message.Kind == "clipboard-snapshot")
        {
            if (!snapshotsAllowed) return;
            // Keep only the latest revision while OLE is busy. Lifecycle messages use the
            // separate queue, so coalescing can never discard an ownership transition.
            Interlocked.Exchange(ref incomingSnapshot, message with { Revision = ClipboardNative.GetClipboardSequenceNumber() });
            Wake(); return;
        }
        if (message.Kind == "clipboard-local")
        {
            snapshotsAllowed = false;
            Interlocked.Exchange(ref incomingSnapshot, null);
            commands.Enqueue(message); Wake(); return;
        }
        if (message.Kind == "clipboard-start")
        {
            sending?.Cancel();
            permitted = false; permittedEpoch = message.Epoch; permitted = message.Ok;
            commands.Enqueue(message);
        }
        else if (message.Kind is "clipboard-yield" or "clipboard-file-send" && commands.Count < 16)
            commands.Enqueue(message);
        // Typed snapshots are the only incoming installation path. Legacy apply/file-offer
        // notifications are deliberately not accepted, even for the current epoch.
        Wake();
    }

    public void Dispose()
    {
        disposed = true; Volatile.Read(ref selection)?.Revoke();
        remoteSession?.Dispose(); sending?.Cancel();
        while (commands.TryDequeue(out _)) { }
        Interlocked.Exchange(ref incomingSnapshot, null); Wake();
    }

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
        using var idle = new System.Windows.Forms.Timer { Interval = 1000 };
        using var retry = new System.Windows.Forms.Timer { Interval = 200 };
        bool? available = null;
        var enabled = false; var priming = true; var yield = false;
        uint epoch = 0;
        var versions = new ClipboardRevision();
        RemoteClipboardLease? lease = null;
        RemoteClipboard? remote = null;
        RemoteClipboardSelection? selected = null;
        DesktopMessage? snapshotMessage = null;
        ClipboardFileSource? source = null;
        FileClipboardOffer? sourceOffer = null;
        var waiting = false;

        bool CanAccess() => !disposed && permitted && permittedEpoch == epoch &&
            lease?.Valid == true && DesktopNative.IsDefaultDesktop();
        void Publish(DesktopMessage message)
        {
            if (lease is not null) message = message with { Source = lease.Source, Ownership = lease.Ownership };
            send(message);
        }
        void ClearRemote()
        {
            remoteSession?.Dispose(); remoteSession = null;
            if (remote?.Revoke() == true) versions.Imported(ClipboardNative.GetClipboardSequenceNumber());
            remote = null;
        }
        void ClearSource() { sending?.Cancel(); source = null; sourceOffer = null; }
        void ObserveLocal()
        {
            var revision = ClipboardNative.GetClipboardSequenceNumber();
            if (remote?.IsCurrent == true) versions.Imported(revision);
            if (versions.Observed == revision) return;
            ClearRemote(); ClearSource();
            if (!ClipboardNative.Read(window.Handle, out revision, out var bytes, out var file) || !CanAccess()) return;
            versions.Capture(revision);
            Publish(new DesktopMessage("clipboard-copy", Epoch: epoch, Revision: revision, Clipboard: bytes, Ok: !priming));
            source = file;
            sourceOffer = file is null ? null : new FileClipboardOffer(epoch, revision, file.Name, file.Size, ContentType: file.ContentType);
            if (sourceOffer is not null)
                Publish(new DesktopMessage("clipboard-file-copy", Epoch: epoch, Revision: revision,
                    Clipboard: JsonSerializer.SerializeToUtf8Bytes(sourceOffer), Ok: !priming));
            priming = false;
        }
        void SendFile(DesktopMessage message)
        {
            if (source is not { } current || sourceOffer is not { } offered || message.Clipboard is not { } payload || lease is not { } scope) return;
            var request = FileClipboardOffer.Parse(payload);
            if (request.Epoch != offered.Epoch || request.Sequence != offered.Sequence || request.Size != offered.Size ||
                request.Name != offered.Name || request.ContentType != offered.ContentType) return;
            sending?.Cancel(); var token = new CancellationTokenSource(); sending = token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await NetworkFileSender.Send(request, (offset, count) =>
                    {
                        bool Valid() => !disposed && scope.Valid && permitted && permittedEpoch == offered.Epoch &&
                            ClipboardNative.GetClipboardSequenceNumber() == offered.Sequence && DesktopNative.IsDefaultDesktop();
                        if (!Valid()) return null;
                        var block = current.Read(offset, count); return Valid() ? block : null;
                    }, token.Token);
                }
                catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or OperationCanceledException or System.Security.Cryptography.CryptographicException)
                { FilePasteDiagnostics.Write($"send-ended hr=0x{error.HResult:X8}"); }
            });
        }
        void Tick()
        {
            if (disposed) { context.ExitThread(); return; }
            var accessible = DesktopNative.IsDefaultDesktop();
            if (available != accessible)
            {
                ClearRemote(); ClearSource(); available = accessible; enabled = false; versions.Reset();
                Publish(new DesktopMessage("clipboard-availability", Ok: accessible));
            }
            while (commands.TryDequeue(out var message))
            {
                if (message.Kind == "clipboard-select")
                {
                    var current = Volatile.Read(ref selection);
                    if (current?.Matches(message.Source, message.Ownership, message.Epoch) != true) continue;
                    ClearRemote(); ClearSource(); lease = current; selected = null; snapshotMessage = null;
                    epoch = message.Epoch; enabled = false; priming = true; waiting = message.Source != 0;
                    versions.Reset(); yield = false;
                }
                else if (lease?.Matches(message.Source, message.Ownership, message.Epoch) == true)
                {
                    if (message.Kind == "clipboard-start")
                    {
                        enabled = message.Ok && accessible; priming = true; versions.Reset(); yield = false;
                        if (!enabled) { ClearRemote(); ClearSource(); }
                    }
                    else if (message.Kind == "clipboard-local") { snapshotMessage = null; waiting = false; }
                    else if (enabled && message.Kind == "clipboard-yield") yield = true;
                    else if (enabled && message.Kind == "clipboard-file-send") SendFile(message);
                }
            }
            var incoming = Interlocked.Exchange(ref incomingSnapshot, null);
            if (snapshotsAllowed && incoming is not null && lease?.Matches(incoming.Source, incoming.Ownership, incoming.Epoch) == true)
                snapshotMessage = incoming;
            if (lease is not { Valid: true } scope || !accessible) return;
            if (selected is null)
            {
                if (!ClipboardNative.ClearImported(window.Handle, () => scope.Valid, out var baseline)) return;
                selected = new RemoteClipboardSelection(baseline);
            }
            if (enabled && CanAccess()) ObserveLocal();
            if (snapshotsAllowed && snapshotMessage is { } update)
            {
                ClipboardSnapshot snapshot;
                try { snapshot = ClipboardSnapshot.Parse(update.Clipboard ?? []); }
                catch (InvalidDataException)
                {
                    snapshotMessage = null;
                    Publish(update with { Kind = "clipboard-ready", Clipboard = null, Ok = false });
                    return;
                }
                if (snapshot.Epoch != scope.Epoch || snapshot.Generation != (ulong)scope.Ownership)
                { snapshotMessage = null; return; }
                // Snapshot and ClipState replies can arrive in either order. A usable data
                // object must not acknowledge readiness until clipboard consent is active.
                if (snapshot.Kind is "text" or "file" or "image" && (!enabled || !CanAccess())) return;
                if (selected.CanInstall(snapshot.Revision, ClipboardNative.GetClipboardSequenceNumber(), remote?.IsCurrent == true, update.Revision))
                {
                    ClearRemote(); ClearSource();
                    if (!ClipboardNative.ClearImported(window.Handle, () => scope.Valid, out var revision)) return;
                    RemoteClipboard? next = null;
                    if (snapshot.Kind == "text") next = new RemoteClipboard(snapshot.Text!, () => scope.Valid && CanAccess());
                    else if (snapshot.File is { } offer)
                    {
                        var session = new FileClipboardSession(offer, FilePasteDiagnostics.Write);
                        remoteSession = session;
                        next = new RemoteClipboard(session, () => scope.Valid && CanAccess() && ReferenceEquals(remoteSession, session), scope.Token);
                    }
                    if (next is not null)
                    {
                        if (!scope.Valid || ClipboardNative.GetClipboardSequenceNumber() != revision)
                        { remoteSession?.Dispose(); remoteSession = null; return; }
                        if (!next.Install()) { remoteSession?.Dispose(); remoteSession = null; return; }
                        if (!scope.Valid) { next.Revoke(); return; }
                        remote = next;
                        revision = ClipboardNative.GetClipboardSequenceNumber();
                        versions.Imported(revision);
                    }
                }
                selected.Complete(snapshot.Revision, ClipboardNative.GetClipboardSequenceNumber());
                snapshotMessage = null;
                if (waiting) Publish(update with { Kind = "clipboard-ready", Clipboard = null, Ok = true });
                waiting = false;
            }
            if (!enabled || !CanAccess()) return;
            if (yield)
            {
                yield = false; Publish(new DesktopMessage("clipboard-yield", Epoch: epoch));
                if (sourceOffer is not null)
                    Publish(new DesktopMessage("clipboard-file-copy", Epoch: epoch, Revision: sourceOffer.Sequence,
                        Clipboard: JsonSerializer.SerializeToUtf8Bytes(sourceOffer), Ok: true));
            }
        }
        // OLE can pump messages during a tick. Coalesce wakeups instead of recursively mutating
        // the selected source while a clipboard operation is still in progress.
        var ticking = false; var again = false;
        void Wakeup()
        {
            if (ticking) { again = true; return; }
            ticking = true;
            try { do { again = false; Tick(); } while (again && !disposed); }
            finally { ticking = false; }
            retry.Enabled = !disposed && available == true && lease?.Valid == true &&
                (selected is null || waiting || snapshotMessage is not null || enabled && CanAccess() &&
                    (versions.Observed != ClipboardNative.GetClipboardSequenceNumber() || yield));
        }
        window.Wake = Wakeup;
        idle.Tick += (_, _) => Wakeup(); retry.Tick += (_, _) => Wakeup();
        var listening = AddClipboardFormatListener(window.Handle);
        var sessions = WTSRegisterSessionNotification(window.Handle, 0);
        if (!listening) idle.Interval = 200;
        Volatile.Write(ref wakeWindow, window.Handle);
        idle.Start(); Wakeup();
        try { Application.Run(context); }
        finally
        {
            Volatile.Write(ref wakeWindow, IntPtr.Zero);
            if (listening) RemoveClipboardFormatListener(window.Handle);
            if (sessions) WTSUnRegisterSessionNotification(window.Handle);
            ClearRemote(); ClearSource(); window.DestroyHandle();
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
