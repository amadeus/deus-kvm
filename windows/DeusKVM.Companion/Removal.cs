using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using DeusKVM.Companion.Core;
using Microsoft.Win32;

namespace DeusKVM.Companion;

internal sealed class Removal(Action<string> progress, bool showResult) : IRemovalSteps
{
    internal static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Removal requires administrator permission.");
    }
    internal static void CheckDirectory(string path)
    {
        for (var parent = new DirectoryInfo(path); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Cannot remove through a directory link: {parent.FullName}");
        var directory = new DirectoryInfo(path);
        if (!directory.Exists) return;
        var owner = directory.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
        if (!new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Equals(owner) &&
            !new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Equals(owner))
            throw new IOException($"Cannot remove an installation not owned by Administrators or SYSTEM: {path}");
    }
    public async Task Stop()
    {
        RequireAdministrator();
        CheckDirectory(Paths.InstallDirectory); CheckDirectory(Paths.DataDirectory);
        if (Directory.Exists(Paths.InstallDirectory)) File.WriteAllText(Paths.RemovalMarker, "Removal requested");
        progress("Stopping DeusKVM and closing its workers and tray…");
        await Task.Run(() =>
        {
            using (var service = ServiceInstaller.FindService())
            {
                if (service is not null && service.Status != ServiceControllerStatus.Stopped)
                {
                    if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
            }
            ServiceInstaller.CloseInstalledProcesses();
        });
    }
    public async Task Unregister()
    {
        progress("Removing service and startup registration…");
        TrayStartup.Remove();
        using (var sources = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\EventLog\Application", true))
            sources?.DeleteSubKeyTree(Paths.ServiceName, false);
        File.Delete(Paths.Shortcut);
        await Task.Run(() =>
        {
            var service = ServiceInstaller.FindService();
            if (service is null) return;
            service.Dispose(); // SCM cannot finish deletion while our handle is open.
            ServiceCommands.RunSc("delete", Paths.ServiceName);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                using var remaining = ServiceInstaller.FindService();
                if (remaining is null) return;
                Thread.Sleep(100);
            }
            throw new IOException("Windows has not finished deleting the service. Close any Services windows and retry removal.");
        });
    }
    public Task FinishFiles()
    {
        progress("Finishing removal. This window will close; a final message will confirm the result.");
        // Windows cannot delete the EXE/native libraries while this process maps
        // them. A built-in PowerShell process completes and verifies file cleanup
        // after this process exits. No helper file or extra user entry point.
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("DeusKVM.Companion.FinishRemoval.ps1")
            ?? throw new InvalidOperationException("Removal helper is missing.");
        using var reader = new StreamReader(source);
        var paths = CachePaths().Append(Paths.DataDirectory).Append(Paths.InstallDirectory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var encodedPaths = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(paths));
        var script = $"$showResult = {(showResult ? "$true" : "$false")}\n$removingProcess = {RuntimeCompat.ProcessId}\n$encodedPaths = '{encodedPaths}'\n" + reader.ReadToEnd();
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory };
        start.SetArguments(new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) });
        using var helper = Process.Start(start) ?? throw new IOException("Could not start final file cleanup. Retry removal.");
        return Task.CompletedTask;
    }
    private static IEnumerable<string> CachePaths()
    {
        const string executable = "DeusKVM.Companion";
        yield return Path.Combine(Path.GetTempPath(), ".net", executable);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", ".net", executable);
        using var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        if (profiles is null) yield break;
        foreach (var name in profiles.GetSubKeyNames())
        {
            using var profile = profiles.OpenSubKey(name);
            if (profile?.GetValue("ProfileImagePath") is string root && RuntimeCompat.IsFullyQualifiedWindowsPath(root))
                yield return Path.Combine(root, "AppData", "Local", "Temp", ".net", executable);
        }
    }
}

internal sealed class RemovalForm : Form
{
    private readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    public bool Succeeded { get; private set; }
    public RemovalForm(bool showResult = true)
    {
        Text = "Removing DeusKVM"; ClientSize = new Size(480, 140); ControlBox = false;
        AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(status);
        Shown += async (_, _) =>
        {
            using var installation = new Mutex(false, "Global\\DeusKVMCompanionInstall");
            var acquired = false;
            try
            {
                Removal.RequireAdministrator();
                try { acquired = installation.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new InvalidOperationException("Another install or removal is already running.");
                await RemovalWorkflow.Run(new Removal(message => status.Text = message, showResult));
                Succeeded = true;
            }
            catch (Exception error)
            {
                if (showResult) TrayContext.ShowError(new InvalidOperationException($"Removal did not finish. Reopen the downloaded EXE to retry.\n\n{error.Message}", error));
                else Console.Error.WriteLine(error);
            }
            finally { if (acquired) installation.ReleaseMutex(); Close(); }
        };
    }
}
