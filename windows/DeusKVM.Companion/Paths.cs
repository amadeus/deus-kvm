namespace DeusKVM.Companion;

internal static class Paths
{
    public const string ServiceName = "DeusKVMCompanion";
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DeusKVM");
    public static string Settings => Path.Combine(DataDirectory, "settings.json");
    public static string AutomaticMacs => Path.Combine(DataDirectory, "automatic-macs.json");
    public static string Status => Path.Combine(DataDirectory, "status.json");
    public static string Log => Path.Combine(DataDirectory, "service.log");
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DeusKVM Companion");
    public static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "DeusKVM Companion.lnk");
    public static string RemovalMarker => Path.Combine(InstallDirectory, "removal-pending");
    public static string InstalledExe => Path.Combine(InstallDirectory, "DeusKVM.Companion.exe");
}
