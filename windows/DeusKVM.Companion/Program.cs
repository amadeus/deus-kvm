using System.Diagnostics;
using System.ServiceProcess;

namespace DeusKVM.Companion;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var quiet = args.LastOrDefault() == "--quiet";
        if (quiet) args = args.Take(args.Length - 1).ToArray();
        if (args is ["--service"])
        {
            ServiceBase.Run(new CompanionService());
            return 0;
        }

        // a dedicated STA with a message loop satisfies WinRT's UI-thread contract.
        // it runs under the service identity/session, with no interactive windows.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args is ["--ble-worker"])
        {
            using var worker = new BluetoothWorker();
            Application.Run(worker);
            return worker.ExitCode;
        }

        if (args is ["--desktop-worker", var pipe, var parent, var address] && int.TryParse(parent, out var pid) &&
            address.Length == 12 && address.All(Uri.IsHexDigit))
        {
            using var worker = new DesktopWorker(pipe, pid, address);
            Application.Run(worker);
            return 0;
        }

        try
        {
            if (args is ["--remove"])
            {
                using var removal = new RemovalForm(showResult: !quiet);
                Application.Run(removal);
                return removal.Succeeded ? 0 : 1;
            }
            var trayOnly = args is ["--tray"];
            if (args.Length > 0 && !trayOnly) return ServiceCommands.Execute(args);
            if (trayOnly && (File.Exists(Paths.RemovalMarker) || !ServiceInstaller.IsInstalledLocation || ServiceInstaller.NeedsInstall())) return 0;
            if (File.Exists(Paths.RemovalMarker))
            {
                if (MessageBox.Show("A previous removal did not finish. Retry removing DeusKVM? Windows Bluetooth pairings are kept.",
                    "Finish removing DeusKVM", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK)
                    ServiceCommands.ElevateExecutableAsync(RuntimeCompat.ProcessPath!, "--remove").GetAwaiter().GetResult();
                return 0;
            }
            if (ServiceInstaller.NeedsInstall())
            {
                using var setup = new SetupForm();
                Application.Run(setup);
                if (!setup.Installed) return 1;
            }
            if (!ServiceInstaller.IsInstalledLocation)
            {
                Process.Start(new ProcessStartInfo(Paths.InstalledExe) { UseShellExecute = true });
                return 0;
            }
            using var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\DeusKVMCompanionShowSettings");
            using var singleInstance = new Mutex(true, "Local\\DeusKVMCompanionTray", out var created);
            if (!created) { if (!trayOnly) showSettings.Set(); return 0; }
            using var tray = new TrayContext(showSettings, !trayOnly);
            Application.Run(tray);
            return 0;
        }
        catch (Exception error)
        {
            if (quiet) Console.Error.WriteLine(error);
            else MessageBox.Show(error.Message, "DeusKVM Companion", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
