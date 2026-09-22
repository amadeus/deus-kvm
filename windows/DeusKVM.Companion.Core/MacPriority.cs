namespace DeusKVM.Companion.Core;

public sealed record ConnectedMac(string Id, string Name, string Address, long Connection);

// One instance per BLE worker. All observations and elections are serialized by its STA.
public sealed class MacPriority(string? startupPreference = null, bool requireAvailability = false)
{
    private readonly Dictionary<string, ConnectedMac> connected = new(StringComparer.Ordinal);
    private readonly HashSet<long> rejected = [];
    private readonly HashSet<long> unavailable = [], previouslyAvailable = [];
    private readonly Dictionary<long, long> order = [];
    private long nextConnection, nextOrder;
    private bool enumerated;
    public ConnectedMac? Active { get; private set; }
    public bool Enumerated => enumerated;
    public ConnectedMac[] Connections => connected.Values.ToArray();

    public void Observe(string id, string name, string address, bool isConnected)
    {
        if (!isConnected) { Remove(id); return; }
        if (connected.TryGetValue(id, out var previous))
        {
            // Name/property refreshes are not new connections.
            connected[id] = previous with { Name = name, Address = address };
            if (Active?.Connection == previous.Connection) Active = connected[id];
            return;
        }
        var mac = new ConnectedMac(id, name, address, ++nextConnection);
        connected.Add(id, mac);
        if (requireAvailability) unavailable.Add(mac.Connection);
        order[mac.Connection] = enumerated ? ++nextOrder : 0;
    }

    public void CompleteEnumeration()
    {
        if (enumerated) return;
        foreach (var mac in connected.Values.OrderBy(item => item.Id != startupPreference)
                     .ThenBy(item => item.Id, StringComparer.Ordinal)) order[mac.Connection] = ++nextOrder;
        enumerated = true;
    }

    public void Remove(string id)
    {
        if (!connected.Remove(id, out var mac)) return;
        rejected.Remove(mac.Connection); order.Remove(mac.Connection);
        unavailable.Remove(mac.Connection); previouslyAvailable.Remove(mac.Connection);
        if (Active?.Connection == mac.Connection) Active = null;
    }

    public bool IsCurrent(ConnectedMac mac) =>
        connected.TryGetValue(mac.Id, out var current) && current.Connection == mac.Connection;

    public ConnectedMac[] DiscoveryCandidates => !enumerated ? [] : connected.Values
        .Where(item => !rejected.Contains(item.Connection))
        .OrderBy(item => order[item.Connection]).ToArray();

    public ConnectedMac[] Candidates => DiscoveryCandidates.Where(item => !unavailable.Contains(item.Connection)).ToArray();

    public void SetAvailable(ConnectedMac mac, bool available)
    {
        if (!IsCurrent(mac)) return;
        if (!available)
        {
            unavailable.Add(mac.Connection);
            if (Active?.Connection == mac.Connection) Active = null;
        }
        else
        {
            if (unavailable.Remove(mac.Connection) && previouslyAvailable.Contains(mac.Connection))
                order[mac.Connection] = ++nextOrder;
            previouslyAvailable.Add(mac.Connection);
        }
    }

    public ConnectedMac? Next => Active ?? Candidates.FirstOrDefault();

    public bool Activate(ConnectedMac mac)
    {
        if (!IsCurrent(mac) || Next?.Connection != mac.Connection) return false;
        Active = connected[mac.Id];
        return true;
    }

    public bool SelectRequested(ConnectedMac mac)
    {
        if (!IsCurrent(mac) || !Candidates.Any(item => item.Connection == mac.Connection)) return false;
        Active = connected[mac.Id];
        return true;
    }

    public void Reject(ConnectedMac mac)
    {
        if (!IsCurrent(mac)) return;
        rejected.Add(mac.Connection);
        if (Active?.Connection == mac.Connection) Active = null;
    }

    public void Reconsider(ConnectedMac mac)
    {
        if (IsCurrent(mac)) rejected.Remove(mac.Connection);
    }
}

// Written only by the service-owned BLE worker, separate from interactive pairing hints.
public sealed record AutomaticMacSettings(string? LastActiveId, CompanionSettings[] Macs)
{
    public static AutomaticMacSettings Migrate(CompanionSettings? legacy)
    {
        legacy?.Validate();
        return new(legacy?.DeviceId, legacy is null ? [] : [legacy]);
    }

    public AutomaticMacSettings Remember(CompanionSettings mac, bool active = false)
    {
        mac.Validate();
        var previous = Macs.FirstOrDefault(item => item.DeviceId == mac.DeviceId);
        var saved = mac with { PairingDeviceId = mac.PairingDeviceId ?? previous?.PairingDeviceId };
        return new(active ? mac.DeviceId : LastActiveId,
            Macs.Where(item => item.DeviceId != mac.DeviceId).Append(saved).ToArray());
    }

    public void Validate()
    {
        if (Macs is null || Macs.Length > 256 || Macs.Select(item => item.DeviceId).Distinct(StringComparer.Ordinal).Count() != Macs.Length)
            throw new ArgumentException("Invalid saved Mac list.");
        foreach (var mac in Macs) mac.Validate();
        if (LastActiveId is not null && !Macs.Any(item => item.DeviceId == LastActiveId))
            throw new ArgumentException("The last active Mac is missing from the saved list.");
    }
}
