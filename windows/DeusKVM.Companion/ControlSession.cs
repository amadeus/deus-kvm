using System.Buffers.Binary;
using System.Text.Json;
using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

// The companion protocol with one Mac over its control link, on the link's STA thread in the service
// (the connection's threads post here). Desktop observations are requests; the Mac remains the
// routing authority.
internal sealed partial class ControlSession : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    private readonly Action<string> status;
    private readonly Action secureAttention;
    private readonly DesktopRouter.Peer desktopPeer;
    private DesktopRouter.Peer.Channel desktop => desktopPeer.Agent;
    private DesktopRouter.Peer.Channel clipboardHost => desktopPeer.Clipboard;
    private bool controlling;
    private long ownership;
    private ulong capture;
    public bool Controlling => controlling;
    public ControlSession(DesktopRouter router, long source, Action<string> status, Action secureAttention)
    {
        this.status = status; this.secureAttention = secureAttention;
        desktopPeer = router.Register(source, DesktopEvent, ClipboardHostEvent);
    }

    private readonly SynchronizationContext context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    private readonly CompanionHandoff handoff = new();
    private readonly StateReporter stateReport = new();
    private (uint Token, string[] Service)? pendingReport;
    private ControlConnection? connection;
    private MonitorInfo[] monitors = [];
    private byte[]? inputOffer;
    private EdgeConfiguration? config => handoff.Configuration;
    private long lastSeen, connectedAt;
    private bool ready, disposed;
    private int generation;
    private byte blind = 4;
    private MacAvailability availability = new();
    private bool active;
    public bool Ready => ready;
    public bool Available => ready && availability.Available && !NeedsReconnect;
    public bool SupportsSelection => ready && availability.Negotiated;
    public uint AvailabilityEpoch => availability.Epoch;
    public bool Active => active;
    public bool NeedsReconnect { get; private set; }
    public bool Attached => connection is not null;
    public string Detail { get; private set; } = "Waiting for the Mac's HELLO";

    // Takes over a connected, verified link; the Mac sends HELLO first.
    public void Attach(ControlConnection link)
    {
        if (disposed) { link.Dispose(); return; }
        ResetLink();
        connection = link;
        var epoch = generation;
        connectedAt = RuntimeCompat.TickCount64;
        link.Start(
            packet => context.Post(_ => { if (!disposed && epoch == generation) Receive(packet); }, null),
            reason => context.Post(_ => { if (!disposed && epoch == generation) Fail(reason); }, null));
    }

    public void Tick()
    {
        if (disposed) return;
        RefreshDesktop();
        UpdateClipboardSession();
        if (clipboardEnabled) Clipboard.Tick(ClipboardNow);
        if (active && desktop?.Connected == false) SetDetail(desktop.Detail);
        if (!Attached) return;
        if ((!ready && RuntimeCompat.TickCount64 - connectedAt > 10000) ||
            (ready && RuntimeCompat.TickCount64 - lastSeen > 10000))
        { Fail("Companion heartbeat expired; reconnecting"); return; }
        if (ready)
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, unchecked((uint)RuntimeCompat.TickCount64));
            Send(Protocol.Message.Ping, payload);
            AdvertiseClipboard();
            if (active && desktop?.Connected != true) { blind = 4; SendState([4, 2, 0]); SetDetail(desktop?.Detail ?? "Waiting for desktop"); }
        }
    }

    // Also called on activation so the session agent (needed for ENTER placement and edge
    // return) starts immediately rather than at the next tick.
    public void RefreshDesktop()
    {
        if (disposed || !active) return;
        desktopPeer.SetEnabled(true);
        desktop.Refresh();
    }

    public void SetActive(bool value, bool notify = true)
    {
        value = value && Available;
        if (active == value) return;
        active = value;
        stateReport.Reset();
        if (!value)
        {
            RevealUserCursor();
            handoff.Reset(); UpdateClipboardSession(true);
            desktopPeer.SetEnabled(false); monitors = []; blind = 4;
            controlling = false; capture = 0;
            clipboardDesktopAvailable = clipboardDefaultDesktop = false;
        }
        if (notify && availability.Negotiated) Send(Protocol.Message.Selection, availability.Selection(active));
        if (value) { RefreshDesktop(); ForwardInputOffer(); }
        SetDetail(active ? "Connected and ready for input" : "Disabled or not allowed");
    }

    // The Mac's input stream listener. Kept for the life of the link and handed to the session
    // agent whenever it is connected and this Mac is active, since only the agent injects.
    private void ReceiveInputOffer(byte[] payload)
    {
        if (payload.Length > 4096) throw new InvalidDataException("Oversized input offer");
        var offer = JsonSerializer.Deserialize<FileNetworkOffer>(payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (offer is not { Valid: true }) throw new InvalidDataException("Invalid input offer");
        inputOffer = payload;
        ForwardInputOffer();
    }

    private void ForwardInputOffer()
    {
        if (inputOffer is { } offer && active && desktop?.Connected == true)
            desktop.Send(new DesktopMessage("input-offer", Report: System.Text.Encoding.UTF8.GetString(offer)));
    }

    private void Receive(Protocol.Packet packet)
    {
        if (NeedsReconnect) return;
        try
        {
            var type = (Protocol.Message)packet.Type;
            if (type == Protocol.Message.Hello)
            {
                using var hello = JsonDocument.Parse(packet.Payload);
                if (ready || hello.RootElement.GetProperty("v").GetInt32() != 1 ||
                    hello.RootElement.GetProperty("role").GetString() != "mac") throw new InvalidDataException("Invalid HELLO");
                if (!hello.RootElement.TryGetProperty("multiInput", out var multi) || multi.GetInt32() != 1)
                    throw new InvalidDataException("Update the Mac app for simultaneous network control");
                ready = true;
                stateReport.Reset();
                var selected = hello.RootElement.TryGetProperty("selection", out var selection) && selection.TryGetInt32(out var version) && version == 1;
                var epoch = selected ? hello.RootElement.GetProperty("availabilityEpoch").GetUInt32() : 0;
                var enabled = !selected || hello.RootElement.GetProperty("available").GetBoolean();
                availability.Hello(selected, epoch, enabled);
                clipboardPeer = hello.RootElement.TryGetProperty("clipboard", out var capability) &&
                    capability.TryGetInt32(out var clipboardVersion) && clipboardVersion == 1;
                fileReceivePeer = hello.RootElement.TryGetProperty("fileReceive", out var reverseFiles) && reverseFiles.TryGetInt32(out var reverseVersion) && reverseVersion == 1;
                filesPeer = hello.RootElement.TryGetProperty("files", out var files) && files.TryGetInt32(out var filesVersion) && filesVersion == 1;
                imagesPeer = hello.RootElement.TryGetProperty("images", out var images) && images.TryGetInt32(out var imageVersion) && imageVersion == 1;
                SendJson(Protocol.Message.Hello, new { v = 1, role = "pc", name = "DeusKVM Companion",
                    computerName = Environment.MachineName, resume = true, center = true, clipboard = 1, files = 1, fileReceive = 1, fileNetwork = 1, images = 1, selection = 1, report = 1, inputNetwork = 3, multiInput = 1, clipboardSnapshot = 1 });
                UpdateClipboardSession(true);
                if (active && monitors.Length > 0) SendScreens();
                SetDetail("Companion connected; waiting for desktop status");
            }
            else
            {
                if (!ready) throw new InvalidDataException("Expected HELLO");
                if (type == Protocol.Message.ClipboardSnapshot)
                {
                    if (packet.Stream != 1) throw new InvalidDataException("Invalid clipboard snapshot stream");
                    ReceiveClipboardSnapshot(packet.Payload);
                }
                else if (type is Protocol.Message.FileOffer or Protocol.Message.FileData or Protocol.Message.FileAccept)
                {
                    if (packet.Stream != 1 || !filesPeer) throw new InvalidDataException("Invalid file message");
                    ReceiveFile(type, packet.Payload);
                }
                else if (type is Protocol.Message.ClipGrab or Protocol.Message.ClipGet or Protocol.Message.ClipData or Protocol.Message.ClipState)
                {
                    if (packet.Stream != (type == Protocol.Message.ClipData ? 1 : 0)) throw new InvalidDataException("Wrong clipboard stream");
                    ReceiveClipboard(type, packet.Payload);
                }
                else if (type == Protocol.Message.InputOffer)
                {
                    if (packet.Stream != 1) throw new InvalidDataException("Invalid input offer stream");
                    ReceiveInputOffer(packet.Payload);
                }
                else Handle(type, packet.Payload);
            }
            lastSeen = RuntimeCompat.TickCount64;
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException)
        { Fail("Invalid companion message: " + error.Message); }
    }

    private void Handle(Protocol.Message type, byte[] payload)
    {
        if (type == Protocol.Message.Availability)
        {
            availability.Receive(payload);
            if (!availability.Available) SetActive(false, notify: false);
            return;
        }
        if (type == Protocol.Message.ReportRequest)
        {
            if (payload.Length != 4) throw new InvalidDataException("Invalid report request");
            BeginReport(BinaryPrimitives.ReadUInt32LittleEndian(payload));
            return;
        }
        if (!active && type is not (Protocol.Message.Ping or Protocol.Message.Pong)) return;
        switch (type)
        {
            case Protocol.Message.Ping when payload.Length == 4: Send(Protocol.Message.Pong, payload); break;
            case Protocol.Message.Pong when payload.Length == 4: break;
            case Protocol.Message.Config:
                var next = JsonSerializer.Deserialize<EdgeConfiguration>(payload, Json);
                if (next is not { Edge: < 4 } ||
                    !monitors.Any(item => item.Id == next.Monitor)) throw new InvalidDataException("Unknown Windows display");
                if (handoff.Configure(next))
                    desktop?.Send(new DesktopMessage("config", Config: config));
                ResumeDesktop();
                break;
            case Protocol.Message.Enter:
            case Protocol.Message.EnterCenter:
                if (!CaptureProtocol.TryReadEnter(type, payload, out var entry)) throw new InvalidDataException("Invalid capture entry");
                EnterDesktop(entry);
                break;
            case Protocol.Message.Resume:
                if (!CaptureProtocol.TryReadResume(payload, out var resumeId, out var resumeEdge, out var resumeCapture)) throw new InvalidDataException("Invalid capture resume");
                RevealUserCursor();
                capture = resumeCapture;
                handoff.Enter(resumeId, resumeEdge);
                ClipboardSwitch(true);
                ResumeDesktop();
                break;
            case Protocol.Message.Exit:
                if (!CaptureProtocol.TryReadExit(payload, out var exitId, out var exitCapture)) throw new InvalidDataException("Invalid capture exit");
                if (handoff.Accept(exitId) && capture == exitCapture)
                {
                    handoff.Exit(); ClipboardSwitch(false);
                    // On the user's desktop the user's own helper hides the cursor: the taskbar never
                    // hears that the mouse left when the cursor moves onto a SYSTEM-owned window, so a
                    // SYSTEM hider leaves its tooltips open. Ok tells the agent it need not hide there.
                    var userHides = controlling && clipboardHost?.Connected == true && clipboardDefaultDesktop;
                    if (userHides) clipboardHost!.Send(new DesktopMessage("cursor-hide"));
                    desktop?.Send(new DesktopMessage("exit", SwitchId: payload[0], Ok: userHides, Capture: capture));
                }
                break;
            default: throw new InvalidDataException("Unsupported companion message");
        }
    }

    private void RevealUserCursor() => clipboardHost.Send(new DesktopMessage("cursor-reveal"));

    private void EnterDesktop(CaptureEntry entry)
    {
        RevealUserCursor();
        capture = entry.Capture;
        handoff.Enter(entry.SwitchId, entry.Edge);
        ClipboardSwitch(true);
        if (desktop.Connected && config is not null && config.Edge == entry.Edge)
            desktop.Send(new DesktopMessage("enter", SwitchId: entry.SwitchId, Edge: entry.Edge, Center: entry.Center,
                Fraction: entry.Fraction, Capture: capture));
        else SendEntryAck(entry.SwitchId, false, 0, 0, blind, capture);
    }

    // The clipboard helper: its clipboard messages, and whether it is there at all.
    private void ClipboardHostEvent(DesktopMessage message)
    {
        if (!active) return;
        if (message.Kind == "clipboard-ready")
        {
            if (controlling && message.Epoch == clipboardEpoch && message.Ownership == ownership) desktop.Send(message);
            return;
        }
        if (message.Kind.StartsWith("clipboard-", StringComparison.Ordinal)) { ClipboardDesktopEvent(message); return; }
        if (message.Kind is "connected" or "disconnected") { clipboardDesktopAvailable = false; UpdateClipboardSession(true); }
    }

    private void DesktopEvent(DesktopMessage message)
    {
        if (!active) return;
        switch (message.Kind)
        {
            case "input-failed":
                Fail(message.Detail ?? "Input stream failed");
                return;
            case "ownership":
                controlling = message.Ok; ownership = message.Ownership;
                UpdateClipboardSession(true);
                return;
            case "connected":
            case "disconnected":
                handoff.DesktopChanged(); blind = 4;
                clipboardDefaultDesktop = false; UpdateClipboardSession(true);
                SendState([4, 2, 0]);
                if (message.Kind == "connected") ForwardInputOffer();
                break;
            case "sas":
                if (controlling && message.Ownership == ownership) secureAttention();
                break;
            case "screens":
                if (message.Monitors is not { Length: > 0 and <= 32 } screens ||
                    screens.Any(item => item.W is < 5 or > 32768 || item.H is < 5 or > 32768 || item.Id.Length > 256)) return;
                handoff.DesktopChanged(); monitors = screens;
                if (ready) SendScreens();
                break;
            case "state":
                clipboardDefaultDesktop = message.Desktop == 0; UpdateClipboardSession();
                blind = message.Blind;
                SendState([blind, message.Desktop, (byte)(message.MousePresent ? 1 : 0)]);
                SetDetail((message.Detail ?? "Desktop status received") + (message.Network is { Length: > 0 } network ? "\n" + network : ""));
                break;
            case "ack" when handoff.Accept(message.SwitchId) && message.Capture == capture:
                SendEntryAck(message.SwitchId, message.Ok, message.X, message.Y, message.Blind, capture);
                break;
            case "report" when pendingReport?.Token == message.Epoch:
                FinishReport(message.Epoch, message.Report);
                break;
            case "leave" when controlling && message.Ownership == ownership && handoff.Accept(message.SwitchId) &&
                message.Capture == capture && message.Edge == config?.Edge && blind == 0:
                var leave = new byte[12];
                leave[0] = message.SwitchId; leave[1] = message.Edge;
                BinaryPrimitives.WriteUInt16LittleEndian(leave.AsSpan(2), message.Fraction);
                BinaryPrimitives.WriteUInt64LittleEndian(leave.AsSpan(4), capture);
                Send(Protocol.Message.Leave, leave);
                break;
        }
    }

    // Service state now; the session agent adds its edge history if it answers within 3 s.
    private void BeginReport(uint token)
    {
        var age = lastSeen == 0 ? -1 : RuntimeCompat.TickCount64 - lastSeen;
        var service = new[]
        {
            $"windows companion: version={typeof(ControlSession).Assembly.GetName().Version} uptimeS={RuntimeCompat.TickCount64 / 1000}",
            $"link: ready={ready} active={active} blind={blind} mac={connection?.Remote} pendingBytes={connection?.PendingBytes ?? 0} lastSeenAgoMs={age} detail=\"{Detail}\"",
            $"agent: connected={desktop?.Connected == true} detail=\"{desktop?.Detail}\" clipboardHelper={clipboardHost?.Connected == true} monitors={string.Join(",", monitors.Select(item => $"{item.W}x{item.H}@{item.Dpi}"))} edge={config?.Edge}",
            $"clipboard: peer={clipboardPeer} offered={clipboardOffered} enabled={clipboardEnabled} desktop={clipboardDefaultDesktop}/{clipboardDesktopAvailable}"
        };
        if (desktop?.Connected != true) { pendingReport = null; SendReport(token, service, "session agent: not connected"); return; }
        pendingReport = (token, service);
        desktop.Send(new DesktopMessage("report-request", Epoch: token));
        _ = Task.Delay(3000).ContinueWith(_ => context.Post(_ => FinishReport(token, null), null), TaskScheduler.Default);
    }

    private void FinishReport(uint token, string? worker)
    {
        if (pendingReport is not { } pending || pending.Token != token) return;
        pendingReport = null;
        SendReport(token, pending.Service, worker ?? "session agent: no answer within 3 s");
    }

    private void SendReport(uint token, string[] service, string worker)
    {
        if (!ready || disposed) return;
        var text = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", service.Append(worker)));
        var payload = new byte[4 + Math.Min(text.Length, 60000)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, token);
        Array.Copy(text, 0, payload, 4, payload.Length - 4);
        Enqueue(Protocol.Message.Report, 1, payload);
    }

    private void ResumeDesktop()
    {
        if (handoff.Resume(desktop?.Connected == true) is { } resume) desktop?.Send(resume with { Capture = capture });
    }

    private void SendEntryAck(byte id, bool ok, int x, int y, byte unavailable, ulong token) =>
        Send(Protocol.Message.EnterAck, CaptureProtocol.Acknowledge(id, ok, x, y, unavailable, token));

    private void SendScreens() => SendJson(Protocol.Message.Screens, new { monitors });
    private void SendJson(Protocol.Message type, object value) => Enqueue(type, 1, JsonSerializer.SerializeToUtf8Bytes(value, Json));
    private void Send(Protocol.Message type, byte[] payload) { if (ready) Enqueue(type, 0, payload); }
    private void SendState(byte[] state) { if (ready && stateReport.ShouldSend(state, RuntimeCompat.TickCount64)) Send(Protocol.Message.State, state); }
    private void Enqueue(Protocol.Message type, byte stream, byte[] payload)
    {
        if (connection is null || NeedsReconnect || payload.Length > Protocol.MaximumPayload) return;
        if (!connection.Send(stream, (byte)type, payload)) Fail("Mac link closed");
    }

    private void SetDetail(string value) { if (Detail != value) { Detail = value; status(value); } }
    // Transport failure revokes this source; the Mac sees the link close and restores local input.
    // User enable/disable preferences are independent of the connection lifetime.
    private void Fail(string reason) { SetActive(false, notify: false); NeedsReconnect = true; ready = false; UpdateClipboardSession(true); SetDetail(reason); }
    public void ResetLink()
    {
        SetActive(false, notify: false);
        generation++;
        connection?.Dispose(); connection = null;
        ready = NeedsReconnect = false;
        availability = new();
        stateReport.Reset();
        handoff.Reset(); UpdateClipboardSession(true); clipboardPeer = false; inputOffer = null; desktop?.Send(new DesktopMessage("reset"));
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; ResetLink();
        desktopPeer.Dispose();
    }
}
