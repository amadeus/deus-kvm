using System.Security.AccessControl;
using System.Security.Principal;
using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

// The service's long-term control-link key: created on first use, readable only by SYSTEM and
// Administrators. A new key makes this PC a new device on every Mac (Enable control again).
internal static class ServiceIdentity
{
    public static ControlIdentity Load()
    {
        try
        {
            if (File.Exists(Paths.Identity)) return ControlIdentity.Import(File.ReadAllBytes(Paths.Identity));
        }
        catch (System.Security.Cryptography.CryptographicException) { } // damaged: replaced below
        var identity = ControlIdentity.Create();
        Directory.CreateDirectory(Paths.DataDirectory);
        var temporary = Paths.Identity + ".tmp";
        File.WriteAllBytes(temporary, identity.Export());
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(temporary).SetAccessControl(security);
        if (File.Exists(Paths.Identity)) File.Delete(Paths.Identity);
        File.Move(temporary, Paths.Identity);
        return identity;
    }
}
