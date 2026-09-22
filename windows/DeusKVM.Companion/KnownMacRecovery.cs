using DeusKVM.Companion.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace DeusKVM.Companion;

// Restore the old worker's reconnect support for every previously verified Mac.
// This requests only a transport connection; it never grants input ownership.
internal sealed class KnownMacRecovery(Action<string> log) : IDisposable
{
    private readonly Dictionary<string, Connection> connections = new(StringComparer.Ordinal);
    private bool disposed;

    public void Refresh(IEnumerable<CompanionSettings> known)
    {
        if (disposed) return;
        foreach (var mac in known)
        {
            if (connections.TryGetValue(mac.DeviceId, out var existing))
            {
                if (!existing.Finished || existing.Session is not null || Environment.TickCount64 < existing.RetryAt) continue;
                existing.Dispose();
            }
            var connection = new Connection(mac, log);
            connections[mac.DeviceId] = connection;
            _ = connection.Open();
        }
    }

    public void Dispose()
    {
        disposed = true;
        foreach (var connection in connections.Values) connection.Dispose();
        connections.Clear();
    }

    private sealed class Connection(CompanionSettings mac, Action<string> log) : IDisposable
    {
        private BluetoothLEDevice? device;
        private bool disposed;
        public GattSession? Session { get; private set; }
        public bool Finished { get; private set; }
        public long RetryAt { get; private set; }

        public async Task Open()
        {
            try
            {
                var info = await DeviceInformation.CreateFromIdAsync(mac.DeviceId,
                    ["System.Devices.Aep.IsPaired"], DeviceInformationKind.AssociationEndpoint);
                if (disposed || !info.Pairing.IsPaired) return;
                var opened = await BluetoothLEDevice.FromIdAsync(mac.DeviceId);
                if (disposed) { opened?.Dispose(); return; }
                device = opened;
                if (device is null) return;
                var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
                if (disposed) { session?.Dispose(); return; }
                Session = session;
                if (Session?.CanMaintainConnection == true) Session.MaintainConnection = true;
            }
            catch (Exception error)
            {
                if (!disposed) log($"Reconnect request for {mac.DeviceName}: {error.Message}");
            }
            finally { Finished = true; RetryAt = Environment.TickCount64 + 30000; }
        }

        public void Dispose()
        {
            disposed = true;
            if (Session is not null) { Session.MaintainConnection = false; Session.Dispose(); Session = null; }
            device?.Dispose(); device = null;
        }
    }
}
