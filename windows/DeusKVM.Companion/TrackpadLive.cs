using System.Runtime.InteropServices;

namespace DeusKVM.Companion;

/// <summary>
/// The live path for the user's precision touchpad settings: SystemParametersInfo with
/// SPI_GETTOUCHPADPARAMETERS / SPI_SETTOUCHPADPARAMETERS, what the Settings page uses on
/// Windows 11 24H2 and later. On older builds the calls fail and the registry values apply at
/// the next sign-in instead. The process must run as the user, which the tray does.
/// </summary>
internal static class TrackpadLive
{
    private const uint GetTouchpadParameters = 0x00AE;
    private const uint SetTouchpadParameters = 0x00AF;
    private const uint UpdateIniFile = 0x0001;
    private const uint SendChange = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint parameter, ref TrackpadParameters value, uint winIni);

    public static bool TryRead(out TrackpadParameters parameters)
    {
        parameters = TrackpadParameters.ForRead;
        return Call(GetTouchpadParameters, ref parameters, 0);
    }

    /// <summary>Reads everything, changes the three settings and writes the block back, persisted and broadcast.</summary>
    public static bool TryApply(TrackpadState state)
    {
        if (!TryRead(out var parameters)) return false;
        parameters.Apply(state);
        return Call(SetTouchpadParameters, ref parameters, UpdateIniFile | SendChange);
    }

    private static bool Call(uint action, ref TrackpadParameters parameters, uint winIni)
    {
        try { return SystemParametersInfoW(action, (uint)TrackpadParameters.Size, ref parameters, winIni); }
        catch (EntryPointNotFoundException) { return false; }
    }
}
