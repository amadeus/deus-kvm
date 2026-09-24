using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Forms;

namespace DeusKVM.Bootstrap;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? staging = null;
        try
        {
            if (args.Any(arg => arg is "--service" or "--ble-worker" or "--desktop-worker" or "--tray"))
                throw new ArgumentException("Background modes use the installed companion. Open this EXE normally to install or update it.");
            var assembly = Assembly.GetExecutingAssembly();
            using var zip = assembly.GetManifestResourceStream("DeusKVM.Payload.zip")
                ?? throw new InvalidDataException("Embedded package is missing");
            using var hash = new StreamReader(assembly.GetManifestResourceStream("DeusKVM.Payload.sha256")
                ?? throw new InvalidDataException("Embedded package checksum is missing"));
            staging = CreatePrivateDirectory();
            BundlePayload.Extract(zip, hash.ReadToEnd(), staging);
            var start = new ProcessStartInfo(Path.Combine(staging, "DeusKVM.Companion.exe"))
            {
                UseShellExecute = false,
                // The installed tray inherits this directory during handoff. Keeping
                // it outside staging lets Windows delete the temporary directory.
                WorkingDirectory = Environment.SystemDirectory,
                Arguments = string.Join(" ", args.Select(BundlePayload.QuoteArgument)),
                // Interactive launches hand off to a long-lived tray. Do not give
                // that process an inherited diagnostic pipe that could keep us alive.
                RedirectStandardError = args.Contains("--quiet") && args.Any(arg => arg != "--quiet")
            };
            using var child = Process.Start(start) ?? throw new IOException("Could not open DeusKVM");
            // Drain concurrently so diagnostic output cannot fill the pipe while we wait.
            var errors = start.RedirectStandardError ? child.StandardError.ReadToEndAsync() : null;
            child.WaitForExit();
            if (errors is not null)
            {
                var diagnostic = errors.GetAwaiter().GetResult();
                if (diagnostic.Length != 0) Console.Error.Write(diagnostic);
            }
            return child.ExitCode;
        }
        catch (Exception error)
        {
            if (args.Contains("--quiet")) Console.Error.WriteLine(error.Message);
            else
            {
                Application.EnableVisualStyles();
                MessageBox.Show(error.Message, "DeusKVM Companion", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 1;
        }
        finally
        {
            // The downloaded companion exits after handing off to the installed app.
            // Wait until its DLLs are unmapped, then remove only this invocation's directory.
            if (staging is not null)
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try { Directory.Delete(staging, recursive: true); break; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    { if (attempt < 2) Thread.Sleep(100); }
                }
        }
    }

    private static string CreatePrivateDirectory()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Current Windows user is unavailable");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(user);
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct())
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = Path.Combine(Path.GetTempPath(), "DeusKVM-Bundle-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(directory)) throw new IOException("Package directory already exists");
        Directory.CreateDirectory(directory, acl);
        return directory;
    }
}
