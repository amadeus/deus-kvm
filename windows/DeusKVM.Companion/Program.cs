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

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // SYSTEM in the console session (login screen, signed in, locked): input, edge return,
        // cursor and displays. Spawned by the service.
        if (args is ["--session-agent", var agentPipe, var agentParent] && int.TryParse(agentParent, out var agentPid))
        {
            using var agent = new SessionAgent(agentPipe, agentPid);
            Application.Run(agent);
            return 0;
        }

        // The signed-in console user: clipboard, file and image paste. Spawned by the service.
        if (args is ["--clipboard-worker", var clipboardPipe, var clipboardParent] && int.TryParse(clipboardParent, out var clipboardPid))
        {
            using var clipboard = new ClipboardWorker(clipboardPipe, clipboardPid);
            Application.Run(clipboard);
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
                if (MessageBox.Show("A previous removal did not finish. Retry removing DeusKVM?",
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
