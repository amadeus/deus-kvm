using DeusKVM.Companion.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace DeusKVM.Companion;

// Pairing runs in the interactive tray's STA. Windows owns PIN/consent dialogs.
internal sealed class MacPairing(MacCandidate candidate, Action<string> progress, CancellationToken stop) : IMacConnection
{
    internal const string MajorClassProperty = "System.Devices.Aep.Bluetooth.Cod.Major";
    internal static readonly string[] Properties = ["System.Devices.Aep.IsPaired", "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.ProtocolId", MajorClassProperty];
    private static readonly Guid ClassicProtocol = new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
    internal static string Selector => string.Join(" OR ", new[] {
        BluetoothDevice.GetDeviceSelectorFromPairingState(true), BluetoothDevice.GetDeviceSelectorFromPairingState(false),
        BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), BluetoothLEDevice.GetDeviceSelectorFromPairingState(false)
    }.Select(query => $"({query})"));
    internal static MacTransport Transport(DeviceInformation info) =>
        info.Properties.TryGetValue("System.Devices.Aep.ProtocolId", out var protocol) &&
        Guid.TryParse(protocol?.ToString(), out var id) && id == ClassicProtocol ? MacTransport.Classic : MacTransport.LowEnergy;
    public async Task<bool> Pair()
    {
        stop.ThrowIfCancellationRequested();
        var info = await DeviceInformation.CreateFromIdAsync(candidate.Id, Properties, DeviceInformationKind.AssociationEndpoint);
        if (info.Pairing.IsPaired) return false;
        progress("Approve the Windows pairing prompt and any prompt on your Mac. Finish or dismiss that prompt before closing this window.");
        // Await the actual result, even if the window is closing, so a completed
        // pairing can be rolled back rather than left behind after cancellation.
        var result = await info.Pairing.PairAsync(DevicePairingProtectionLevel.Encryption);
        return result.Status switch
        {
            DevicePairingResultStatus.Paired => true,
            DevicePairingResultStatus.AlreadyPaired => false,
            _ => throw new InvalidOperationException($"Windows could not pair with {candidate.Name}: {result.Status}. Check that DeusKVM is enabled and advertising on the Mac, then try again.")
        };
    }
    public async Task<CompanionSettings> Verify()
    {
        stop.ThrowIfCancellationRequested();
        progress("Checking that this Mac is running DeusKVM…");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var classic = candidate.Transport == MacTransport.Classic
            ? await BluetoothDevice.FromIdAsync(candidate.Id).AsTask(timeout.Token) : null;
        if (candidate.Transport == MacTransport.Classic && classic is null)
            throw new InvalidOperationException("Windows could not open the paired Mac.");
        for (var attempt = 0; attempt < 10; attempt++)
        {
            // A Classic endpoint ID cannot be passed to BluetoothLEDevice.FromIdAsync.
            // Resolve only this physical Mac's public Bluetooth address, never its name.
            using var device = classic is null
                ? await BluetoothLEDevice.FromIdAsync(candidate.Id).AsTask(timeout.Token)
                : await BluetoothLEDevice.FromBluetoothAddressAsync(classic.BluetoothAddress, BluetoothAddressType.Public).AsTask(timeout.Token);
            if (device is null) { await Task.Delay(500, timeout.Token); continue; }
            var result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(timeout.Token);
            try
            {
                if (result.Status == GattCommunicationStatus.Success &&
                    result.Services.Any(service => service.Uuid == Protocol.Uuid(1)) &&
                    result.Services.Any(service => service.Uuid == new Guid("00001812-0000-1000-8000-00805f9b34fb")))
                    return new CompanionSettings(device.DeviceId, candidate.Name) { PairingDeviceId = candidate.Id };
            }
            finally { foreach (var service in result.Services) service.Dispose(); }
            await Task.Delay(500, timeout.Token);
        }
        throw new InvalidOperationException("DeusKVM was not found on this device. Enable DeusKVM on the Mac and try again. Existing Mac pairings are unchanged.");
    }
    public async Task Save(CompanionSettings settings)
    {
        stop.ThrowIfCancellationRequested();
        progress("Saving your Mac… Approve the administrator prompt if requested.");
        await ServiceCommands.ElevateAsync("--configure", ServiceCommands.EncodeSettings(settings));
    }
    public Task RollbackPairing() => Unpair(candidate.Id);
    internal static async Task Unpair(string id)
    {
        DeviceInformation info;
        try { info = await DeviceInformation.CreateFromIdAsync(id, Properties, DeviceInformationKind.AssociationEndpoint); }
        catch (Exception error) when (error.HResult == unchecked((int)0x80070490)) { return; } // endpoint no longer exists
        if (info is null || !info.Pairing.IsPaired) return;
        var result = await info.Pairing.UnpairAsync();
        if (result.Status is not (DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired))
            throw new InvalidOperationException($"Windows could not remove the selected Mac pairing: {result.Status}. Try removal again with Bluetooth turned on.");
    }
}
