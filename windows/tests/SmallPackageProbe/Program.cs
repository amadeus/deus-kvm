using System.ServiceProcess;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Forms;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

// Dependency-size / API-compilation probe, not a replacement companion.
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var channel = Channel.CreateBounded<string>(1);
        channel.Writer.TryWrite(JsonSerializer.Serialize(new { Probe = true }));
        Application.EnableVisualStyles();
        using var form = new Form { Text = "DeusKVM dependency probe — not the app" };
        var button = new Button { Text = "Check first paired BLE device", Dock = DockStyle.Fill };
        button.Click += async (_, _) =>
        {
            button.Enabled = false;
            try { button.Text = $"Services: {await CheckBluetoothAsync()}"; }
            catch (Exception e) { button.Text = e.Message; }
            finally { button.Enabled = true; }
        };
        form.Controls.Add(button);
        Application.Run(form);
    }

    private static async Task<int> CheckBluetoothAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
        if (devices.Count == 0) return 0;
        using var device = await BluetoothLEDevice.FromIdAsync(devices[0].Id);
        if (device == null) return 0;
        using var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
        session.MaintainConnection = true;
        var result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"GATT discovery: {result.Status}");
        foreach (var service in result.Services) service.Dispose();
        return result.Services.Count;
    }
}

// Resolves the OS-supplied service assembly; does not install or start a service.
internal sealed class ProbeService : ServiceBase { }
