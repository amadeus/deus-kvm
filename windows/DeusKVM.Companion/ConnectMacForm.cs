using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

// "Add Mac": lists the DeusKVM Macs Bonjour finds on the local network and saves the chosen
// one (by its control-link id) through the elevated --configure command. The service then
// connects to it; the Mac lists this PC once it connects, where Enable control finishes setup.
internal sealed class ConnectMacForm : Form
{
    private sealed record Choice(DiscoveredMac Mac, Guid Id)
    {
        public override string ToString() =>
            $"{Mac.Name}   ({string.Join(", ", Mac.Endpoints.Select(item => item.Address.ToString()).Distinct())})";
    }

    private readonly ListBox devices = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button connect = new() { Text = "Add", AutoSize = true, Enabled = false };
    private readonly Button cancel = new() { Text = "Cancel", AutoSize = true };
    private readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(490, 0) };
    private readonly HashSet<Guid> known = [];
    private NetworkMacDiscovery? discovery;
    private bool busy;

    public ConnectMacForm()
    {
        SuspendLayout();
        Icon = AppIcon.Image;
        Text = "Add Mac";
        Font = SystemFonts.MessageBoxFont;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(540, 400); MinimumSize = new Size(480, 360); StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = "Open DeusKVM on your Mac (same network as this PC), then choose it below. After adding it, turn on Enable control for this PC in the Mac's Setup.",
            AutoSize = true, MaximumSize = new Size(490, 0)
        });
        layout.Controls.Add(devices); layout.Controls.Add(status);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(connect); buttons.Controls.Add(cancel); layout.Controls.Add(buttons);
        Controls.Add(layout); AcceptButton = connect; CancelButton = cancel;
        ResumeLayout(true);
        devices.SelectedIndexChanged += (_, _) => connect.Enabled = !busy && devices.SelectedItem is Choice;
        connect.Click += async (_, _) => await AddAsync();
        cancel.Click += (_, _) => Close();
        Shown += (_, _) => Search();
        FormClosing += (_, args) => { if (busy) args.Cancel = true; else { discovery?.Dispose(); discovery = null; } };
    }

    private void Search()
    {
        status.Text = "Looking for Macs on the network…";
        var automatic = JsonFiles.Read<AutomaticMacSettings>(Paths.AutomaticMacs);
        foreach (var mac in automatic?.Macs ?? [])
            if (Guid.TryParse(mac.DeviceId, out var id)) known.Add(id);
        discovery = new NetworkMacDiscovery(Render, message => status.Text = message, hostsFile: null);
    }

    private void Render(IReadOnlyList<DiscoveredMac> macs)
    {
        if (IsDisposed || busy) return;
        var selected = (devices.SelectedItem as Choice)?.Id;
        var choices = macs.Where(mac => mac.Id is not null).Select(mac => new Choice(mac, mac.Id!.Value))
            .OrderBy(choice => choice.Mac.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        devices.BeginUpdate(); devices.Items.Clear(); devices.Items.AddRange(choices);
        devices.SelectedIndex = Array.FindIndex(choices, choice => choice.Id == selected); devices.EndUpdate();
        var added = choices.Count(choice => known.Contains(choice.Id));
        status.Text = choices.Length == 0
            ? "No Mac found yet. Make sure DeusKVM is running on the Mac and both are on the same network."
            : added > 0 ? $"Found {choices.Length}; {added} already added (choose it again to keep it)." : $"Found {choices.Length}. Choose your Mac.";
    }

    private async Task AddAsync()
    {
        if (busy || devices.SelectedItem is not Choice choice) return;
        busy = true; connect.Enabled = devices.Enabled = false;
        try
        {
            var settings = new CompanionSettings(choice.Id.ToString(), choice.Mac.Name);
            settings.Validate();
            status.Text = "Saving your Mac… Approve the administrator prompt if requested.";
            await ServiceCommands.ElevateAsync("--configure", ServiceCommands.EncodeSettings(settings));
            DialogResult = DialogResult.OK;
        }
        catch (Exception error) { status.Text = error.Message; }
        finally
        {
            busy = false;
            if (DialogResult == DialogResult.OK) Close();
            else { devices.Enabled = true; connect.Enabled = devices.SelectedItem is Choice; }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { discovery?.Dispose(); discovery = null; }
        base.Dispose(disposing);
    }
}
