using System.Runtime.InteropServices;

namespace DeusKVM.Companion;

/// <summary>
/// Ctrl+Alt+Delete from the Mac, raised by the service process itself (the link runs in it). Windows ignores the chord
/// through SendInput by design, and SendSAS works only for a caller "running as a service" (a
/// SYSTEM helper in the user's session is not enough), once the SoftwareSASGeneration policy
/// allows services (1, or 3 with Ease of Access apps). The policy is set here the first time it
/// is needed, keeping an existing Ease of Access permission. SendSAS reports nothing, so the
/// result is only what was attempted, for the service log.
/// </summary>
internal static class SecureAttention
{
    private const string PolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string PolicyValue = "SoftwareSASGeneration";

    public static string Send()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(PolicyKey, writable: true);
            var current = key?.GetValue(PolicyValue) is int value ? value : 0;
            if ((current & 1) == 0) key?.SetValue(PolicyValue, current | 1, Microsoft.Win32.RegistryValueKind.DWord);
            SendSAS(false);
            return $"Ctrl+Alt+Delete: SendSAS sent (policy {current}{((current & 1) == 0 ? $" -> {current | 1}" : "")})";
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException or DllNotFoundException or EntryPointNotFoundException)
        {
            return $"Ctrl+Alt+Delete: SendSAS failed: {error.Message}";
        }
    }

    [DllImport("sas.dll")]
    private static extern void SendSAS([MarshalAs(UnmanagedType.Bool)] bool asUser);
}
