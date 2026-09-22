using DeusKVM.Companion.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace DeusKVM.Companion;

// Owns one connection lifetime. A superseded asynchronous discovery may finish,
// but its results are disposed here and cannot reach a replacement session.
internal sealed class MacSession : IDisposable
{
    private static readonly Guid HidUuid = new("00001812-0000-1000-8000-00805f9b34fb");
    private readonly SynchronizationContext context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    private readonly Action<bool> connectionChanged;
    private readonly Action<string> detail;
    private readonly List<GattDeviceService> services = [];
    private BluetoothLEDevice? device;
    private GattSession? session;
    private BluetoothControl? control;
    private bool disposed, changed;
    public ConnectedMac Mac { get; }
    public bool Verified { get; private set; }
    public bool Unsupported { get; private set; }
    public bool Attached => control?.Attached == true;
    public bool Ready => control?.Ready == true;
    public bool Available => control?.Available == true;
    public bool SupportsSelection => control?.SupportsSelection == true;
    public bool SupportsTakeover => control?.SupportsTakeover == true;
    public long RequestSerial => control?.RequestSerial ?? 0;
    public uint? ReleaseAcknowledged => control?.ReleaseAcknowledged;
    public void RequestDisable(uint? epoch = null) => control?.RequestDisable(epoch);
    public uint AvailabilityEpoch => control?.AvailabilityEpoch ?? 0;
    public bool Active => control?.Active == true;
    public void SetActive(bool active) => control?.SetActive(active);
    public bool NeedsReconnect => changed || control?.NeedsReconnect == true;
    public int ServiceCount => services.Count;
    public string? Error { get; private set; }

    public MacSession(ConnectedMac mac, Action<bool> connectionChanged, Action<string> detail)
    {
        Mac = mac; this.connectionChanged = connectionChanged; this.detail = detail;
    }

    public async Task Open()
    {
        try
        {
            var opened = await BluetoothLEDevice.FromIdAsync(Mac.Id);
            if (disposed) { opened?.Dispose(); return; }
            device = opened ?? throw new IOException("Windows denied service-account access to this paired BLE endpoint.");
            device.ConnectionStatusChanged += ConnectionChanged;
            device.GattServicesChanged += ServicesChanged;
            if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            { connectionChanged(false); return; }
            // Only an observed connected candidate is actively probed.
            var result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (disposed) { foreach (var service in result.Services) service.Dispose(); return; }
            services.AddRange(result.Services);
            if (result.Status != GattCommunicationStatus.Success)
                throw new IOException($"Uncached discovery: {result.Status}; ATT error: {result.ProtocolError}");
            var companion = services.FirstOrDefault(service => service.Uuid == Protocol.Uuid(1));
            Verified = companion is not null && services.Any(service => service.Uuid == HidUuid);
            if (!Verified) { Unsupported = true; return; }
            // Companion handshakes stay alive for paused/waiting Macs; only the
            // elected connection can create a desktop or clipboard session.
        }
        catch (Exception error) { if (!disposed) Error = Describe(error); }
    }

    public async Task Attach()
    {
        try
        {
            if (disposed || !Verified || device is null) return;
            var opened = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
            if (disposed) { opened?.Dispose(); return; }
            session = opened;
            if (session is not null && session.CanMaintainConnection) session.MaintainConnection = true;
            control = new BluetoothControl(value => { if (!disposed) detail(value); });
            await control.Attach(services.First(service => service.Uuid == Protocol.Uuid(1)), CancellationToken.None);
        }
        catch (Exception error) { if (!disposed) Error = Describe(error); }
    }

    public void Tick()
    {
        if (!disposed && device is not null && control is not null)
            control.Tick(device.BluetoothAddress.ToString("X12"));
    }

    private void ConnectionChanged(BluetoothLEDevice sender, object args)
    {
        var connected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
        context.Post(_ => { if (!disposed && sender == device) connectionChanged(connected); }, null);
    }
    private void ServicesChanged(BluetoothLEDevice sender, object args) => context.Post(_ =>
    {
        if (!disposed && sender == device) changed = true;
    }, null);
    private static string Describe(Exception error) => $"{error.Message} (HRESULT 0x{error.HResult:X8}).";

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        control?.Dispose(); control = null;
        foreach (var service in services) service.Dispose(); services.Clear();
        if (session is not null) { session.MaintainConnection = false; session.Dispose(); session = null; }
        if (device is not null)
        {
            device.ConnectionStatusChanged -= ConnectionChanged; device.GattServicesChanged -= ServicesChanged;
            device.Dispose(); device = null;
        }
    }
}
