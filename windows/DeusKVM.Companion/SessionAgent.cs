using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using DeusKVM.Companion.Core;
using Microsoft.Win32.SafeHandles;

namespace DeusKVM.Companion;

/// <summary>
/// The service's single SYSTEM agent in the console session. Every enabled Mac has a stream;
/// one arbitration gate orders ownership, held-input release, clipboard readiness and injection.
/// The window thread owns surfaces; windowless input/desktop threads attach to the active desktop.
/// </summary>
internal sealed class SessionAgent : ApplicationContext
{
    private sealed class Source(long id, long stream)
    {
        public long Id { get; } = id;
        public long Stream { get; } = stream;
        public EdgeConfiguration? Config { get; set; }
        public byte? Switch { get; set; }
        public ulong Capture { get; set; }
        public NetworkInputClient? Network { get; set; }
        public InputOfferState Offers { get; } = new();
        public string NetworkStatus { get; set; } = "no offer";
        public InputDeliveryHealth Delivery { get; } = new();
    }
    private readonly NamedPipeClientStream pipe;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Channel<DesktopMessage> output = Channel.CreateBounded<DesktopMessage>(256);
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly System.Threading.Timer watchdog;
    private readonly RawWindow window;
    private readonly CursorHider cursor = new();
    private readonly InputDesktopThread desktopOps = new();
    private readonly int parent;
    private SynchronizationContext? context;
    private readonly object gate = new();
    private readonly Dictionary<long, Source> sources = [];
    private InputOwnership ownership = new();
    private readonly OwnershipWork work = new();
    private readonly KeyboardState keyboard = new();
    private readonly ConsumerState media = new();
    private readonly KeyRepeater repeater;
    private uint heldButtons;
    private int repeatSerial;
    private bool releasePending, started, seeded;
    private long pendingSince;
    private bool clipboardReadyOnDefault;
    private MonitorInfo[] monitors = [];
    private byte blind = 4;
    private readonly DiagnosticHistory history = new();
    private long lastLeave, firstLeave, lastLeaveGeneration;
    private long deliveringSince, lastHealth, lastRejection;
    private readonly InputArrivalStats arrivals = new();
    private readonly LatencySamples latency = new();
    private int selectedMotion, otherMotion, heldMotion, clippedMotion, leaveRequests, rejectedLeaves;
    private readonly List<KeyAction> owedReleases = [];
    private uint owedButtons;

    public SessionAgent(string name, int parent)
    {
        this.parent = parent;
        watchdog = new System.Threading.Timer(_ => CheckDelivery(), null, 1000, 1000);
        pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        repeater = new KeyRepeater(Repeat);
        window = new RawWindow(this);
        timer.Tick += (_, _) => Refresh();
        Application.Idle += Start;
    }
    private async void Start(object? sender, EventArgs args)
    {
        if (started) return;
        started = true; Application.Idle -= Start;
        try
        {
            await pipe.ConnectAsync(10000, shutdown.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != parent)
                throw new InvalidDataException("Unexpected agent pipe server");
            var post = context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            if (!DesktopNative.RegisterRawInputDevices([new() { Page = 1, Usage = 2, Flags = 0x2100, Target = window.Handle }],
                1, (uint)Marshal.SizeOf<DesktopNative.RawDevice>())) throw new Win32Exception(Marshal.GetLastWin32Error());
            Refresh(); timer.Start();
            await Task.Run(() => DesktopPipe.Pump(pipe, output.Reader, message => post.Post(_ => Handle(message), null), shutdown.Token)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or Win32Exception or System.Text.Json.JsonException) { }
        finally { Environment.Exit(0); }
    }
    private void Send(DesktopMessage message)
    {
        if (!output.Writer.TryWrite(message)) pipe.Dispose();
    }
    private void OnDesktop(Action action)
    {
        desktopOps.Post(() =>
        {
            lock (gate)
            {
                Interlocked.Exchange(ref deliveringSince, RuntimeCompat.TickCount64);
                try { action(); }
                finally { Interlocked.Exchange(ref deliveringSince, 0); }
            }
        });
    }
    private void Note(string line) => context?.Post(_ => history.Add(line), null);
    private bool Current(Source source) => sources.TryGetValue(source.Id, out var current) && ReferenceEquals(source, current);
    private Source? Owner => ownership.Owner is { } id ? sources.GetValueOrDefault(id) : null;

    // Independent of the window loop: a control callback may itself be waiting for the input gate.
    private void CheckDelivery()
    {
        var since = Interlocked.Read(ref deliveringSince);
        if (since == 0 || RuntimeCompat.TickCount64 - since <= 5000) return;
        EdgeDiagnostics.Write("session agent: input delivery stuck for 5 s; restarting");
        Environment.Exit(3);
    }

    private void Refresh()
    {
        if (!Monitor.TryEnter(gate)) return;
        try
        {
            var desktop = DesktopNative.DesktopState();
            var next = DesktopNative.Monitors();
            blind = desktop.Blind;
            if (next.Length > 0 && !next.SequenceEqual(monitors))
            {
                monitors = next;
                Send(new DesktopMessage("screens", Monitors: monitors));
            }
            if (!work.Ready && pendingSince != 0 && RuntimeCompat.TickCount64 - pendingSince > 5000 && Owner is { } timedOut)
                FailSource(timedOut, "Clipboard preparation timed out");
            if (RuntimeCompat.TickCount64 - lastHealth >= 10000)
            {
                lastHealth = RuntimeCompat.TickCount64;
                if (arrivals.Received > 0 || leaveRequests + rejectedLeaves > 0)
                {
                    var line = $"input 10s: sources={sources.Count} owner={ownership.Owner} {arrivals.Summary} latency=[{latency.Take()}] " +
                        $"edge selected={selectedMotion} other={otherMotion} held={heldMotion} clipped={clippedMotion} leave={leaveRequests} rejected={rejectedLeaves}";
                    history.Add(line); ThreadPool.QueueUserWorkItem(_ => EdgeDiagnostics.Write(line));
                    arrivals.Reset(); selectedMotion = otherMotion = heldMotion = clippedMotion = leaveRequests = rejectedLeaves = 0;
                }
            }
            if (blind != 0) cursor.Reveal(); else cursor.Follow(desktop.Desktop);
            Send(new DesktopMessage("state", Blind: desktop.Blind, Desktop: desktop.Desktop, MousePresent: true,
                Detail: desktop.Blind == 0 ? "Windows edge return ready" : "Input desktop unavailable; use the Mac hotkey"));
        }
        finally { Monitor.Exit(gate); }
    }

    private void Handle(DesktopMessage message)
    {
        lock (gate)
        {
            if (message.Kind == "generation")
            {
                if (!seeded) { ownership = new InputOwnership(message.Ownership); seeded = true; }
                return;
            }
            if (!seeded) return;
            if (message.Kind == "source")
            {
                if (sources.TryGetValue(message.Source, out var old) && old.Stream == message.Stream) return;
                if (old is not null) Remove(old);
                sources[message.Source] = new Source(message.Source, message.Stream);
                ownership.Connect(message.Source, message.Stream);
                return;
            }
            if (message.Kind == "clipboard-ready")
            {
                if (message.Ok && work.Current(message.Source, message.Ownership))
                    OnDesktop(() => Complete(message.Source, message.Ownership, message.SecureReady));
                return;
            }
            if (message.Kind == "clipboard-lost")
            {
                if (Owner is { } selected) { ownership.Renew(); BeginOwner(selected); }
                return;
            }
            if (!sources.TryGetValue(message.Source, out var source) || source.Stream != message.Stream) return;
            switch (message.Kind)
            {
                case "remove": Remove(source); break;
                case "config": source.Config = message.Config is { Edge: < 4 } ? message.Config : null; break;
                case "input-offer": StartNetwork(source, message.Report); break;
                case "enter":
                    if (source.Config?.Edge != message.Edge || message.Capture == 0) { Ack(source, message, false); break; }
                    source.Switch = message.SwitchId; source.Capture = message.Capture;
                    ownership.Enter(source.Id, source.Stream, message.SwitchId, message.Capture);
                    BeginOwner(source);
                    // The placement waits behind the same clipboard barrier as keyboard/mouse input.
                    work.Submit(source.Id, ownership.Generation, () => Place(source, message));
                    break;
                case "resume":
                    if (source.Config?.Edge != message.Edge || message.Capture == 0) break;
                    if (ownership.Resume(source.Id, source.Stream, message.SwitchId, message.Capture))
                    { source.Switch = message.SwitchId; source.Capture = message.Capture; }
                    break;
                case "exit":
                    var wasOwner = ownership.Owner == source.Id;
                    if (!ownership.Exit(source.Id, source.Stream, message.SwitchId, message.Capture)) break;
                    source.Switch = null; source.Capture = 0;
                    if (wasOwner)
                    {
                        work.Cancel(); pendingSince = 0; releasePending = true; repeater.Stop();
                        var exitGeneration = ownership.Generation;
                        OnDesktop(() => { lock (gate) { if (ownership.Generation == exitGeneration && InputDesktop.Attach()) ReleaseHeld(); } });
                        if (blind == 0) cursor.Hide(userHides: message.Ok);
                    }
                    break;
                case "report-request":
                    Send(new DesktopMessage("report", Epoch: message.Epoch, Source: source.Id, Stream: source.Stream,
                        Report: $"session agent: sources={sources.Count} owner={ownership.Owner} generation={ownership.Generation} pending={work.Count}\n" + string.Join("\n", history.Lines())));
                    break;
            }
        }
    }

    private void BeginOwner(Source source)
    {
        work.Begin(source.Id, ownership.Generation);
        releasePending = true; repeater.Stop();
        pendingSince = RuntimeCompat.TickCount64; clipboardReadyOnDefault = false; lastLeave = 0;
        foreach (var item in sources.Values)
            item.Network?.SetOwner(item.Capture, ownership.Generation, item.Id == source.Id);
        Send(new DesktopMessage("ownership", Source: source.Id, Stream: source.Stream, Ownership: ownership.Generation, Capture: source.Capture));
        var generation = ownership.Generation;
        // Release the previous controller promptly; clipboard fetches never keep its keys held.
        OnDesktop(() => { lock (gate) { if (ownership.Generation == generation && InputDesktop.Attach()) ReleaseHeld(); } });
        context?.Post(_ => cursor.Reveal(), null);
    }
    private void EndOwner()
    {
        work.Begin(0, ownership.Generation); pendingSince = 0; releasePending = true; repeater.Stop();
        var generation = ownership.Generation;
        foreach (var item in sources.Values) item.Network?.SetOwner(item.Capture, generation, false);
        Send(new DesktopMessage("ownership", Ownership: generation));
        OnDesktop(() => { lock (gate) { if (ownership.Generation == generation && InputDesktop.Attach()) ReleaseHeld(); } });
    }
    private void Complete(long source, long generation, bool secureOnly)
    {
        lock (gate)
        {
            if (!work.Current(source, generation) || ownership.Owner != source || !InputDesktop.Attach()) return;
            if (secureOnly && string.Equals(InputDesktop.Current, "Default", StringComparison.OrdinalIgnoreCase))
            { context?.Post(_ => Refresh(), null); return; }
            if (releasePending && !ReleaseHeld()) return;
            clipboardReadyOnDefault = string.Equals(InputDesktop.Current, "Default", StringComparison.OrdinalIgnoreCase);
            pendingSince = 0;
            work.Complete(source, generation);
        }
    }
    private void Place(Source source, DesktopMessage message)
    {
        if (!Current(source) || ownership.Owner != source.Id || source.Capture != message.Capture) return;
        var monitor = monitors.FirstOrDefault(item => item.Id == source.Config?.Monitor);
        if (monitor is null) { Ack(source, message, false); return; }
        var point = message.Center ? monitor.Center : monitor.Entry(message.Edge, message.Fraction);
        var position = new DesktopNative.Point();
        var ok = DesktopNative.SetCursorPos(point.X, point.Y) && DesktopNative.GetCursorPos(out position) &&
            Math.Abs(point.X - position.X) <= 1 && Math.Abs(point.Y - position.Y) <= 1;
        Ack(source, message, ok, position);
    }
    private void Ack(Source source, DesktopMessage message, bool ok, DesktopNative.Point position = default) =>
        Send(new DesktopMessage("ack", SwitchId: message.SwitchId, Capture: message.Capture, Ok: ok,
            X: position.X, Y: position.Y, Blind: ok ? blind : (byte)4, Source: source.Id, Stream: source.Stream, Ownership: ownership.Generation));

    private void StartNetwork(Source source, string? json)
    {
        FileNetworkOffer? offer = null;
        try { if (json is not null) offer = System.Text.Json.JsonSerializer.Deserialize<FileNetworkOffer>(json, FileClipboardOffer.WireJson); }
        catch (System.Text.Json.JsonException) { }
        if (offer is not { Valid: true } || context is not { } post) return;
        var change = source.Offers.Receive(offer, source.Switch is not null);
        if (change == InputOfferChange.Unchanged) return;
        if (change == InputOfferChange.Reconnect)
        {
            FailSource(source, "Input listener changed during capture; reconnecting for a fresh local session");
            return;
        }
        StopNetwork(source);
        NetworkInputClient? client = null;
        client = new NetworkInputClient(offer, record =>
        {
            lock (gate)
            {
                if (!Current(source) || !ReferenceEquals(source.Network, client)) return false;
                Interlocked.Exchange(ref deliveringSince, RuntimeCompat.TickCount64);
                try { return Deliver(source, record); }
                finally { Interlocked.Exchange(ref deliveringSince, 0); }
            }
        },
            text => post.Post(_ =>
            {
                lock (gate)
                {
                    if (!Current(source) || !ReferenceEquals(source.Network, client)) return;
                    source.NetworkStatus = text;
                    if (text.StartsWith("closed", StringComparison.Ordinal)) FailSource(source, "Input stream disconnected");
                }
            }, null));
        source.Network = client;
    }
    private void StopNetwork(Source source)
    {
        source.Network?.Dispose(); source.Network = null;
    }
    private void Remove(Source source)
    {
        StopNetwork(source);
        var wasOwner = ownership.Owner == source.Id;
        ownership.Disconnect(source.Id, source.Stream); sources.Remove(source.Id);
        if (wasOwner) EndOwner();
    }
    private void FailSource(Source source, string reason)
    {
        Send(new DesktopMessage("input-failed", Source: source.Id, Stream: source.Stream, Detail: reason));
        Remove(source);
    }

    // Called on an attached stream thread with gate held; control/ready callbacks use the same gate.
    private bool Deliver(Source source, InputEvent record)
    {
        if (!InputDesktop.Attach()) return false;
        if (record.Type == InputRecord.Ping)
        {
            return source.Delivery.Probe();
        }
        arrivals.Observe(DesktopNative.Milliseconds());
        var admission = ownership.Observe(source.Id, source.Stream, record);
        if (admission.OwnershipChanged) BeginOwner(source);
        if (admission.Disposition == InputDisposition.Reset)
        {
            // Clear queued presses/placement from before cleanup. EXIT still owns mode changes.
            work.DiscardPending(); repeater.Stop();
            return ReleaseHeld();
        }
        if (admission.Disposition != InputDisposition.Forward) { arrivals.Discarded++; return true; }
        // A secure-desktop acknowledgement cannot authorize a paste after returning to Default.
        if (work.Ready && !clipboardReadyOnDefault && string.Equals(InputDesktop.Current, "Default", StringComparison.OrdinalIgnoreCase))
        { ownership.Renew(); BeginOwner(source); }
        var generation = ownership.Generation;
        if (!work.Submit(source.Id, generation, () => source.Delivery.Observe(Inject(source, admission.Record, admission.RestoreModifiers))))
        { FailSource(source, "Input preparation queue exceeded its limit"); return false; }
        return source.Delivery.Healthy;
    }
    private bool Inject(Source source, InputEvent record, byte restoreModifiers)
    {
        var arrived = Stopwatch.GetTimestamp();
        if (releasePending && !ReleaseHeld()) { arrivals.Discarded++; return false; }
        var ok = true;
        if (restoreModifiers != 0)
            ok &= ApplyKeys(keyboard.Apply(restoreModifiers, []));
        if (record.IsKeyboard) { ok &= ApplyKeys(keyboard.Apply(record.Modifiers, record.Keys ?? [])); ArmRepeat(); }
        else if (record.IsConsumer) ok &= ApplyKeys(media.Apply(record.Usage));
        else if (record.IsScroll) ok &= DesktopNative.InjectWheel(record.Wheel * 120, record.Pan * 120);
        else if (record.IsButton)
        {
            var (down, up) = DesktopNative.ButtonFlags(record.Button);
            if (record.Down) heldButtons |= up; else heldButtons &= ~up;
            ok &= DesktopNative.InjectMouse(0, 0, record.Down ? down : up);
        }
        else if (record.IsMotion)
        {
            ok &= DesktopNative.InjectMouse(record.Dx, record.Dy, DesktopNative.MouseMove);
            if (ok) ObserveMotion(source, record.Dx, record.Dy);
        }
        if (ok) arrivals.Injected++;
        else { arrivals.Discarded++; releasePending = true; repeater.Stop(); }
        latency.Add(LatencySamples.Micros(Stopwatch.GetTimestamp() - arrived));
        return ok;
    }
    private bool ApplyKeys(IEnumerable<KeyAction> actions)
    {
        var ok = true;
        foreach (var action in actions)
        {
            if (action.Kind == KeyActionKind.Sas && Owner is { } source)
                Send(new DesktopMessage("sas", Source: source.Id, Stream: source.Stream, Ownership: ownership.Generation));
            else if (action.Kind != KeyActionKind.Sas) ok &= DesktopNative.InjectKey(action.Key, action.Kind == KeyActionKind.Release);
        }
        return ok;
    }
    private bool ReleaseHeld()
    {
        owedReleases.AddRange(keyboard.ReleaseAll()); owedReleases.AddRange(media.ReleaseAll());
        owedButtons |= heldButtons; heldButtons = 0; ArmRepeat();
        var ok = ApplyKeys(owedReleases);
        if (owedButtons != 0) ok &= DesktopNative.InjectMouse(0, 0, owedButtons);
        if (ok) { owedReleases.Clear(); owedButtons = 0; releasePending = false; }
        return ok;
    }
    private void ArmRepeat()
    {
        if (keyboard.RepeatSerial == repeatSerial) return;
        repeatSerial = keyboard.RepeatSerial;
        if (keyboard.Repeating is not { } key) { repeater.Stop(); return; }
        var (delay, period) = DesktopNative.KeyRepeat();
        repeater.Start(key, repeatSerial, delay, period);
    }
    private void Repeat(KeyStroke key, int serial)
    {
        lock (gate)
        {
            if (!work.Ready || releasePending || serial != repeatSerial || keyboard.Repeating != key || Owner is not { } source) return;
            Interlocked.Exchange(ref deliveringSince, RuntimeCompat.TickCount64);
            try
            {
                var ok = InputDesktop.Attach() && DesktopNative.InjectKey(key, false);
                source.Delivery.Observe(ok);
                if (!ok) { releasePending = true; repeater.Stop(); }
            }
            finally { Interlocked.Exchange(ref deliveringSince, 0); }
        }
    }
    private void ObserveMotion(Source source, int dx, int dy)
    {
        if (source.Switch is not { } id || source.Config is not { } config || blind != 0 || !DesktopNative.GetCursorPos(out var point)) return;
        var monitor = monitors.FirstOrDefault(item => item.Id == config.Monitor);
        if (monitor is null) return;
        selectedMotion++;
        var held = new[] { 1, 2, 4, 5, 6 }.Any(key => DesktopNative.GetAsyncKeyState(key) < 0);
        var clipped = !DesktopNative.GetClipCursor(out var clip) || clip.Left > monitor.X || clip.Top > monitor.Y ||
            clip.Right < monitor.X + monitor.W || clip.Bottom < monitor.Y + monitor.H;
        if (held) heldMotion++; if (clipped) clippedMotion++;
        if (!EdgeDetector.Observe(monitor, monitors, config, point.X, point.Y, dx, dy, held, clipped)) return;
        var verdict = EdgeDetector.Confirm(monitor, DesktopNative.MonitorAt(point.X, point.Y), monitors, config, point.X, point.Y, dx, dy);
        if (verdict != EdgeVerdict.Leave)
        {
            rejectedLeaves++;
            if (RuntimeCompat.TickCount64 - lastRejection > 1000)
            {
                lastRejection = RuntimeCompat.TickCount64;
                var line = $"leave rejected: {verdict} source={source.Id} capture={source.Capture} desktop={InputDesktop.Current} raw={dx},{dy} point={point.X},{point.Y}";
                Note(line); ThreadPool.QueueUserWorkItem(_ => EdgeDiagnostics.Write(line));
                context?.Post(_ => Refresh(), null);
            }
            return;
        }
        leaveRequests++;
        var now = RuntimeCompat.TickCount64;
        if (lastLeaveGeneration != ownership.Generation)
        { lastLeaveGeneration = ownership.Generation; firstLeave = now; lastLeave = long.MinValue / 2; }
        var fraction = monitor.Fraction(config.Edge, point.X, point.Y);
        var viaStream = source.Network?.Leave(InputRecord.LeaveFlags(id, config.Edge, fraction)) == true;
        if (viaStream && now - firstLeave < 150 || now - lastLeave < 100) return;
        lastLeave = now;
        Send(new DesktopMessage("leave", Source: source.Id, Stream: source.Stream, Ownership: ownership.Generation,
            Capture: source.Capture, SwitchId: id, Edge: config.Edge, Fraction: fraction));
    }

    private void RawInput(IntPtr handle)
    {
        if (!cursor.Hidden) return;
        var headerSize = (uint)(8 + IntPtr.Size * 2); uint size = 0;
        if (DesktopNative.GetRawInputData(handle, 0x10000003, IntPtr.Zero, ref size, headerSize) == uint.MaxValue || size < headerSize + 24 || size > 4096) return;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (DesktopNative.GetRawInputData(handle, 0x10000003, buffer, ref size, headerSize) != size || Marshal.ReadInt32(buffer) != 0) return;
            var mouse = buffer + (int)headerSize;
            if (Marshal.ReadInt32(mouse, 20) == unchecked((int)DesktopNative.InjectTag)) return;
            if (Marshal.ReadInt32(mouse, 12) != 0 || Marshal.ReadInt32(mouse, 16) != 0 || Marshal.ReadInt16(mouse, 4) != 0) { otherMotion++; cursor.Reveal(); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.Idle -= Start; watchdog.Dispose();
            lock (gate) foreach (var source in sources.Values) StopNetwork(source);
            OnDesktop(() => { lock (gate) if (InputDesktop.Attach()) ReleaseHeld(); });
            desktopOps.Dispose(); repeater.Dispose(); cursor.Dispose();
            shutdown.Cancel(); timer.Dispose(); pipe.Dispose(); window.DestroyHandle(); shutdown.Dispose();
        }
        base.Dispose(disposing);
    }
    private sealed class RawWindow : NativeWindow
    {
        private readonly SessionAgent owner;
        public RawWindow(SessionAgent owner) { this.owner = owner; CreateHandle(new CreateParams { Caption = "DeusKVM session agent", Parent = new IntPtr(-3) }); }
        protected override void WndProc(ref System.Windows.Forms.Message message)
        { if (message.Msg == 0xFF) owner.RawInput(message.LParam); base.WndProc(ref message); }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
