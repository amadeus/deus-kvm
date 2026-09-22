using DeusKVM.Companion.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace DeusKVM.Companion;

// Passive paired-LE enumeration. Never opens GATT or enables MaintainConnection.
internal sealed class PairedMacDiscovery : IDisposable
{
    private const string ConnectedProperty = "System.Devices.Aep.IsConnected";
    private readonly Dictionary<string, DeviceInformation> found = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> connections = new(StringComparer.Ordinal);
    private readonly SynchronizationContext context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    private readonly MacPriority priority;
    private readonly Action<string> log;
    private readonly DeviceWatcher watcher;
    private bool disposed;
    public string? Error { get; private set; }

    public PairedMacDiscovery(MacPriority priority, Action<string> log)
    {
        this.priority = priority; this.log = log;
        watcher = DeviceInformation.CreateWatcher(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
            [ConnectedProperty, "System.Devices.Aep.IsPaired", "System.Devices.Aep.DeviceAddress"],
            DeviceInformationKind.AssociationEndpoint);
        watcher.Added += (_, info) => Post(() =>
        {
            found[info.Id] = info;
            connections[info.Id] = IsConnected(info);
            Reconcile();
        });
        watcher.Updated += (_, update) => Post(() =>
        {
            if (!found.TryGetValue(update.Id, out var info)) return;
            info.Update(update);
            if (update.Properties.ContainsKey(ConnectedProperty)) connections[info.Id] = IsConnected(info);
            Reconcile();
        });
        watcher.Removed += (_, update) => Post(() =>
        {
            found.Remove(update.Id); connections.Remove(update.Id); Reconcile();
        });
        watcher.EnumerationCompleted += (_, _) => Post(() =>
        {
            priority.CompleteEnumeration(); log("Initial paired-device enumeration complete.");
        });
        watcher.Stopped += (_, _) => Post(() => Error = "Paired Bluetooth discovery stopped; restarting the worker.");
        watcher.Start();
    }

    public void ConnectionChanged(string id, bool connected)
    {
        if (disposed) return;
        if (found.ContainsKey(id)) connections[id] = connected;
        Reconcile();
    }

    private static bool IsConnected(DeviceInformation info) =>
        info.Properties.TryGetValue(ConnectedProperty, out var value) && value is true;

    private void Reconcile()
    {
        // The LE selector excludes Classic aliases; group any duplicate LE endpoints
        // by address and retain an existing endpoint while it is still connected.
        var previous = priority.Connections;
        var candidates = found.Values.Where(info => info.Pairing.IsPaired && connections.GetValueOrDefault(info.Id))
            .Select(info => new { Info = info, Address = Address(info) })
            .Where(item => item.Address.Length == 12)
            .GroupBy(item => item.Address, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => !previous.Any(old => old.Id == item.Info.Id))
                .ThenBy(item => item.Info.Id, StringComparer.Ordinal).First()).ToArray();
        var ids = candidates.Select(item => item.Info.Id).ToHashSet(StringComparer.Ordinal);
        // Include rejected and initial-enumeration observations, not just eligible candidates.
        foreach (var old in priority.Connections.Where(item => !ids.Contains(item.Id)).ToArray())
        {
            priority.Remove(old.Id); log($"Disconnected: {old.Name}; endpoint={old.Id}");
        }
        foreach (var item in candidates)
        {
            var fresh = !priority.Connections.Any(old => old.Id == item.Info.Id);
            var name = new string(item.Info.Name.Where(character => !char.IsControl(character)).Take(256).ToArray()).Trim();
            if (name.Length == 0) name = "Paired Bluetooth device";
            priority.Observe(item.Info.Id, name, item.Address, true);
            if (fresh) log($"Connected: {name}; endpoint={item.Info.Id}; address={item.Address}");
        }
    }

    private static string Address(DeviceInformation info) =>
        info.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var value) && value is string address
            ? new string(address.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant() : "";

    private void Post(Action action) => context.Post(_ =>
    {
        if (disposed) return;
        try { action(); }
        catch (Exception error) { Error = $"Paired Bluetooth discovery failed: {error.Message}"; }
    }, null);

    public void Dispose()
    {
        disposed = true;
        if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
    }
}
