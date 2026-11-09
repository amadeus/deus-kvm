using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using DeusKVM.Companion.Core;
using Microsoft.Win32.SafeHandles;

namespace DeusKVM.Companion;

internal sealed class DesktopWorker : ApplicationContext
{
    private readonly NamedPipeClientStream pipe;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Channel<DesktopMessage> output = Channel.CreateBounded<DesktopMessage>(32);
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly RawWindow window;
    private DesktopClipboard? clipboard;
    private readonly InactiveCursor cursor = new();
    private readonly string address;
    private readonly int parent;
    private readonly HandoffSession handoff = new();
    private readonly DesktopRecoveryPoll recoveryPoll = new();
    private readonly MouseIdentityCache devices = new();
    // The Mac's network pointer stream: injected here, the only process in the user's session.
    private readonly InputArrivalStats arrivals = new();
    private NetworkInputClient? network;
    private SynchronizationContext? context;
    private string networkStatus = "Network pointer: no offer";
    private readonly object networkGate = new();
    private uint heldNetworkButtons; // MOUSEEVENTF up flags still owed; under networkGate
    private volatile bool defaultDesktop;
    // The input desktop is not the default one (UAC prompt, lock screen): injection is refused
    // there, so the Mac is told to use the Bluetooth mouse. Re-checked every 100 ms while
    // records arrive, on every record while blocked, and on each idle ping.
    private volatile bool injectBlocked;
    private double lastDesktopCheck;
    private const double DesktopCheckMs = 100;
    // Injected motion since the desktop thread last ran the edge test; one post at a time.
    private int pendingDx, pendingDy, observePosted;
    private int selectedMotion, otherMotion, heldMotion, clippedMotion, leaveRequests, rejectedLeaves;
    private long lastEdgeReport, lastRejectionLog;
    // Recent edge/desktop decisions for a problem report requested from the Mac; memory only.
    private readonly DiagnosticHistory history = new();
    private DesktopNative.Point? lastPoint;
    private MonitorInfo[] monitors = [];
    private EdgeConfiguration? config;
    private byte blind = 4;
    private bool started;

    public DesktopWorker(string name, int parent, string address)
    {
        this.parent = parent;
        this.address = address;
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
                throw new InvalidDataException("Unexpected desktop pipe server");
            var context = this.context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            clipboard = new DesktopClipboard(Send);
            var connection = Task.Run(async () =>
            {
                try
                {
                    await DesktopPipe.Pump(pipe, output.Reader,
                        message => { if (message.Kind.StartsWith("clipboard-", StringComparison.Ordinal)) clipboard?.Post(message);
                            else context.Post(_ => Handle(message), null); }, shutdown.Token).ConfigureAwait(false);
                }
                finally { Environment.Exit(0); }
            });
            if (!DesktopNative.RegisterRawInputDevices([
                new() { Page = 1, Usage = 2, Flags = 0x2100, Target = window.Handle }
            ], 1, (uint)Marshal.SizeOf<DesktopNative.RawDevice>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Refresh();
            timer.Start();
            // Pipe EOF must terminate this process independently of the desktop
            // message loop: it replaces cross-session job inheritance.
            await connection.ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or Win32Exception or System.Text.Json.JsonException) { }
        finally { Environment.Exit(0); }
    }

    private void Send(DesktopMessage message)
    {
        if (!output.Writer.TryWrite(message)) pipe.Dispose();
    }

    private void Refresh()
    {
        var state = DesktopNative.DesktopState();
        defaultDesktop = state.Desktop == 0;
        injectBlocked = !defaultDesktop;
        if (state.Desktop != 0)
        {
            UpdateDesktopState(state);
            return; // Secure-desktop enumeration must not invalidate the saved display/session.
        }
        var next = DesktopNative.Monitors();
        if (next.Length > 0 && !next.SequenceEqual(monitors))
        {
            var change = $"layout: {Describe(monitors)} -> {Describe(next)}";
            history.Add(change);
            if (monitors.Length > 0) ThreadPool.QueueUserWorkItem(_ => EdgeDiagnostics.Write(change));
            cursor.Reveal();
            handoff.Exit(); monitors = next;
            Send(new DesktopMessage("screens", Monitors: monitors));
        }
        uint count = 0;
        var size = (uint)Marshal.SizeOf<DesktopNative.DeviceEntry>();
        if (DesktopNative.GetRawInputDeviceList(null, ref count, size) != uint.MaxValue && count <= 1024)
        {
            var list = new DesktopNative.DeviceEntry[count];
            var read = DesktopNative.GetRawInputDeviceList(list, ref count, size);
            if (read != uint.MaxValue)
            {
                var present = list.Take((int)read).Where(item => item.Type == 0).Select(item => item.Handle).ToHashSet();
                devices.Refresh(present, device => DesktopNative.IsMacMouse(device, address));
            }
        }
        UpdateDesktopState(state);
        ReportEdgeHealth();
    }

    private void UpdateDesktopState((byte Blind, byte Desktop) state)
    {
        var mouse = devices.HasMatch;
        var accessible = state.Desktop == 0;
        var next = !accessible ? state.Blind : !mouse ? (byte)3 : (byte)0;
        if (next != blind) history.Add($"desktop: blind={next} desktop={state.Desktop} mouse={mouse}");
        blind = next;
        handoff.SetAvailable(blind == 0);
        if (blind != 0) cursor.Reveal();
        Send(new DesktopMessage("state", Blind: blind, Desktop: state.Desktop, MousePresent: mouse,
            Detail: !accessible ? "Secure desktop; use the Mac hotkey" : !mouse ?
                "Waiting for the active Mac's HID mouse" : "Windows edge return ready", Network: networkStatus));
    }

    private void Handle(DesktopMessage message)
    {
        if (message.Kind.StartsWith("clipboard-", StringComparison.Ordinal)) { clipboard?.Post(message); return; }
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
                    ReleaseNetworkButtons();
                    if (blind == 0) cursor.Hide();
                    lock (networkGate) { history.Add($"exit: switch={message.SwitchId} network={arrivals.Summary}"); arrivals.Reset(); }
                }
                break;
            case "input-offer": StartNetwork(message.Report); break;
            case "input-revoke": StopNetwork("no offer"); break;
            case "resume":
                // Reattach to current Mac ownership after login/worker startup.
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
                cursor.Reveal();
                handoff.Exit();
                var monitor = monitors.FirstOrDefault(item => item.Id == config?.Monitor);
                var ok = false;
                var position = new DesktopNative.Point();
                if (monitor is not null && message.Edge == config?.Edge)
                {
                    // The Mac already owns this handoff even when secure desktop
                    // prevents cursor placement. Resume edge observation on return.
                    handoff.Enter(message.SwitchId);
                    if (DesktopNative.IsDefaultDesktop())
                    {
                        var point = message.Center ? monitor.Center : monitor.Entry(message.Edge, message.Fraction);
                        ok = DesktopNative.SetCursorPos(point.X, point.Y) && DesktopNative.GetCursorPos(out position) &&
                            Math.Abs(position.X - point.X) <= 1 && Math.Abs(position.Y - point.Y) <= 1;
                    }
                }
                Send(new DesktopMessage("ack", SwitchId: message.SwitchId, Ok: ok,
                    X: position.X, Y: position.Y, Blind: ok ? blind : (byte)4));
                lastPoint = null;
                history.Add($"enter: switch={message.SwitchId} ok={ok} center={message.Center} " +
                    $"monitor={Describe(monitor)} x={Fraction(monitor, position.X):0.000}");
                break;
        }
    }

    private void RawInput(IntPtr handle)
    {
        // Probe immediately on first movement, but do not enumerate devices and
        // send Bluetooth status on every mouse packet while recovery is pending.
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
            var device = Marshal.ReadIntPtr(buffer, 8);
            var selected = devices.Resolve(device, candidate => DesktopNative.IsMacMouse(candidate, address));
            var mouse = buffer + (int)headerSize;
            if (Marshal.ReadInt32(mouse, 20) == unchecked((int)DesktopNative.InjectTag)) return; // our own injected step
            var dx = Marshal.ReadInt32(mouse, 12);
            var dy = Marshal.ReadInt32(mouse, 16);
            var buttons = Marshal.ReadInt16(mouse, 4);
            if (dx != 0 || dy != 0)
            {
                if (selected) selectedMotion++; else otherMotion++;
            }
            if (!selected)
            {
                if (dx != 0 || dy != 0 || buttons != 0) cursor.Reveal();
                return;
            }
            if ((Marshal.ReadInt16(mouse) & 1) != 0) return; // absolute motion carries no edge direction
            ObserveMotion(dx, dy);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }


    // The edge test for one pointer step the Mac produced, whether it came as a Raw Input packet
    // from the Bluetooth mouse or was injected from the network stream a moment ago.
    private void ObserveMotion(int dx, int dy)
    {
        if (handoff.Active is not { } id || config is null || !handoff.CanReturn || !DesktopNative.GetCursorPos(out var point)) return;
        if (dx == 0 && dy == 0) return;
        var monitor = monitors.FirstOrDefault(item => item.Id == config.Monitor);
        if (monitor is null) return;
        var held = new[] { 1, 2, 4, 5, 6 }.Any(key => DesktopNative.GetAsyncKeyState(key) < 0);
        var clipped = !DesktopNative.GetClipCursor(out var clip) || clip.Left > monitor.X || clip.Top > monitor.Y ||
            clip.Right < monitor.X + monitor.W || clip.Bottom < monitor.Y + monitor.H;
        if (held) heldMotion++;
        if (clipped) clippedMotion++;
        var previous = lastPoint;
        lastPoint = point;
        if (EdgeDetector.Observe(monitor, monitors, config, point.X, point.Y, dx, dy, held, clipped))
        {
            var live = DesktopNative.MonitorAt(point.X, point.Y);
            var verdict = EdgeDetector.Confirm(monitor, live, monitors, config, point.X, point.Y, dx, dy);
            var detail = LeaveDetail(verdict, monitor, live, point, previous, dx, dy);
            if (verdict == EdgeVerdict.Leave)
            {
                leaveRequests++;
                history.Add(detail);
                Send(new DesktopMessage("leave", SwitchId: id, Edge: config.Edge,
                    Fraction: monitor.Fraction(config.Edge, point.X, point.Y)));
            }
            else RejectLeave(detail);
        }
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
            text => post.Post(_ => { if (text.StartsWith("closed", StringComparison.Ordinal)) ReleaseNetworkButtons(); SetNetworkStatus(text); }, null));
    }

    private void StopNetwork(string reason)
    {
        network?.Dispose(); network = null;
        ReleaseNetworkButtons();
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

    // One record from the stream, on the receiving thread: injected at once, since SendInput
    // needs no particular thread, while the Mac owns the handoff and the default desktop is the
    // input desktop. Returns whether the PC can inject right now; false rides on the next
    // acknowledgement so the Mac switches to the Bluetooth mouse within a round trip. The edge
    // test runs on the desktop thread over the motion accumulated since its last turn.
    private bool Inject(InputEvent record)
    {
        var now = DesktopNative.Milliseconds();
        if (record.Type == InputRecord.Ping) { CheckInputDesktop(now, force: true); return !injectBlocked; }
        lock (networkGate)
        {
            arrivals.Observe(now);
            if (handoff.Active is null) { arrivals.Discarded++; return !injectBlocked; }
            CheckInputDesktop(now, force: injectBlocked);
            if (injectBlocked) { arrivals.Discarded++; return false; }
            if (record.IsMotion)
            {
                if (DesktopNative.InjectMouse(record.Dx, record.Dy, DesktopNative.MouseMove)) arrivals.Injected++;
                else { CheckInputDesktop(now, force: true); if (injectBlocked) { arrivals.Discarded++; return false; } }
                pendingDx += record.Dx; pendingDy += record.Dy;
            }
            else if (record.IsButton)
            {
                var (down, up) = DesktopNative.ButtonFlags(record.Button);
                if (record.Down) heldNetworkButtons |= up; else heldNetworkButtons &= ~up;
                if (DesktopNative.InjectMouse(0, 0, record.Down ? down : up)) arrivals.Injected++;
                return true;
            }
        }
        if (Interlocked.Exchange(ref observePosted, 1) == 0) context?.Post(_ => ObserveInjected(), null);
        return true;
    }

    private void CheckInputDesktop(double now, bool force)
    {
        if (!force && now - lastDesktopCheck < DesktopCheckMs) return;
        lastDesktopCheck = now;
        var blocked = !DesktopNative.IsDefaultDesktop();
        if (blocked != injectBlocked)
        {
            injectBlocked = blocked;
            context?.Post(_ => history.Add(blocked ? "network: secure desktop, injection paused" : "network: injection resumed"), null);
        }
    }

    private void ObserveInjected()
    {
        int dx, dy;
        lock (networkGate) { dx = pendingDx; dy = pendingDy; pendingDx = pendingDy = 0; }
        Interlocked.Exchange(ref observePosted, 0);
        ObserveMotion(dx, dy);
    }

    // A button pressed from the stream is released here when the stream, the handoff or the
    // worker ends, so Windows never keeps one that the Mac can no longer release.
    private void ReleaseNetworkButtons()
    {
        lock (networkGate)
        {
            if (heldNetworkButtons == 0) return;
            DesktopNative.InjectMouse(0, 0, heldNetworkButtons);
            heldNetworkButtons = 0;
        }
    }

    // A layout that disagrees with Windows' live monitor is re-read, and the durable log gets a
    // line, at most once per second however long the cursor pushes outward.
    private void RejectLeave(string detail)
    {
        rejectedLeaves++;
        var now = RuntimeCompat.TickCount64;
        if (now - lastRejectionLog >= 1000)
        {
            lastRejectionLog = now;
            history.Add(detail);
            ThreadPool.QueueUserWorkItem(_ => EdgeDiagnostics.Write(detail));
            Refresh();
        }
    }

    private static string LeaveDetail(EdgeVerdict verdict, MonitorInfo cached, MonitorInfo? live, DesktopNative.Point point,
        DesktopNative.Point? previous, int dx, int dy)
    {
        var physical = DesktopNative.GetPhysicalCursorPos(out var real) ?
            real.X == point.X && real.Y == point.Y ? "same" : $"x={Fraction(live ?? cached, real.X):0.000}" : "unavailable";
        var moved = previous is { } last ? Math.Abs(point.X - last.X) + Math.Abs(point.Y - last.Y) : -1;
        return $"leave {verdict}: cached={Describe(cached)} live={Describe(live)} x={Fraction(cached, point.X):0.000} " +
            $"physical={physical} awareness={DesktopNative.DpiAwareness()} movedPx={moved} raw={dx},{dy}";
    }

    private static double Fraction(MonitorInfo? monitor, int x) => monitor is null || monitor.W <= 0 ? -1 : (x - monitor.X) / (double)monitor.W;
    private static string Describe(MonitorInfo? monitor) => monitor is null ? "none" : $"{monitor.W}x{monitor.H}@{monitor.Dpi}";
    private static string Describe(IReadOnlyList<MonitorInfo> list) => list.Count == 0 ? "none" : string.Join(",", list.Select(Describe));

    // Answers a problem report requested from the Mac. Sizes, fractions and counts only.
    private string BuildReport()
    {
        var configured = monitors.FirstOrDefault(item => item.Id == config?.Monitor);
        var cursorLine = DesktopNative.GetCursorPos(out var point)
            ? $"cursor: x={Fraction(configured, point.X):0.000} live={Describe(DesktopNative.MonitorAt(point.X, point.Y))} " +
              $"physical={(DesktopNative.GetPhysicalCursorPos(out var real) ? real.X == point.X && real.Y == point.Y ? "same" : "differs" : "unavailable")}"
            : "cursor: unavailable";
        var lines = new List<string>
        {
            $"desktop worker: awareness={DesktopNative.DpiAwareness()} blind={blind} mouse={devices.HasMatch} handoff={handoff.Active is not null} " +
                $"hidden={cursor.Hidden} edge={config?.Edge} configured={Describe(configured)}",
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
                $"mouse={devices.HasMatch} selected={selectedMotion} other={otherMotion} held={heldMotion} clipped={clippedMotion} leave={leaveRequests} rejected={rejectedLeaves}";
            // File IO never runs on the raw-input thread. At most one aggregate per five seconds.
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
            ReleaseNetworkButtons();
            clipboard?.Dispose();
            cursor.Dispose();
            shutdown.Cancel(); timer.Dispose(); pipe.Dispose(); window.DestroyHandle(); shutdown.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class RawWindow : NativeWindow
    {
        private readonly DesktopWorker owner;
        public RawWindow(DesktopWorker owner)
        {
            this.owner = owner;
            CreateHandle(new CreateParams { Caption = "DeusKVM desktop input", Parent = new IntPtr(-3) });
        }
        protected override void WndProc(ref System.Windows.Forms.Message message)
        {
            if (message.Msg == 0xFF) owner.RawInput(message.LParam);
            if (message.Msg == 0xFE) owner.devices.Clear(); // device handles can be reused after reconnect
            base.WndProc(ref message);
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
