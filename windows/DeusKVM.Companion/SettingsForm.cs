using System.ServiceProcess;
using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

internal sealed class SettingsForm : Form
{
    private readonly Button connect = new() { Text = "Add Mac…", AutoSize = true };
    private readonly Label selected = new() { AutoSize = true, MaximumSize = new Size(420, 0) };
    private readonly Label state = new() { AutoSize = true, MaximumSize = new Size(420, 0) };
    private readonly Label detail = new() { AutoSize = true, MaximumSize = new Size(420, 0) };
    private readonly Button startService = new() { Text = "Start Service", AutoSize = true };
    private readonly Button stopService = new() { Text = "Stop Service", AutoSize = true };
    private readonly CheckBox automatic = new() { Text = "Start service with Windows (even when signed out)", AutoSize = true };
    private readonly CheckBox trayStartup = new() { Text = "Show tray icon when users sign in", AutoSize = true };
    private readonly TableLayoutPanel layout = new();
    private readonly Button remove = new() { Text = "Remove DeusKVM from this PC…", AutoSize = true };
    // Windows' own Touchpad page does not list the Mac trackpad, so its feel is set here.
    private readonly ITrackpadStore trackpad;
    private readonly GroupBox trackpadGroup = new() { Text = "Mac trackpad", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Label cursorSpeedLabel = new() { AutoSize = true };
    private readonly TrackBar cursorSpeed = new() { Minimum = 1, Maximum = TrackpadSettings.CursorSpeedNotches, TickFrequency = 1, LargeChange = 1, Width = 300 };
    private readonly Label sensitivityLabel = new() { AutoSize = true };
    private readonly TrackBar sensitivity = new() { Minimum = 0, Maximum = TrackpadSettings.SensitivityLabels.Length - 1, TickFrequency = 1, LargeChange = 1, Width = 300 };
    private readonly CheckBox reverseScroll = new() { Text = "Reverse scrolling direction", AutoSize = true };
    private readonly LinkLabel resetTrackpad = new() { Text = "Reset to Windows defaults", AutoSize = true };
    private bool loadingTrackpad;

    public SettingsForm(Func<string[], Task> control, Action connectMac, Func<Task> removeApp, ITrackpadStore trackpad)
    {
        this.trackpad = trackpad;
        SuspendLayout(); layout.SuspendLayout();
        Icon = AppIcon.Image;
        Text = "DeusKVM Companion";
        Font = SystemFonts.MessageBoxFont;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        layout.AutoSize = true; layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        layout.Padding = new Padding(12); layout.Margin = Padding.Empty;
        layout.ColumnCount = 1;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.Controls.Add(selected); layout.Controls.Add(connect);
        layout.Controls.Add(state); layout.Controls.Add(detail);
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, Margin = Padding.Empty };
        startService.Margin = new Padding(0, 0, 6, 0); stopService.Margin = Padding.Empty;
        buttons.Controls.Add(startService); buttons.Controls.Add(stopService); layout.Controls.Add(buttons);
        layout.Controls.Add(automatic); layout.Controls.Add(trayStartup);
        layout.Controls.Add(BuildTrackpadGroup());
        var closingHint = new Label { Text = "Closing this window leaves the service running. Open DeusKVM Companion again or use its tray icon to return here.",
            AutoSize = true, MaximumSize = new Size(420, 0) };
        layout.Controls.Add(closingHint); layout.Controls.Add(remove);
        layout.RowCount = layout.Controls.Count;
        foreach (Control item in layout.Controls)
        {
            item.Margin = new Padding(0, 0, 0, 6);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        buttons.Margin = new Padding(0, 6, 0, 6);
        closingHint.Margin = new Padding(0, 6, 0, 12);
        remove.Margin = Padding.Empty;
        Controls.Add(layout);
        layout.ResumeLayout(true); ResumeLayout(true);
        connect.Click += (_, _) => connectMac();
        startService.Click += async (_, _) => await control(["--start"]);
        stopService.Click += async (_, _) => await control(["--stop"]);
        automatic.Click += async (_, _) => await control(["--startup", automatic.Checked ? "auto" : "manual"]);
        trayStartup.Click += async (_, _) => await control(["--tray-startup", trayStartup.Checked ? "on" : "off"]);
        remove.Click += async (_, _) => await removeApp();
        cursorSpeed.Scroll += (_, _) => ApplyTrackpad(() => TrackpadSettings.SetCursorSpeed(trackpad, cursorSpeed.Value));
        sensitivity.Scroll += (_, _) => ApplyTrackpad(() => TrackpadSettings.SetSensitivity(trackpad, sensitivity.Value));
        reverseScroll.Click += (_, _) => ApplyTrackpad(() => TrackpadSettings.SetScrollReversed(trackpad, reverseScroll.Checked));
        resetTrackpad.LinkClicked += (_, _) => ApplyTrackpad(() => TrackpadSettings.Reset(trackpad));
        LoadTrackpad();
    }

    private GroupBox BuildTrackpadGroup()
    {
        var rows = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, ColumnCount = 1 };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var hint = new Label
        {
            Text = "Applies to the Mac trackpad only; mouse settings stay in Windows Settings. If a change has no effect, sign out and back in.",
            AutoSize = true, MaximumSize = new Size(400, 0)
        };
        foreach (Control item in new Control[] { cursorSpeedLabel, cursorSpeed, sensitivityLabel, sensitivity, reverseScroll, resetTrackpad, hint })
        {
            item.Margin = new Padding(0, 0, 0, 4);
            rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rows.Controls.Add(item);
        }
        rows.RowCount = rows.Controls.Count;
        rows.Padding = new Padding(6, 2, 6, 2);
        trackpadGroup.Controls.Add(rows);
        return trackpadGroup;
    }

    /// <summary>Writes the value Windows' Settings page would write, then shows what the registry now holds.</summary>
    private void ApplyTrackpad(Action change)
    {
        try { change(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { TrayContext.ShowError(error); }
        LoadTrackpad();
    }

    private void LoadTrackpad()
    {
        if (loadingTrackpad) return;
        loadingTrackpad = true;
        try
        {
            var state = TrackpadSettings.Read(trackpad);
            cursorSpeed.Value = state.CursorSpeedNotch;
            sensitivity.Value = state.SensitivityIndex;
            reverseScroll.Checked = state.ScrollReversed;
            cursorSpeedLabel.Text = $"Cursor speed: {state.CursorSpeedNotch}";
            sensitivityLabel.Text = $"Touchpad sensitivity: {TrackpadSettings.SensitivityLabels[state.SensitivityIndex]}";
            resetTrackpad.Enabled = !state.IsDefault;
            trackpadGroup.Enabled = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            cursorSpeedLabel.Text = "Cursor speed: unavailable";
            sensitivityLabel.Text = $"Touchpad sensitivity: {error.Message}";
            trackpadGroup.Enabled = false;
        }
        finally { loadingTrackpad = false; }
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
        // Apply the final status before the content-sized form lays out again.
        SuspendLayout(); layout.SuspendLayout();
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
        finally { layout.ResumeLayout(true); ResumeLayout(true); }
    }
}
