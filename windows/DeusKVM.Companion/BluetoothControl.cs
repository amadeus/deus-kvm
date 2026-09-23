using System.Buffers.Binary;
using System.Text.Json;
using DeusKVM.Companion.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace DeusKVM.Companion;

// Runs on the service's STA BLE worker. Desktop observations are requests; the Mac remains the routing authority.
internal sealed partial class BluetoothControl(Action<string> status) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    private readonly SynchronizationContext context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    private readonly Queue<(GattCharacteristic Characteristic, byte[] Data, bool HelloEnd)> controls = new(), bulk = new();
    private readonly CompanionHandoff handoff = new();
    private Protocol.Encoder controlEncoder = new(), bulkEncoder = new();
    private Protocol.Decoder controlDecoder = new(), bulkDecoder = new();
    private GattCharacteristic? controlRead, controlWrite, bulkRead, bulkWrite;
    private DesktopHost? desktop;
    private MonitorInfo[] monitors = [];
    private EdgeConfiguration? config => handoff.Configuration;
    private long lastSeen, connectedAt;
    private bool subscribing, ready, writing, helloSent, disposed;
    private int generation;
    private byte blind = 4;
    private MacAvailability availability = new();
    private bool active;
    private int desktopGeneration;
    private static long nextRequest;
    private uint? requestedEpoch, disabledEpoch;
    public long RequestSerial { get; private set; }
    public uint? ReleaseAcknowledged { get; private set; }
    public bool SupportsTakeover { get; private set; }
    public bool Ready => ready;
    public bool Available => ready && availability.Available && !NeedsReconnect;
    public bool SupportsSelection => ready && availability.Negotiated;
    public uint AvailabilityEpoch => availability.Epoch;
    public bool Active => active;
    public bool NeedsReconnect { get; private set; }
    public bool Attached => controlRead is not null;
    public string Detail { get; private set; } = "Waiting for Mac control service";

    public async Task Attach(GattDeviceService service, CancellationToken stop)
    {
        if (disposed) return;
        ResetLink();
        var epoch = generation;
        var result = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(stop);
        if (disposed || epoch != generation) return;
        if (result.Status != GattCommunicationStatus.Success) throw new IOException("Control characteristic discovery: " + result.Status);
        GattCharacteristic Find(int id) => result.Characteristics.FirstOrDefault(item => item.Uuid == Protocol.Uuid(id))
            ?? throw new IOException("Missing companion characteristic " + id);
        controlRead = Find(2); controlWrite = Find(3); bulkRead = Find(4); bulkWrite = Find(5);
        foreach (var characteristic in new[] { controlRead, controlWrite, bulkRead, bulkWrite })
            characteristic.ProtectionLevel = GattProtectionLevel.EncryptionRequired;
        // Clear persisted CCCDs first so macOS starts a fresh per-connection handshake.
        foreach (var characteristic in new[] { controlRead, bulkRead })
        {
            var outcome = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.None).AsTask(stop);
            if (disposed || epoch != generation) return;
            if (outcome != GattCommunicationStatus.Success) throw new IOException("Companion subscription reset: " + outcome);
        }
        controlRead.ValueChanged += Notification;
        bulkRead.ValueChanged += Notification;
        subscribing = true;
        connectedAt = Environment.TickCount64;
        foreach (var characteristic in new[] { controlRead, bulkRead })
        {
            var outcome = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(stop);
            if (disposed || epoch != generation) return;
            if (outcome != GattCommunicationStatus.Success) throw new IOException("Companion subscription: " + outcome);
        }
        subscribing = false;
        Drain();
    }

    public void Tick(string address)
    {
        if (disposed) return;
        if (active)
        {
            if (desktop is null)
            {
                var epoch = ++desktopGeneration;
                desktop = new DesktopHost(message => context.Post(_ =>
                { if (!disposed && active && epoch == desktopGeneration) DesktopEvent(message); }, null));
            }
            desktop.Refresh(address);
        }
        UpdateClipboardSession();
        if (clipboardEnabled) Clipboard.Tick(ClipboardNow);
        if (active && desktop?.Connected == false) SetDetail(desktop.Detail);
        if (!Attached) return;
        if ((!ready && Environment.TickCount64 - connectedAt > 10000) ||
            (ready && Environment.TickCount64 - lastSeen > 10000))
        { Fail("Companion heartbeat expired; reconnecting"); return; }
        if (ready)
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, unchecked((uint)Environment.TickCount64));
            Send(Protocol.Message.Ping, payload);
            AdvertiseClipboard();
            if (active && desktop?.Connected != true) { blind = 4; Send(Protocol.Message.State, [4, 2, 0]); SetDetail(desktop?.Detail ?? "Waiting for desktop"); }
        }
    }

    public void SetActive(bool value, bool notify = true)
    {
        value = value && Available;
        if (active == value) return;
        active = value;
        if (!value)
        {
            desktopGeneration++;
            handoff.Reset(); UpdateClipboardSession(true);
            desktop?.Dispose(); desktop = null; monitors = []; blind = 4;
            clipboardDesktopAvailable = clipboardDefaultDesktop = false;
        }
        if (notify && availability.Negotiated) Send(Protocol.Message.Selection, availability.Selection(active));
        if (value) { disabledEpoch = null; ReleaseAcknowledged = null; }
        SetDetail(active ? "Active Mac; preparing desktop" : "Waiting for Windows priority");
    }

    public void RequestDisable(uint? epoch = null)
    {
        var requested = epoch ?? availability.Epoch;
        if (!ready || !availability.Negotiated || disabledEpoch == requested) return;
        SetActive(false, notify: false);
        var payload = new byte[5]; BinaryPrimitives.WriteUInt32LittleEndian(payload, requested);
        disabledEpoch = requested;
        Send(Protocol.Message.Selection, payload);
    }

    private void RequestOwnership()
    {
        if (!SupportsTakeover || !availability.Available || requestedEpoch == availability.Epoch) return;
        requestedEpoch = availability.Epoch;
        RequestSerial = Interlocked.Increment(ref nextRequest);
    }

    private void Notification(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        using var reader = DataReader.FromBuffer(args.CharacteristicValue);
        if (reader.UnconsumedBufferLength > Protocol.ChunkSize) { context.Post(_ => Fail("Oversized companion frame"), null); return; }
        var bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        context.Post(_ => Receive(sender, bytes), null);
    }

    private void Receive(GattCharacteristic source, byte[] frame)
    {
        if (NeedsReconnect || (source != controlRead && source != bulkRead)) return;
        try
        {
            var control = source == controlRead;
            var packet = (control ? controlDecoder : bulkDecoder).Receive(frame);
            if (packet is null) return;
            if (packet.Stream != (control ? 0 : 1)) throw new InvalidDataException("Wrong companion stream");
            var type = (Protocol.Message)packet.Type;
            if (type == Protocol.Message.Hello)
            {
                using var hello = JsonDocument.Parse(packet.Payload);
                if (ready || hello.RootElement.GetProperty("v").GetInt32() != 1 ||
                    hello.RootElement.GetProperty("role").GetString() != "mac" ||
                    hello.RootElement.GetProperty("chunk").GetInt32() < 20) throw new InvalidDataException("Invalid HELLO");
                ready = true;
                var selected = hello.RootElement.TryGetProperty("selection", out var selection) && selection.TryGetInt32(out var version) && version == 1;
                var epoch = selected ? hello.RootElement.GetProperty("availabilityEpoch").GetUInt32() : 0;
                var enabled = !selected || hello.RootElement.GetProperty("available").GetBoolean();
                availability.Hello(selected, epoch, enabled);
                SupportsTakeover = selected && hello.RootElement.TryGetProperty("takeover", out var takeover) && takeover.ValueKind == JsonValueKind.True;
                if (hello.RootElement.TryGetProperty("requestControl", out var request) && request.ValueKind == JsonValueKind.True) RequestOwnership();
                clipboardPeer = hello.RootElement.TryGetProperty("clipboard", out var capability) &&
                    capability.TryGetInt32(out var clipboardVersion) && clipboardVersion == 1;
                fileReceivePeer = hello.RootElement.TryGetProperty("fileReceive", out var reverseFiles) && reverseFiles.TryGetInt32(out var reverseVersion) && reverseVersion == 1;
                filesPeer = hello.RootElement.TryGetProperty("files", out var files) && files.TryGetInt32(out var filesVersion) && filesVersion == 1;
                SendJson(Protocol.Message.Hello, new { v = 1, role = "pc", name = "DeusKVM Companion",
                    computerName = Environment.MachineName, chunk = 20, resume = true, center = true, clipboard = 1, files = 1, fileReceive = 1, fileNetwork = 1, selection = 1, takeover = true });
                UpdateClipboardSession(true);
                if (active && monitors.Length > 0) SendScreens();
                SetDetail("Companion connected; waiting for desktop status");
            }
            else
            {
                if (!ready) throw new InvalidDataException("Expected HELLO");
                if (type is Protocol.Message.FileOffer or Protocol.Message.FileData or Protocol.Message.FileAccept)
                {
                    if (packet.Stream != 1 || !filesPeer) throw new InvalidDataException("Invalid file message");
                    ReceiveFile(type, packet.Payload);
                }
                else if (type is Protocol.Message.ClipGrab or Protocol.Message.ClipGet or Protocol.Message.ClipData or Protocol.Message.ClipState)
                {
                    if (packet.Stream != (type == Protocol.Message.ClipData ? 1 : 0)) throw new InvalidDataException("Wrong clipboard stream");
                    ReceiveClipboard(type, packet.Payload);
                }
                else Handle(type, packet.Payload);
            }
            lastSeen = Environment.TickCount64;
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
            if (availability.Requested) RequestOwnership();
            return;
        }
        if (type == Protocol.Message.RequestControl)
        {
            if (!SupportsTakeover || payload.Length != 4) throw new InvalidDataException("Invalid control request");
            if (BinaryPrimitives.ReadUInt32LittleEndian(payload) == availability.Epoch) RequestOwnership();
            return;
        }
        if (type == Protocol.Message.ReleaseAck)
        {
            if (!SupportsTakeover || payload.Length != 4) throw new InvalidDataException("Invalid release acknowledgement");
            ReleaseAcknowledged = BinaryPrimitives.ReadUInt32LittleEndian(payload);
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
            case Protocol.Message.Enter when payload.Length == 4 && payload[1] < 4:
                EnterDesktop(payload, false);
                break;
            case Protocol.Message.EnterCenter when payload.Length == 2 && payload[1] < 4:
                EnterDesktop(payload, true);
                break;
            case Protocol.Message.Resume when payload.Length == 2 && payload[1] < 4:
                handoff.Enter(payload[0], payload[1]);
                ClipboardSwitch(true);
                ResumeDesktop();
                break;
            case Protocol.Message.Exit when payload.Length == 1:
                if (handoff.Accept(payload[0])) { handoff.Exit(); ClipboardSwitch(false); desktop?.Send(new DesktopMessage("exit", SwitchId: payload[0])); }
                break;
            default: throw new InvalidDataException("Unsupported companion message");
        }
    }

    private void EnterDesktop(byte[] payload, bool center)
    {
        handoff.Enter(payload[0], payload[1]);
        ClipboardSwitch(true);
        if (desktop?.Connected == true && config is not null && config.Edge == payload[1])
            desktop.Send(new DesktopMessage("enter", SwitchId: payload[0], Edge: payload[1], Center: center,
                Fraction: center ? (ushort)0 : BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2))));
        else Send(Protocol.Message.EnterAck, [payload[0], 0, 0, 0, 0, 0, blind]);
    }

    private void DesktopEvent(DesktopMessage message)
    {
        if (!active) return;
        if (message.Kind.StartsWith("clipboard-", StringComparison.Ordinal)) { ClipboardDesktopEvent(message); return; }
        switch (message.Kind)
        {
            case "connected":
            case "disconnected":
                handoff.DesktopChanged(); blind = 4;
                clipboardDesktopAvailable = clipboardDefaultDesktop = false; UpdateClipboardSession(true);
                if (ready) Send(Protocol.Message.State, [4, 2, 0]);
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
                if (ready) Send(Protocol.Message.State, [blind, message.Desktop, (byte)(message.MousePresent ? 1 : 0)]);
                SetDetail(message.Detail ?? "Desktop status received");
                break;
            case "ack" when handoff.Accept(message.SwitchId):
                var ack = new byte[7]; ack[0] = message.SwitchId; ack[1] = message.Ok ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteInt16LittleEndian(ack.AsSpan(2), (short)Math.Clamp(message.X, short.MinValue, short.MaxValue));
                BinaryPrimitives.WriteInt16LittleEndian(ack.AsSpan(4), (short)Math.Clamp(message.Y, short.MinValue, short.MaxValue));
                ack[6] = message.Blind;
                Send(Protocol.Message.EnterAck, ack);
                break;
            case "leave" when handoff.Accept(message.SwitchId) && message.Edge == config?.Edge && blind == 0:
                Send(Protocol.Message.Leave, [message.SwitchId, message.Edge, (byte)message.Fraction, (byte)(message.Fraction >> 8)]);
                break;
        }
    }

    private void ResumeDesktop()
    {
        if (handoff.Resume(desktop?.Connected == true) is { } resume) desktop?.Send(resume);
    }

    private void SendScreens() => SendJson(Protocol.Message.Screens, new { monitors });
    private void SendJson(Protocol.Message type, object value) => Enqueue(type, 1, JsonSerializer.SerializeToUtf8Bytes(value, Json));
    private void Send(Protocol.Message type, byte[] payload) { if (ready) Enqueue(type, 0, payload); }
    private void Enqueue(Protocol.Message type, byte stream, byte[] payload)
    {
        var characteristic = stream == 0 ? controlWrite : bulkWrite;
        if (characteristic is null || NeedsReconnect) return;
        var frames = (stream == 0 ? controlEncoder : bulkEncoder).Encode(new Protocol.Packet(stream, (byte)type, payload));
        if (controls.Count + bulk.Count + frames.Count > 512) { Fail("Companion queue full"); return; }
        for (var index = 0; index < frames.Count; index++)
            (stream == 0 ? controls : bulk).Enqueue((characteristic, frames[index],
                type == Protocol.Message.Hello && index == frames.Count - 1));
        Drain();
    }

    private async void Drain()
    {
        if (writing || subscribing || NeedsReconnect) return;
        writing = true;
        var epoch = generation;
        try
        {
            while (epoch == generation && !NeedsReconnect && (controls.Count > 0 || bulk.Count > 0))
            {
                // The peer must receive the whole HELLO before any control-stream messages.
                var queue = !helloSent ? bulk : controls.Count > 0 ? controls : bulk;
                if (queue.Count == 0) break;
                var item = queue.Dequeue();
                using var writer = new DataWriter(); writer.WriteBytes(item.Data);
                var result = await item.Characteristic.WriteValueAsync(writer.DetachBuffer(),
                    item.Characteristic == controlWrite ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(8));
                if (result != GattCommunicationStatus.Success) throw new IOException("Companion write: " + result);
                if (epoch == generation && item.HelloEnd) helloSent = true;
            }
        }
        catch (Exception error) { if (epoch == generation) Fail(error.Message); }
        finally { writing = false; if (epoch != generation) Drain(); }
    }

    private void SetDetail(string value) { if (Detail != value) { Detail = value; status(value); } }
    private void Fail(string reason) { SetActive(false); NeedsReconnect = true; ready = false; UpdateClipboardSession(true); SetDetail(reason); }
    public void ResetLink()
    {
        SetActive(false);
        generation++;
        if (controlRead is not null) controlRead.ValueChanged -= Notification;
        if (bulkRead is not null) bulkRead.ValueChanged -= Notification;
        controlRead = controlWrite = bulkRead = bulkWrite = null;
        controls.Clear(); bulk.Clear(); controlEncoder = new(); bulkEncoder = new(); controlDecoder = new(); bulkDecoder = new();
        ready = subscribing = NeedsReconnect = helloSent = false;
        availability = new();
        SupportsTakeover = false; RequestSerial = 0; requestedEpoch = disabledEpoch = ReleaseAcknowledged = null;
        handoff.Reset(); UpdateClipboardSession(true); clipboardPeer = false; desktop?.Send(new DesktopMessage("reset"));
    }
    public void Dispose() { if (disposed) return; disposed = true; ResetLink(); desktop?.Dispose(); desktop = null; }
}
