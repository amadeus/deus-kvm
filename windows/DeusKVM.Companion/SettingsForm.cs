using System.ServiceProcess;
using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

internal sealed class SettingsForm : Form
{
    private readonly Button connect = new() { Text = "Add Mac…", AutoSize = true };
    private readonly Label selected = new() { AutoSize = true, MaximumSize = new Size(460, 0) };
    private readonly Label state = new() { AutoSize = true, MaximumSize = new Size(460, 0) };
    private readonly Label detail = new() { AutoSize = true, MaximumSize = new Size(460, 0) };
    private readonly Button startService = new() { Text = "Start Service", AutoSize = true };
    private readonly Button stopService = new() { Text = "Stop Service", AutoSize = true };
    private readonly CheckBox automatic = new() { Text = "Start service with Windows (even when signed out)", AutoSize = true };
    private readonly CheckBox trayStartup = new() { Text = "Show tray icon when users sign in", AutoSize = true };
    private readonly Button remove = new() { Text = "Remove DeusKVM from this PC…", AutoSize = true };

    public SettingsForm(Func<string[], Task> control, Action connectMac, Func<Task> removeApp)
    {
        Icon = AppIcon.Image;
        Text = "DeusKVM Companion"; AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(530, 490); MinimumSize = new Size(530, 490);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, AutoScroll = true };
        layout.Controls.Add(selected); layout.Controls.Add(connect);
        layout.Controls.Add(state); layout.Controls.Add(detail);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(startService); buttons.Controls.Add(stopService); layout.Controls.Add(buttons);
        layout.Controls.Add(automatic); layout.Controls.Add(trayStartup);
        layout.Controls.Add(new Label { Text = "Closing this window leaves the service running. Open DeusKVM Companion again or use its tray icon to return here.",
            AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(0, 12, 0, 12) });
        layout.Controls.Add(remove); Controls.Add(layout);
        connect.Click += (_, _) => connectMac();
        startService.Click += async (_, _) => await control(["--start"]);
        stopService.Click += async (_, _) => await control(["--stop"]);
        automatic.Click += async (_, _) => await control(["--startup", automatic.Checked ? "auto" : "manual"]);
        trayStartup.Click += async (_, _) => await control(["--tray-startup", trayStartup.Checked ? "on" : "off"]);
        remove.Click += async (_, _) => await removeApp();
    }
    public void RefreshControls(ServiceControllerStatus? service, bool autoStart, bool trayAtLogin, bool busy)
    {
        startService.Enabled = !busy && service == ServiceControllerStatus.Stopped;
        stopService.Enabled = !busy && service == ServiceControllerStatus.Running;
        automatic.Enabled = !busy && service is not null; automatic.Checked = autoStart;
        trayStartup.Enabled = connect.Enabled = remove.Enabled = !busy;
        trayStartup.Checked = trayAtLogin;
    }
    public void RefreshStatus(string serviceState)
    {
        try
        {
            selected.Text = "Waiting for a paired Mac";
            state.Text = $"Service: {serviceState}";
            var snapshot = JsonFiles.Read<ServiceSnapshot>(Paths.Status);
            if (serviceState != "Running" || snapshot is null) { detail.Text = "Bluetooth recovery is not running."; return; }
            if (!ServicePolicy.IsFresh(snapshot.UpdatedAt, DateTimeOffset.UtcNow)) { detail.Text = "Waiting for a fresh service status…"; return; }
            selected.Text = snapshot.Worker.DeviceName is { } active ? $"Active: {active}" : "Waiting for a paired Mac";
            if (snapshot.Worker.WaitingMacs is { Length: > 0 } waiting)
                selected.Text += $"\nWaiting: {string.Join(", ", waiting)}";
            if (snapshot.Worker.PausedMacs is { Length: > 0 } paused)
                selected.Text += $"\nDisabled or not allowed: {string.Join(", ", paused)}";
            detail.Text = $"Bluetooth: {snapshot.Worker.State}\n{snapshot.Worker.Detail}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { detail.Text = "Service status is temporarily unavailable."; }
    }
}
