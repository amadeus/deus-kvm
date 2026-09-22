using DeusKVM.Companion.Core;
using Windows.Devices.Enumeration;

namespace DeusKVM.Companion;

internal sealed class ConnectMacForm : Form
{
    private readonly Dictionary<string, DeviceInformation> found = new(StringComparer.Ordinal);
    private readonly ListBox devices = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button connect = new() { Text = "Connect", AutoSize = true, Enabled = false };
    private readonly Button rescan = new() { Text = "Search again", AutoSize = true };
    private readonly Button cancel = new() { Text = "Cancel", AutoSize = true };
    private readonly CheckBox showAll = new() { Text = "Show all devices", AutoSize = true };
    private readonly HashSet<string> knownDeviceIds = new(StringComparer.Ordinal);
    private readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(490, 0) };
    private readonly CancellationTokenSource stop = new();
    private DeviceWatcher? watcher;
    private bool busy, closing;
    public ConnectMacForm()
    {
        Icon = AppIcon.Image;
        Text = "Add Mac"; AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(540, 440); MinimumSize = new Size(480, 400); StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Enable DeusKVM and open System Settings → Bluetooth on your Mac. Leave it open while choosing and pairing your Mac below.", AutoSize = true, MaximumSize = new Size(490, 0) });
        layout.Controls.Add(devices); layout.Controls.Add(showAll); layout.Controls.Add(status);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(connect); buttons.Controls.Add(rescan); buttons.Controls.Add(cancel); layout.Controls.Add(buttons);
        Controls.Add(layout); AcceptButton = connect; CancelButton = cancel;
        devices.SelectedIndexChanged += (_, _) => connect.Enabled = !busy && devices.SelectedItem is MacCandidate;
        showAll.CheckedChanged += (_, _) => Render();
        connect.Click += async (_, _) => await ConnectAsync();
        rescan.Click += (_, _) => Search(); cancel.Click += (_, _) => Close();
        Shown += (_, _) => Search();
        FormClosing += (_, args) =>
        {
            StopSearch();
            if (busy) { args.Cancel = true; closing = true; stop.Cancel(); status.Text = "Cancelling… Finish or dismiss any Windows pairing prompt."; }
        };
    }
    private void Post(DeviceWatcher sender, Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!IsDisposed && watcher == sender && !busy) action(); }); }
        catch (InvalidOperationException) { }
    }
    private void Search()
    {
        StopSearch(); found.Clear(); devices.Items.Clear(); status.Text = "Searching for computers… If your Mac is missing, try Show all devices.";
        try
        {
            knownDeviceIds.Clear();
            var selected = JsonFiles.Read<CompanionSettings>(Paths.Settings);
            if (selected is not null)
            {
                knownDeviceIds.Add(selected.DeviceId);
                if (selected.PairingDeviceId is not null) knownDeviceIds.Add(selected.PairingDeviceId);
            }
            var automatic = JsonFiles.Read<AutomaticMacSettings>(Paths.AutomaticMacs);
            if (automatic is not null)
                foreach (var mac in automatic.Macs)
                {
                    knownDeviceIds.Add(mac.DeviceId);
                    if (mac.PairingDeviceId is not null) knownDeviceIds.Add(mac.PairingDeviceId);
                }
            watcher = DeviceInformation.CreateWatcher(MacPairing.Selector, MacPairing.Properties, DeviceInformationKind.AssociationEndpoint);
            watcher.Added += (sender, info) => Post(sender, () => { found[info.Id] = info; Render(); });
            watcher.Updated += (sender, update) => Post(sender, () => { if (found.TryGetValue(update.Id, out var info)) info.Update(update); Render(); });
            watcher.Removed += (sender, update) => Post(sender, () => { found.Remove(update.Id); Render(); });
            watcher.EnumerationCompleted += (sender, _) => Post(sender, () => status.Text = "Choose your Mac. If missing, keep its Bluetooth settings open and try Show all devices or Search again.");
            watcher.Stopped += (sender, _) => Post(sender, () => status.Text = "Search stopped. Check Windows Bluetooth and choose Search again.");
            watcher.Start();
        }
        catch (Exception error) { status.Text = error.Message; StopSearch(); }
    }
    private void Render()
    {
        var selected = (devices.SelectedItem as MacCandidate)?.Id;
        var items = MacCandidates.Visible(found.Values.Select(info => new MacCandidate(info.Id, info.Name,
            info.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var address) ? address as string : null,
            info.Pairing.IsPaired, MacPairing.Transport(info),
            info.Properties.TryGetValue(MacPairing.MajorClassProperty, out var category) && category is ushort major ? major : null)),
            showAll.Checked, knownDeviceIds);
        devices.BeginUpdate(); devices.Items.Clear(); devices.Items.AddRange(items);
        devices.SelectedIndex = Array.FindIndex(items, item => item.Id == selected); devices.EndUpdate();
    }
    private void StopSearch()
    {
        var old = watcher; watcher = null;
        if (old?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) old.Stop();
    }
    private async Task ConnectAsync()
    {
        if (busy || devices.SelectedItem is not MacCandidate candidate) return;
        busy = true; connect.Enabled = rescan.Enabled = devices.Enabled = showAll.Enabled = false; StopSearch();
        try
        {
            await MacConnection.Connect(new MacPairing(candidate, message => status.Text = message, stop.Token));
            DialogResult = DialogResult.OK;
        }
        catch (OperationCanceledException) { status.Text = "Pairing cancelled or timed out. Existing Mac pairings are unchanged."; }
        catch (Exception error) { status.Text = error.Message; }
        finally
        {
            busy = false;
            if (DialogResult == DialogResult.OK || closing) Close();
            else
            {
                rescan.Enabled = devices.Enabled = showAll.Enabled = true;
                connect.Enabled = devices.SelectedItem is MacCandidate;
            }
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { StopSearch(); stop.Dispose(); }
        base.Dispose(disposing);
    }
}
