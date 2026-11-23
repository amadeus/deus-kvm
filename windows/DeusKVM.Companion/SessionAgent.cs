using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using DeusKVM.Companion.Core;
using Microsoft.Win32.SafeHandles;

namespace DeusKVM.Companion;

/// <summary>
/// `--session-agent`: the service's hands in the console session. It runs as SYSTEM whenever
/// there is a console session (the login screen before anyone signs in, while signed in, while
/// locked) and does everything that needs the screen: it receives the Mac's input stream and
/// injects keys, pointer and scroll on whichever desktop has input (the user's desktop, behind
/// elevated windows, the login and lock screens and UAC prompts), watches the configured edge for
/// the return to the Mac on all of them, places the cursor on entry, hides it while the Mac is in
/// control, and reports displays and desktop state. The clipboard is the only thing it does not
/// do (<see cref="ClipboardWorker"/>, which must run as the user).
///
/// Threads: the window thread (this context: the cursor surface, raw input for the PC's own mouse,
/// the one-second refresh) stays on the Default desktop it started on. Injection and the edge test
/// run on the stream's receive thread and key repeats on the repeat thread; both attach to the
/// input desktop themselves (<see cref="InputDesktop"/>). Work the window thread asks for on the
/// input desktop (cursor placement, releasing held input) runs on <see cref="InputDesktopThread"/>.
/// </summary>
internal sealed class SessionAgent : ApplicationContext
{
    private readonly NamedPipeClientStream pipe;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Channel<DesktopMessage> output = Channel.CreateBounded<DesktopMessage>(128);
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly RawWindow window;
    private readonly CursorHider cursor = new();
    private readonly InputDesktopThread desktopOps = new();
    private readonly int parent;
    private readonly HandoffSession handoff = new();
    private readonly DesktopRecoveryPoll recoveryPoll = new();
    private readonly InputArrivalStats arrivals = new();
    private NetworkInputClient? network;
    private SynchronizationContext? context;
    private string networkStatus = "Network pointer: no offer";
    // Stream state: held buttons and keys, the keyboard and media-key state, the repeat.
    private readonly object networkGate = new();
    private uint heldNetworkButtons; // MOUSEEVENTF up flags still owed
    private readonly KeyboardState keyboard = new();
    private readonly ConsumerState media = new();
    private readonly KeyRepeater repeater;
    private int repeatSerial;
    // record received → SendInput returned, per ten-second window.
    private readonly LatencySamples inject = new();
    private long lastInjectReport;
    // Edge test state, on the stream's thread.
    private int selectedMotion, otherMotion, heldMotion, clippedMotion, leaveRequests, rejectedLeaves;
    private long lastEdgeReport, lastRejectionLog, lastLeaveSent, firstLeave;
    private byte lastLeaveSwitch;
    private const long LeaveIntervalMs = 100, LinkLeaveAfterMs = 150;
    private DesktopNative.Point? lastPoint;
    // Recent edge/desktop decisions for a problem report requested from the Mac; memory only,
    // owned by the window thread (other threads add through Note).
    private readonly DiagnosticHistory history = new();
    // Replaced on the window thread, read by the stream's thread.
    private volatile MonitorInfo[] monitors = [];
    private volatile EdgeConfiguration? config;
    private byte blind = 4;
    private bool started;

    public SessionAgent(string name, int parent)
    {
        repeater = new KeyRepeater(Repeat);
        this.parent = parent;
        pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        window = new RawWindow(this);
        timer.Tick += (_, _) => Refresh();
        Application.Idle += Start;
    }

    private async void Start(object? sender, EventArgs args)
    {
        if (started) return;
        started = true;
        Application.Idle -= Start;
        try
        {
            await pipe.ConnectAsync(10000, shutdown.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != parent)
                throw new InvalidDataException("Unexpected agent pipe server");
            var context = this.context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            var connection = Task.Run(async () =>
            {
                try { await DesktopPipe.Pump(pipe, output.Reader, message => context.Post(_ => Handle(message), null), shutdown.Token).ConfigureAwait(false); }
                finally { Environment.Exit(0); }
            });
            if (!DesktopNative.RegisterRawInputDevices([
                new() { Page = 1, Usage = 2, Flags = 0x2100, Target = window.Handle }
            ], 1, (uint)Marshal.SizeOf<DesktopNative.RawDevice>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Refresh();
            timer.Start();
            // Pipe EOF ends this process independently of the message loop.
            await connection.ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or Win32Exception or System.Text.Json.JsonException) { }
        finally { Environment.Exit(0); }
    }

    private void Send(DesktopMessage message)
    {
        if (!output.Writer.TryWrite(message)) pipe.Dispose();
    }

    // A history line from another thread; the history belongs to the window thread.
    private void Note(string line) => context?.Post(_ => history.Add(line), null);

    private void Refresh()
    {
        var state = DesktopNative.DesktopState();
        var next = DesktopNative.Monitors();
        if (next.Length > 0 && !next.SequenceEqual(monitors))
        {
            var change = $"layout: {Describe(monitors)} -> {Describe(next)}";
            history.Add(change);
            if (monitors.Length > 0) ThreadPool.QueueUserWorkItem(_ => EdgeDiagnostics.Write(change));
            cursor.Reveal();
            handoff.Exit(); monitors = next;
            Send(new DesktopMessage("screens", Monitors: next));
        }
        UpdateDesktopState(state);
        ReportEdgeHealth();
        ReportInject();
    }

    // Edge return works on every desktop the agent can attach to; only an input desktop it cannot
    // open at all (none, or access denied) leaves the Mac's hotkey as the way back.
    private void UpdateDesktopState((byte Blind, byte Desktop) state)
    {
        var next = state.Blind;
        if (next != blind) history.Add($"desktop: blind={next} desktop={state.Desktop}");
        blind = next;
        handoff.SetAvailable(blind == 0);
        if (blind != 0) cursor.Reveal(); else cursor.Follow(state.Desktop);
        var detail = blind != 0 ? "Input desktop unavailable; use the Mac hotkey"
            : state.Desktop == 0 ? "Windows edge return ready" : "Windows edge return ready (secure desktop)";
        Send(new DesktopMessage("state", Blind: blind, Desktop: state.Desktop, MousePresent: true, Detail: detail, Network: networkStatus));
    }

    private void Handle(DesktopMessage message)
    {
        switch (message.Kind)
        {
            case "config":
                cursor.Reveal();
                handoff.Exit();
                config = message.Config is { Edge: < 4 } ? message.Config : null;
                history.Add($"config: edge={config?.Edge} monitor={Describe(monitors.FirstOrDefault(item => item.Id == config?.Monitor))}");
                break;
            case "reset": cursor.Reveal(); handoff.Exit(); StopNetwork("no offer"); history.Add("reset"); break;
            case "exit":
                if (handoff.Accept(message.SwitchId))
                {
                    handoff.Exit();
                    ReleaseNetworkInput();
                    if (blind == 0) cursor.Hide();
                    lock (networkGate) { history.Add($"exit: switch={message.SwitchId} network={arrivals.Summary}"); arrivals.Reset(); }
                }
                break;
            case "input-offer": StartNetwork(message.Report); break;
            case "input-revoke": StopNetwork("no offer"); break;
            case "resume":
                // Reattach to current Mac ownership after login/agent startup.
                // Never replay entry placement over the user's current cursor.
                if (config?.Edge == message.Edge && monitors.Any(item => item.Id == config.Monitor))
                {
                    handoff.Enter(message.SwitchId);
                    cursor.Reveal();
                    UpdateDesktopState(DesktopNative.DesktopState());
                    history.Add($"resume: switch={message.SwitchId}");
                }
                break;
            case "report-request":
                Send(new DesktopMessage("report", Epoch: message.Epoch, Report: BuildReport()));
                break;
            case "enter":
                Enter(message);
                break;
        }
    }

    // Places the cursor at the entry point on whichever desktop has input (on the input-desktop
    // thread: this thread cannot reach a secure desktop), then acknowledges from here.
    private void Enter(DesktopMessage message)
    {
        cursor.Reveal();
        handoff.Exit();
        var current = config;
        var monitor = monitors.FirstOrDefault(item => item.Id == current?.Monitor);
        if (monitor is null || current is null || message.Edge != current.Edge)
        {
            Acknowledge(message, monitor, false, new DesktopNative.Point());
            return;
        }
        // The Mac owns this handoff even if placement fails; edge observation resumes on return.
        handoff.Enter(message.SwitchId);
        var point = message.Center ? monitor.Center : monitor.Entry(message.Edge, message.Fraction);
        desktopOps.Post(() =>
        {
            var position = new DesktopNative.Point();
            var ok = DesktopNative.SetCursorPos(point.X, point.Y) && DesktopNative.GetCursorPos(out position) &&
                Math.Abs(position.X - point.X) <= 1 && Math.Abs(position.Y - point.Y) <= 1;
            context?.Post(_ => Acknowledge(message, monitor, ok, position), null);
        });
    }

    private void Acknowledge(DesktopMessage message, MonitorInfo? monitor, bool ok, DesktopNative.Point position)
    {
        Send(new DesktopMessage("ack", SwitchId: message.SwitchId, Ok: ok, X: position.X, Y: position.Y, Blind: ok ? blind : (byte)4));
        lastPoint = null;
        history.Add($"enter: switch={message.SwitchId} ok={ok} center={message.Center} " +
            $"monitor={Describe(monitor)} x={Fraction(monitor, position.X):0.000}");
    }

    private void RawInput(IntPtr handle)
    {
        // Probe immediately on first movement, but do not re-check the desktop and send status
        // on every mouse packet while recovery is pending.
        if (recoveryPoll.ShouldRefresh(blind != 0, handoff.Active is not null, RuntimeCompat.TickCount64)) Refresh();
        if (!cursor.Hidden && (!handoff.CanReturn || config is null)) return;
        var headerSize = (uint)(8 + IntPtr.Size * 2);
        uint size = 0;
        if (DesktopNative.GetRawInputData(handle, 0x10000003, IntPtr.Zero, ref size, headerSize) == uint.MaxValue ||
            size < headerSize + 24 || size > 4096) return;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (DesktopNative.GetRawInputData(handle, 0x10000003, buffer, ref size, headerSize) != size ||
                Marshal.ReadInt32(buffer) != 0) return;
            var mouse = buffer + (int)headerSize;
            if (Marshal.ReadInt32(mouse, 20) == unchecked((int)DesktopNative.InjectTag)) return; // our own injected step
            // Anything else is the PC's own mouse: it only reveals the cursor. The Mac's motion
            // is observed as it is injected (ObserveMotion), never through Raw Input.
            var dx = Marshal.ReadInt32(mouse, 12);
            var dy = Marshal.ReadInt32(mouse, 16);
            var buttons = Marshal.ReadInt16(mouse, 4);
            if (dx != 0 || dy != 0) otherMotion++;
            if (dx != 0 || dy != 0 || buttons != 0) cursor.Reveal();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // The edge test for one pointer step the Mac produced, just injected, on the stream's thread
    // (attached to the input desktop, so the cursor position is that desktop's).
    private void ObserveMotion(int dx, int dy)
    {
        var current = config;
        if (handoff.Active is not { } id || current is null || !handoff.CanReturn || (dx == 0 && dy == 0) ||
            !DesktopNative.GetCursorPos(out var point)) return;
        var layout = monitors;
        var monitor = layout.FirstOrDefault(item => item.Id == current.Monitor);
        if (monitor is null) return;
        selectedMotion++;
        var held = new[] { 1, 2, 4, 5, 6 }.Any(key => DesktopNative.GetAsyncKeyState(key) < 0);
        var clipped = !DesktopNative.GetClipCursor(out var clip) || clip.Left > monitor.X || clip.Top > monitor.Y ||
            clip.Right < monitor.X + monitor.W || clip.Bottom < monitor.Y + monitor.H;
        if (held) heldMotion++;
        if (clipped) clippedMotion++;
        var previous = lastPoint;
        lastPoint = point;
        if (!EdgeDetector.Observe(monitor, layout, current, point.X, point.Y, dx, dy, held, clipped)) return;
        var live = DesktopNative.MonitorAt(point.X, point.Y);
        var verdict = EdgeDetector.Confirm(monitor, live, layout, current, point.X, point.Y, dx, dy);
        var detail = LeaveDetail(verdict, monitor, live, point, previous, dx, dy);
        if (verdict == EdgeVerdict.Leave)
        {
            leaveRequests++;
            RequestLeave(id, current.Edge, monitor.Fraction(current.Edge, point.X, point.Y), detail);
        }
        else RejectLeave(detail);
    }

    // Asks the Mac to take the pointer back: on the network stream's acknowledgements (the next
    // one is a pointer step away), and over the control link only while the stream is down or once
    // the Mac has not taken it back within 150 ms. The control link gets at most one per 100 ms: one
    // per step (~120/s) outran a briefly slow link, and the backlog delayed the return by seconds.
    // The Mac acts on the first that arrives and ignores the rest.
    private void RequestLeave(byte id, byte edge, ushort fraction, string detail)
    {
        var now = RuntimeCompat.TickCount64;
        if (id != lastLeaveSwitch) { lastLeaveSwitch = id; firstLeave = now; lastLeaveSent = long.MinValue / 2; Note(detail); }
        var network = this.network?.Leave(InputRecord.LeaveFlags(id, edge, fraction)) == true;
        if (network && now - firstLeave < LinkLeaveAfterMs) return;
        if (now - lastLeaveSent < LeaveIntervalMs) return;
        lastLeaveSent = now;
        Note(network ? "leave: not taken over the stream yet, also over the control link" : "leave: over the control link (stream down)");
        Send(new DesktopMessage("leave", SwitchId: id, Edge: edge, Fraction: fraction));
    }

    // A layout that disagrees with Windows' live monitor is re-read, and the durable log gets a
    // line, at most once per second however long the cursor pushes outward.
    private void RejectLeave(string detail)
    {
        rejectedLeaves++;
        var now = RuntimeCompat.TickCount64;
        if (now - lastRejectionLog < 1000) return;
        lastRejectionLog = now;
        Note(detail);
        ThreadPool.QueueUserWorkItem(_ => EdgeDiagnostics.Write(detail));
        context?.Post(_ => Refresh(), null);
    }

    private void StartNetwork(string? json)
    {
        StopNetwork("connecting");
        FileNetworkOffer? offer = null;
        try { if (json is not null) offer = System.Text.Json.JsonSerializer.Deserialize<FileNetworkOffer>(json, FileClipboardOffer.WireJson); }
        catch (System.Text.Json.JsonException) { }
        if (offer is not { Valid: true } || context is not { } post) { SetNetworkStatus("invalid offer"); return; }
        history.Add($"network: offer port={offer.Port} hosts={offer.Hosts.Length}");
        network = new NetworkInputClient(offer, Inject,
            text => post.Post(_ => { if (text.StartsWith("closed", StringComparison.Ordinal)) ReleaseNetworkInput(); SetNetworkStatus(text); }, null));
    }

    private void StopNetwork(string reason)
    {
        network?.Dispose(); network = null;
        ReleaseNetworkInput();
        SetNetworkStatus(reason);
    }

    private void SetNetworkStatus(string text)
    {
        var next = "Network pointer: " + text;
        if (next == networkStatus) return;
        networkStatus = next;
        history.Add(next);
        UpdateDesktopState(DesktopNative.DesktopState());
    }

    // One record from the stream, on its receiving thread, while the Mac owns the handoff: injected
    // at once on the input desktop. Returns whether the PC can take input right now; false rides on
    // the next acknowledgement so the Mac stops sending within a round trip.
    private bool Inject(InputEvent record)
    {
        var arrived = Stopwatch.GetTimestamp();
        if (record.Type == InputRecord.Ping) return InputDesktop.Attach();
        if (record.IsKeyboard || record.IsConsumer || record.IsScroll) return InjectKeys(record, arrived);
        if (!record.IsMotion && !record.IsButton) return true;
        lock (networkGate)
        {
            arrivals.Observe(DesktopNative.Milliseconds());
            if (handoff.Active is null) { arrivals.Discarded++; return InputDesktop.Attach(); }
            if (!InputDesktop.Attach()) return Drop();
            bool ok;
            if (record.IsButton)
            {
                var (down, up) = DesktopNative.ButtonFlags(record.Button);
                if (record.Down) heldNetworkButtons |= up; else heldNetworkButtons &= ~up;
                ok = DesktopNative.InjectMouse(0, 0, record.Down ? down : up);
            }
            else ok = DesktopNative.InjectMouse(record.Dx, record.Dy, DesktopNative.MouseMove);
            if (!ok) return Drop();
            Injected(arrived);
        }
        if (record.IsMotion) ObserveMotion(record.Dx, record.Dy);
        return true;
    }

    // A keyboard state, media key or scroll step. Keys are state: the difference from the last
    // record is injected, so a lost moment cannot leave a key held. Ctrl+Alt+Delete goes to the
    // service (only a service may raise that screen).
    private bool InjectKeys(InputEvent record, long arrived)
    {
        lock (networkGate)
        {
            if (handoff.Active is null) { arrivals.Discarded++; return InputDesktop.Attach(); }
            if (!InputDesktop.Attach()) return Drop();
            if (record.IsScroll)
            {
                if (!DesktopNative.InjectWheel(record.Wheel * 120, record.Pan * 120)) return Drop();
                Injected(arrived);
                return true;
            }
            var actions = record.IsKeyboard ? keyboard.Apply(record.Modifiers, record.Keys ?? []) : media.Apply(record.Usage);
            var ok = true;
            foreach (var action in actions)
            {
                if (action.Kind == KeyActionKind.Sas) { Send(new DesktopMessage("sas")); Note("Ctrl+Alt+Delete: asked the service"); }
                else ok &= DesktopNative.InjectKey(action.Key, action.Kind == KeyActionKind.Release);
            }
            if (record.IsKeyboard) ArmRepeat();
            if (!ok) return Drop();
            Injected(arrived);
            return true;
        }
    }

    private void Injected(long arrived)
    {
        arrivals.Injected++;
        inject.Add(LatencySamples.Micros(Stopwatch.GetTimestamp() - arrived));
    }

    private bool Drop() { arrivals.Discarded++; return false; }

    // Restarts the repeat delay when the newest held key changes (under networkGate).
    private void ArmRepeat()
    {
        if (keyboard.RepeatSerial == repeatSerial) return;
        repeatSerial = keyboard.RepeatSerial;
        if (keyboard.Repeating is not { } key) { repeater.Stop(); return; }
        var (delay, period) = DesktopNative.KeyRepeat();
        repeater.Start(key, repeatSerial, delay, period);
    }

    // On the repeat thread: one more key-down for the key still held, if it is.
    private void Repeat(KeyStroke key, int serial)
    {
        lock (networkGate)
        {
            if (serial != repeatSerial || keyboard.Repeating != key || handoff.Active is null) return;
            if (InputDesktop.Attach()) DesktopNative.InjectKey(key, false);
        }
    }

    // A button or key pressed from the stream is released when the stream, the handoff or the
    // agent ends, so Windows never keeps one that the Mac can no longer release.
    private void ReleaseNetworkInput()
    {
        List<KeyAction> keys;
        uint buttons;
        lock (networkGate)
        {
            keys = keyboard.ReleaseAll().Concat(media.ReleaseAll()).ToList();
            ArmRepeat();
            buttons = heldNetworkButtons;
            heldNetworkButtons = 0;
        }
        if (buttons == 0 && keys.Count == 0) return;
        desktopOps.Post(() =>
        {
            if (buttons != 0) DesktopNative.InjectMouse(0, 0, buttons);
            foreach (var key in keys) DesktopNative.InjectKey(key.Key, up: true);
        });
    }

    // Injection latency in the last ten seconds, if there was any (window thread).
    private void ReportInject()
    {
        var now = RuntimeCompat.TickCount64;
        if (now - lastInjectReport < 10000 || inject.Count == 0) return;
        lastInjectReport = now;
        var line = $"input inject 10s: [{inject.Take()}]";
        history.Add(line);
        ThreadPool.QueueUserWorkItem(_ => EdgeDiagnostics.Write(line));
    }

    private static string LeaveDetail(EdgeVerdict verdict, MonitorInfo cached, MonitorInfo? live, DesktopNative.Point point,
        DesktopNative.Point? previous, int dx, int dy)
    {
        var physical = DesktopNative.GetPhysicalCursorPos(out var real) ?
            real.X == point.X && real.Y == point.Y ? "same" : $"x={Fraction(live ?? cached, real.X):0.000}" : "unavailable";
        var moved = previous is { } last ? Math.Abs(point.X - last.X) + Math.Abs(point.Y - last.Y) : -1;
        return $"leave {verdict}: desktop={InputDesktop.Current} cached={Describe(cached)} live={Describe(live)} x={Fraction(cached, point.X):0.000} " +
            $"physical={physical} awareness={DesktopNative.DpiAwareness()} movedPx={moved} raw={dx},{dy}";
    }

    private static double Fraction(MonitorInfo? monitor, int x) => monitor is null || monitor.W <= 0 ? -1 : (x - monitor.X) / (double)monitor.W;
    private static string Describe(MonitorInfo? monitor) => monitor is null ? "none" : $"{monitor.W}x{monitor.H}@{monitor.Dpi}";
    private static string Describe(IReadOnlyList<MonitorInfo> list) => list.Count == 0 ? "none" : string.Join(",", list.Select(Describe));

    // Answers a problem report requested from the Mac. Sizes, fractions and counts only.
    private string BuildReport()
    {
        var configured = monitors.FirstOrDefault(item => item.Id == config?.Monitor);
        var state = DesktopNative.DesktopState();
        var cursorLine = state.Desktop != 0 ? "cursor: on a secure desktop" : DesktopNative.GetCursorPos(out var point)
            ? $"cursor: x={Fraction(configured, point.X):0.000} live={Describe(DesktopNative.MonitorAt(point.X, point.Y))} " +
              $"physical={(DesktopNative.GetPhysicalCursorPos(out var real) ? real.X == point.X && real.Y == point.Y ? "same" : "differs" : "unavailable")}"
            : "cursor: unavailable";
        var lines = new List<string>
        {
            $"session agent: identity={ProcessIdentity.Name} session={ProcessIdentity.Session} awareness={DesktopNative.DpiAwareness()} " +
                $"desktop={state.Desktop} blind={blind} handoff={handoff.Active is not null} hidden={cursor.Hidden} edge={config?.Edge} configured={Describe(configured)}",
            $"{networkStatus.ToLowerInvariant()} {arrivals.Summary}",
            $"cached layout: {Describe(monitors)}",
            $"live layout: {Describe(DesktopNative.Monitors())}",
            cursorLine,
            $"since last health line: selected={selectedMotion} other={otherMotion} held={heldMotion} clipped={clippedMotion} " +
                $"leave={leaveRequests} rejected={rejectedLeaves}",
            $"history ({history.Count}, oldest first):"
        };
        lines.AddRange(history.Lines().Select(line => "  " + line));
        return string.Join("\n", lines);
    }

    private void ReportEdgeHealth()
    {
        var now = RuntimeCompat.TickCount64;
        if (now - lastEdgeReport < 5000) return;
        lastEdgeReport = now;
        if (EdgeHealth.ShouldWrite(handoff.Active is not null, leaveRequests, rejectedLeaves))
        {
            var value = $"edge health: active={handoff.Active is not null} blind={blind} configured={config is not null} " +
                $"selected={selectedMotion} other={otherMotion} held={heldMotion} clipped={clippedMotion} leave={leaveRequests} rejected={rejectedLeaves}";
            ThreadPool.QueueUserWorkItem(_ => EdgeDiagnostics.Write(value));
        }
        selectedMotion = otherMotion = heldMotion = clippedMotion = leaveRequests = rejectedLeaves = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.Idle -= Start;
            network?.Dispose();
            ReleaseNetworkInput();
            desktopOps.Dispose();
            repeater.Dispose();
            cursor.Dispose();
            shutdown.Cancel(); timer.Dispose(); pipe.Dispose(); window.DestroyHandle(); shutdown.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class RawWindow : NativeWindow
    {
        private readonly SessionAgent owner;
        public RawWindow(SessionAgent owner)
        {
            this.owner = owner;
            CreateHandle(new CreateParams { Caption = "DeusKVM session agent", Parent = new IntPtr(-3) });
        }
        protected override void WndProc(ref System.Windows.Forms.Message message)
        {
            if (message.Msg == 0xFF) owner.RawInput(message.LParam);
            base.WndProc(ref message);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
