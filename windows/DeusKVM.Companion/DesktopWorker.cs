using System.ComponentModel;
using System.Diagnostics;
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
    private readonly Channel<DesktopMessage> output = Channel.CreateBounded<DesktopMessage>(128);
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly RawWindow window;
    private DesktopClipboard? clipboard;
    private readonly InactiveCursor cursor = new();
    private readonly string address;
    private readonly string pipeName;
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
    // Every network pointer record goes to the SYSTEM secure-input worker through the pointer
    // pipe: it can inject on every desktop and behind elevated windows, so nothing has to switch
    // when Windows does. This process injects only as the fallback, while that relay is not
    // connected, is stuck, or has reported failures for longer than the grace period. Block says
    // where the fallback cannot inject either: the input desktop is not the default one (UAC
    // prompt, lock screen), or an elevated window has the foreground, where Windows (UIPI) silently
    // discards this process's input; the Mac is then told to use the Bluetooth mouse. The desktop
    // is re-checked every 100 ms while records arrive, on every record while blocked and on each
    // idle ping; the foreground window on every record (cached per window, so normally one call).
    private enum Block : byte { None, SecureDesktop, Elevated }
    private volatile Block block;
    private volatile bool secureDesktop; // the last input-desktop check
    private readonly ForegroundIntegrity foreground = new();
    private bool InjectBlocked => block != Block.None;
    // The service says the SYSTEM secure-input worker is connected.
    private volatile bool secureRelay;
    private PointerPipeClient? pointer;
    // Relay health, under secureGate (taken after networkGate when both are held). The relay is
    // trusted again at every Block change and reconnect; the fallback starts only once the worker
    // has reported failures for longer than the grace period, so a failure or two during a desktop
    // switch (which the worker replays) neither stalls nor disables it. While falling back, a
    // zero-motion probe every 100 ms lets a recovered worker say so.
    private const double SecureGraceMs = 250, ProbeMs = 100;
    private readonly object secureGate = new();
    private bool secureFailing;
    private double secureFailingSince, lastProbe;
    private SecureEpisode? episode;
    // Direct-injection latency (record received → SendInput returned), to compare with the relay's.
    private readonly LatencySamples direct = new();
    private long lastDirectReport;
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
        pipeName = name;
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
            pointer = new PointerPipeClient(pipeName + ".Pointer", parent, text =>
            {
                context.Post(_ => history.Add(text), null);
                ThreadPool.QueueUserWorkItem(_ => EdgeDiagnostics.Write(text));
            });
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
        secureDesktop = !defaultDesktop;
        SetBlocked(Classify());
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
        ReportDirect();
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
            case "secure-ready":
                secureRelay = message.SecureReady;
                if (message.SecureReady) lock (secureGate) secureFailing = false;
                break;
            case "secure-status": SecureStatus(message.Ok); break;
            case "secure-stats": RelayStats(SecureRelayStats.Decode(message.Input)); break;
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

    // One record from the stream, on the receiving thread, while the Mac owns the handoff: handed
    // to the SYSTEM relay (or, as the fallback, injected here at once, since SendInput needs no
    // particular thread). Returns whether the PC can deliver the pointer right now; false rides on
    // the next acknowledgement so the Mac switches to the Bluetooth mouse within a round trip. The
    // edge test runs on the desktop thread over the motion accumulated since its last turn.
    private bool Inject(InputEvent record)
    {
        var arrived = Stopwatch.GetTimestamp();
        var now = DesktopNative.Milliseconds();
        if (record.Type == InputRecord.Ping) { CheckInputDesktop(now, force: true); Probe(now); return CanHandle(now); }
        if (!record.IsMotion && !record.IsButton) return true;
        bool result;
        lock (networkGate)
        {
            arrivals.Observe(now);
            if (handoff.Active is null) { arrivals.Discarded++; return CanHandle(now); }
            CheckInputDesktop(now, force: InjectBlocked);
            if (record.IsButton)
            {
                var up = DesktopNative.ButtonFlags(record.Button).Up;
                if (record.Down) heldNetworkButtons |= up; else heldNetworkButtons &= ~up;
            }
            if (RelayUsable(now) && Relay(record, now, arrived)) result = true;
            else
            {
                Probe(now);
                // A click that just brought an elevated window to the front must not be lost.
                if (record.IsButton) CheckInputDesktop(now, force: true);
                if (InjectBlocked || !InjectLocal(record, now, arrived)) return Drop();
                result = true;
            }
            // On the secure desktop there is no edge to return through; everywhere else the edge
            // test runs, and a relayed step lands a moment later (at the edge the cursor is already
            // pinned there).
            if (record.IsButton || block == Block.SecureDesktop) return result;
            pendingDx += record.Dx; pendingDy += record.Dy;
        }
        if (Interlocked.Exchange(ref observePosted, 1) == 0) context?.Post(_ => ObserveInjected(), null);
        return result;
    }

    // One record injected by this process (under networkGate). False when a failed motion step
    // turns out to be blocked after all.
    private bool InjectLocal(InputEvent record, double now, long arrived)
    {
        var ok = record.IsButton
            ? DesktopNative.InjectMouse(0, 0, record.Down ? DesktopNative.ButtonFlags(record.Button).Down : DesktopNative.ButtonFlags(record.Button).Up)
            : DesktopNative.InjectMouse(record.Dx, record.Dy, DesktopNative.MouseMove);
        if (ok)
        {
            arrivals.Injected++;
            direct.Add(LatencySamples.Micros(Stopwatch.GetTimestamp() - arrived));
            return true;
        }
        if (record.IsButton) return true;
        CheckInputDesktop(now, force: true);
        return !InjectBlocked;
    }

    // Whether the pointer can be delivered to the PC at all (via the relay or directly).
    private bool CanHandle(double now) => RelayUsable(now) || !InjectBlocked;
    private bool RelayUsable(double now) =>
        secureRelay && pointer is { Connected: true } relay && !relay.Stuck(Stopwatch.GetTimestamp()) && !SecureGivenUp(now);

    private bool Relay(InputEvent record, double now, long arrived)
    {
        var bytes = record.IsMotion ? SecureInputRecord.EncodeMotion(record.Dx, record.Dy)
            : SecureInputRecord.EncodeButton(record.Button, record.Down);
        if (pointer?.Enqueue(bytes, arrived) != true) return false;
        arrivals.Injected++;
        lock (secureGate)
        {
            if (episode is not null)
            {
                episode.Forwarded++;
                if (episode.FirstForward < 0) episode.FirstForward = now;
            }
        }
        return true;
    }
    private bool Drop() { arrivals.Discarded++; return false; }

    // While the relay is given up, a zero-motion record lets the worker report that it can inject
    // again (it reports only on a change, and it gets nothing else meanwhile).
    private void Probe(double now)
    {
        if (!secureRelay || now - lastProbe < ProbeMs) return;
        lock (secureGate) if (!secureFailing) return;
        lastProbe = now;
        pointer?.Enqueue(SecureInputRecord.EncodeMotion(0, 0), Stopwatch.GetTimestamp());
    }

    // The worker has been failing to inject for longer than the grace period.
    private bool SecureGivenUp(double now)
    {
        lock (secureGate)
        {
            if (!secureFailing || now - secureFailingSince < SecureGraceMs) return false;
            if (episode is not null) episode.FellBack = true;
            return true;
        }
    }

    // A status from the SYSTEM worker (desktop thread).
    private void SecureStatus(bool injecting)
    {
        var now = DesktopNative.Milliseconds();
        lock (secureGate)
        {
            if (injecting)
            {
                secureFailing = false;
                if (episode is { FirstOk: < 0 }) episode.FirstOk = now;
            }
            else
            {
                if (episode is not null) episode.Failures++;
                if (!secureFailing) { secureFailing = true; secureFailingSince = now; }
            }
        }
    }

    // The SYSTEM worker's ten-second window (desktop thread): one line in the edge log.
    private void RelayStats(SecureRelayStats? stats)
    {
        if (stats is null) return;
        var line = $"pointer relay 10s: total[{stats.Total}] transit[{stats.Transit}] sendInput[{stats.Inject}] " +
            $"failed={stats.Failed} longestFailMs={stats.LongestFailMs} replayed={stats.Replayed} desktopSwitches={stats.Switches}";
        history.Add(line);
        ThreadPool.QueueUserWorkItem(_ => EdgeDiagnostics.Write(line));
    }

    // Direct injections in the last ten seconds, if there were any (desktop thread).
    private void ReportDirect()
    {
        var now = RuntimeCompat.TickCount64;
        if (now - lastDirectReport < 10000 || direct.Count == 0) return;
        lastDirectReport = now;
        var line = $"pointer direct 10s: [{direct.Take()}]";
        history.Add(line);
        ThreadPool.QueueUserWorkItem(_ => EdgeDiagnostics.Write(line));
    }

    private void CheckInputDesktop(double now, bool force)
    {
        if (force || now - lastDesktopCheck >= DesktopCheckMs)
        {
            lastDesktopCheck = now;
            secureDesktop = !DesktopNative.IsDefaultDesktop();
        }
        SetBlocked(Classify());
    }

    private Block Classify() => secureDesktop ? Block.SecureDesktop : foreground.Outranks() ? Block.Elevated : Block.None;

    // Each Block change trusts the relay again. A blocked episode (a secure desktop, or an elevated
    // window in front) writes one line when it ends saying how the relay did, so a slow handoff
    // shows in the edge log.
    private void SetBlocked(Block next)
    {
        if (next == block) return;
        var now = DesktopNative.Milliseconds();
        string? summary = null;
        lock (secureGate)
        {
            if (next == block) return;
            summary = episode?.Summary(now);
            block = next;
            episode = next == Block.None ? null : new SecureEpisode(now, secureRelay, next == Block.Elevated ? "elevated window" : "secure desktop");
            secureFailing = false;
        }
        context?.Post(_ =>
        {
            if (summary is not null) history.Add(summary);
            history.Add(next switch
            {
                Block.SecureDesktop => "network: secure desktop (relay only)",
                Block.Elevated => "network: elevated window in front (relay only)",
                _ => "network: default desktop"
            });
        }, null);
        if (summary is not null) ThreadPool.QueueUserWorkItem(_ => EdgeDiagnostics.Write(summary));
    }

    // Timings for one blocked episode, relative to its start (ms). Counts and timings only.
    private sealed class SecureEpisode(double start, bool relay, string kind)
    {
        public double FirstForward = -1, FirstOk = -1;
        public int Forwarded, Failures;
        public bool FellBack; // to Bluetooth: neither the relay nor this process could inject
        public string Summary(double end) => FormattableString.Invariant(
            $"{kind}: ms={end - start:F0} relay={relay} firstForwardMs={At(FirstForward)} firstInjectedMs={At(FirstOk)} forwarded={Forwarded} failures={Failures} bluetooth={FellBack}");
        private string At(double time) => time < 0 ? "-" : (time - start).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
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
            pointer?.Enqueue(SecureInputRecord.ReleaseRecord, Stopwatch.GetTimestamp()); // the relay's presses
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
