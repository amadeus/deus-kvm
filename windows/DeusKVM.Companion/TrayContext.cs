using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon icon;
    private readonly ToolStripMenuItem statusItem = new("Checking service…") { Enabled = false };
    private readonly ToolStripMenuItem startItem = new("Start Service");
    private readonly ToolStripMenuItem stopItem = new("Stop Service");
    private readonly ToolStripMenuItem automaticItem = new("Start automatically with Windows");
    private readonly ToolStripMenuItem trayStartupItem = new("Show tray icon at sign-in");
    private readonly ToolStripMenuItem removeItem = new("Remove DeusKVM from this PC…");
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 2000 };
    private SettingsForm? settings;
    private bool busy;

    public TrayContext(EventWaitHandle showSettings, bool openSettings = true)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startItem);
        menu.Items.Add(stopItem);
        menu.Items.Add(automaticItem);
        menu.Items.Add(trayStartupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Open diagnostics", null, (_, _) => OpenDiagnostics());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(removeItem);
        menu.Items.Add("Quit Tray", null, (_, _) => ExitThread());
        startItem.Click += async (_, _) => await ControlAsync("--start");
        stopItem.Click += async (_, _) => await ControlAsync("--stop");
        automaticItem.Click += async (_, _) => await ControlAsync("--startup", automaticItem.Checked ? "manual" : "auto");
        trayStartupItem.Click += async (_, _) => await ControlAsync("--tray-startup", trayStartupItem.Checked ? "off" : "on");
        removeItem.Click += async (_, _) => await RemoveAsync();
        icon = new NotifyIcon { Icon = AppIcon.Image, Text = "DeusKVM Companion", ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => ShowSettings();
        refresh.Tick += (_, _) =>
        {
            if (showSettings.WaitOne(0)) ShowSettings();
            RefreshStatus();
        };
        refresh.Start();
        RefreshStatus();
        if (openSettings) ShowSettings();
    }

    private async Task ControlAsync(params string[] args)
    {
        if (busy) return;
        busy = true;
        RefreshStatus();
        try { await ServiceCommands.ElevateAsync(args); }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { }
        catch (Exception error) { ShowError(error); }
        finally { busy = false; RefreshStatus(); }
    }

    private void RefreshStatus()
    {
        trayStartupItem.Enabled = removeItem.Enabled = !busy;
        try
        {
            trayStartupItem.Checked = TrayStartup.Enabled;
            using var service = new ServiceController(Paths.ServiceName);
            var state = service.Status;
            statusItem.Text = $"Service: {state}";
            startItem.Enabled = !busy && state == ServiceControllerStatus.Stopped;
            stopItem.Enabled = !busy && state == ServiceControllerStatus.Running;
            automaticItem.Enabled = !busy;
            automaticItem.Checked = service.StartType == ServiceStartMode.Automatic;
            icon.Text = $"DeusKVM: {state}";
            if (state == ServiceControllerStatus.Running)
            {
                var snapshot = JsonFiles.Read<ServiceSnapshot>(Paths.Status);
                if (snapshot is not null && ServicePolicy.IsFresh(snapshot.UpdatedAt, DateTimeOffset.UtcNow))
                    statusItem.Text = snapshot.Worker.DeviceName is { } name ? $"Active: {name}" : "Waiting for a paired Mac";
            }
            settings?.RefreshControls(state, automaticItem.Checked, trayStartupItem.Checked, busy);
            settings?.RefreshStatus(state.ToString());
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or UnauthorizedAccessException or System.Security.SecurityException or IOException or System.Text.Json.JsonException)
        {
            statusItem.Text = "Service unavailable — reopen DeusKVM Companion to repair";
            startItem.Enabled = stopItem.Enabled = automaticItem.Enabled = false;
            settings?.RefreshControls(null, false, trayStartupItem.Checked, busy);
            settings?.RefreshStatus("Not installed or inaccessible");
        }
    }

    private void ShowSettings()
    {
        if (settings is null || settings.IsDisposed) settings = new SettingsForm(ControlAsync, ConnectMac, RemoveAsync);
        settings.Show();
        if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
        settings.Activate();
        RefreshStatus();
    }

    private void ConnectMac()
    {
        if (busy) return;
        busy = true; RefreshStatus();
        try { using var dialog = new ConnectMacForm(); dialog.ShowDialog(settings); }
        finally { busy = false; RefreshStatus(); }
    }

    private async Task RemoveAsync()
    {
        if (busy) return;
        if (MessageBox.Show(settings,
            "Remove DeusKVM, its service, startup settings and saved data from this PC? Windows Bluetooth pairings are kept.",
            "Remove DeusKVM from this PC", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        await ControlAsync("--remove");
    }

    private static void OpenDiagnostics()
    {
        try { Process.Start(new ProcessStartInfo(Paths.DataDirectory) { UseShellExecute = true }); }
        catch (Exception error) { ShowError(error); }
    }

    internal static void ShowError(Exception error) =>
        MessageBox.Show(error.Message, "DeusKVM Companion", MessageBoxButtons.OK, MessageBoxIcon.Error);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            refresh.Dispose();
            icon.Visible = false;
            icon.Dispose();
            settings?.Dispose();
        }
        base.Dispose(disposing);
    }
}
