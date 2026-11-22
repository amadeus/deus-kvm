namespace DeusKVM.Companion;

// Resolved once: each GetFolderPath call is a shell lookup, and the workers poll these files.
internal static class Paths
{
    public const string ServiceName = "DeusKVMCompanion";
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DeusKVM");
    public static string Settings { get; } = Path.Combine(DataDirectory, "settings.json");
    public static string AutomaticMacs { get; } = Path.Combine(DataDirectory, "automatic-macs.json");
    public static string Status { get; } = Path.Combine(DataDirectory, "status.json");
    public static string Log { get; } = Path.Combine(DataDirectory, "service.log");
    // The service's control-link identity key (SYSTEM and Administrators only).
    public static string Identity { get; } = Path.Combine(DataDirectory, "identity.bin");
    // Optional: Macs to try by name or address, one host[:port] per line (no multicast needed).
    public static string MacHosts { get; } = Path.Combine(DataDirectory, "mac-hosts.txt");
    public static string InstallDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DeusKVM Companion");
    public static string Shortcut { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "DeusKVM Companion.lnk");
    public static string RemovalMarker { get; } = Path.Combine(InstallDirectory, "removal-pending");
    public static string InstalledExe { get; } = Path.Combine(InstallDirectory, "DeusKVM.Companion.exe");
}
