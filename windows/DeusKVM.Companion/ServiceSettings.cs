namespace DeusKVM.Companion;

/// <summary>
/// The settings changes the tray can ask for, applied the same way whether they arrive over
/// the service's control pipe (as SYSTEM) or from an elevated command line. The link worker
/// watches settings.json, so a saved Mac takes effect without a restart.
/// </summary>
internal static class ServiceSettings
{
    public static void Configure(CompanionSettings settings)
    {
        settings.Validate();
        RefuseDuringRemoval();
        if (!Directory.Exists(Paths.DataDirectory)) throw new InvalidOperationException("Install the service first.");
        JsonFiles.Write(Paths.Settings, settings);
    }

    public static void SetAutomaticStartup(bool automatic)
    {
        RefuseDuringRemoval();
        ServiceCommands.RunSc("config", Paths.ServiceName, "start=", automatic ? "auto" : "demand");
    }

    public static void SetTrayStartup(bool enabled)
    {
        RefuseDuringRemoval();
        TrayStartup.SetEnabled(enabled);
    }

    public static void RefuseDuringRemoval()
    {
        if (File.Exists(Paths.RemovalMarker)) throw new InvalidOperationException("Finish the pending removal before changing service settings.");
    }
}
