using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;

namespace DeusKVM.Companion;

internal static class ServiceInstaller
{
    public static bool IsInstalledLocation => string.Equals(RuntimeCompat.ProcessPath, Paths.InstalledExe, StringComparison.OrdinalIgnoreCase);

    public static bool NeedsInstall()
    {
        using var service = FindService();
        if (service is null || !File.Exists(Paths.InstalledExe)) return true;
        if (IsInstalledLocation) return false;
        return !PackagePayload.Matches(AppContext.BaseDirectory, Paths.InstallDirectory);
    }

    internal static ServiceController? FindService()
    {
        var service = new ServiceController(Paths.ServiceName);
        try { _ = service.Status; return service; }
        catch (InvalidOperationException error) when (error.InnerException is Win32Exception { NativeErrorCode: 1060 })
        { service.Dispose(); return null; }
        catch { service.Dispose(); throw; }
    }

    public static void Install()
    {
        using var installation = new Mutex(false, "Global\\DeusKVMCompanionInstall");
        var acquired = false;
        try
        {
            try { acquired = installation.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException("Another DeusKVM installation is already running.");
            InstallCore();
        }
        finally { if (acquired) installation.ReleaseMutex(); }
    }

    private static void InstallCore()
    {
        if (File.Exists(Paths.RemovalMarker)) throw new InvalidOperationException("Finish the pending removal before reinstalling DeusKVM.");
        ProtectDirectory(Paths.InstallDirectory);
        ProtectDirectory(Paths.DataDirectory);
        using var existing = FindService();
        var startAfterInstall = existing is null || existing.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
        PackagePayload? payload = null;
        var activating = false;
        var createdService = false;
        try
        {
            // Verify every replacement file before interrupting the working installation.
            if (!IsInstalledLocation) payload = new PackagePayload(AppContext.BaseDirectory, Paths.InstallDirectory);
            if (existing is not null && existing.Status != ServiceControllerStatus.Stopped)
            {
                if (existing.Status != ServiceControllerStatus.StopPending) existing.Stop();
                existing.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
            if (payload is not null)
            {
                CloseInstalledProcesses();
                activating = true;
                payload.Activate();
            }
            var imagePath = $"\"{Paths.InstalledExe}\" --service";
            if (existing is null)
            {
                ServiceCommands.RunSc("create", Paths.ServiceName, "binPath=", imagePath, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "DeusKVM Companion");
                createdService = true;
            }
            else
                ServiceCommands.RunSc("config", Paths.ServiceName, "binPath=", imagePath, "obj=", "LocalSystem", "DisplayName=", "DeusKVM Companion");
            ServiceCommands.RunSc("description", Paths.ServiceName, "Automatically selects the first connected paired DeusKVM Mac independently of user login.");
            ServiceCommands.RunSc("failure", Paths.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000");
            ServiceCommands.RunSc("failureflag", Paths.ServiceName, "0");
            CreateShortcut();
            if (startAfterInstall)
            {
                using var service = new ServiceController(Paths.ServiceName);
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
        }
        catch
        {
            // Restore the entire previous payload, including a legacy single-file installation.
            if (activating)
            {
                using var service = FindService();
                if (service is not null && service.Status != ServiceControllerStatus.Stopped)
                {
                    if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
                CloseInstalledProcesses();
                payload!.RollBack();
            }
            if (createdService) ServiceCommands.RunSc("delete", Paths.ServiceName);
            if (existing is not null && startAfterInstall)
            {
                existing.Refresh();
                if (existing.Status == ServiceControllerStatus.Stopped) existing.Start();
            }
            throw;
        }
        finally { payload?.Dispose(); }
        payload?.Complete();
        // Only register --tray after the new executable has successfully installed.
        // A rollback may restore an older build that does not understand that flag.
        TrayStartup.InstallDefault();
    }

    internal static void CloseInstalledProcesses()
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Paths.InstalledExe)))
        {
            using (process)
            {
                try
                {
                    if (process.Id == RuntimeCompat.ProcessId || process.HasExited ||
                        !string.Equals(process.MainModule?.FileName, Paths.InstalledExe, StringComparison.OrdinalIgnoreCase)) continue;
                    process.Kill();
                    if (!process.WaitForExit(10000)) throw new IOException("The previous companion did not close during the update.");
                }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
        }
    }

    private static void ProtectDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (directory.Exists)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Cannot install through a directory link: {path}");
            var owner = directory.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
            if (!admins.Equals(owner) && !system.Equals(owner))
                throw new IOException($"The installation directory must be owned by Administrators or SYSTEM: {path}");
        }
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(admins);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { admins, system })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (directory.Exists) directory.SetAccessControl(acl);
        else directory.Create(acl);
    }

    private static void CreateShortcut()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows shortcuts are unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(Paths.Shortcut);
            try
            {
                shortcut.TargetPath = Paths.InstalledExe;
                shortcut.WorkingDirectory = Paths.InstallDirectory;
                shortcut.Save();
            }
            finally { Marshal.FinalReleaseComObject(shortcut); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
}

internal sealed class SetupForm : Form
{
    private readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    public bool Installed { get; private set; }

    public SetupForm()
    {
        Text = "DeusKVM Companion";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(440, 110);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        ControlBox = false;
        status.Text = "Installing or updating DeusKVM Companion…\nApprove the Windows administrator prompt to continue.";
        Controls.Add(status);
        Shown += async (_, _) =>
        {
            try { await ServiceCommands.ElevateExecutableAsync(RuntimeCompat.ProcessPath!, "--install"); Installed = true; }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223) { }
            catch (Exception error) { TrayContext.ShowError(error); }
            finally { Close(); }
        };
    }
}
