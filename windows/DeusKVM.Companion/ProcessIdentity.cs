using System.Diagnostics;
using System.Security.Principal;

namespace DeusKVM.Companion;

// Constant per process. On .NET Framework, Process.SessionId snapshots every process and
// WindowsIdentity.Name asks LSA, so periodic status and heartbeat paths read these once.
internal static class ProcessIdentity
{
    public static readonly string Name = ReadName();
    public static readonly int Session = ReadSession();
    private static string ReadName() { using var identity = WindowsIdentity.GetCurrent(); return identity.Name; }
    private static int ReadSession() { using var process = Process.GetCurrentProcess(); return process.SessionId; }
}
