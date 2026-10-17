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
    public static string InstallDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DeusKVM Companion");
    public static string Shortcut { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "DeusKVM Companion.lnk");
    public static string RemovalMarker { get; } = Path.Combine(InstallDirectory, "removal-pending");
    public static string InstalledExe { get; } = Path.Combine(InstallDirectory, "DeusKVM.Companion.exe");
}
