namespace DeusKVM.Companion.Core;

/// <summary>
/// Reads and writes the per-user precision touchpad values Windows keeps for the signed-in user.
/// The companion only needs three of them; the store is the registry in the app and a dictionary
/// in tests.
/// </summary>
public interface ITrackpadStore
{
    int? Read(string name);
    void Write(string name, int value);
    void Delete(string name);
}

/// <summary>What the dialog shows: positions on the same scales as the Windows Settings page.</summary>
public sealed record TrackpadState(int CursorSpeedNotch, int SensitivityIndex, bool ScrollReversed)
{
    public static readonly TrackpadState Defaults = new(5, 2, false);
    public bool IsDefault => this == Defaults;
}

/// <summary>
/// Windows' precision touchpad settings for the Mac trackpad, as the Settings app would write them.
/// Windows does not show its Touchpad page for DeusKVM's touchpad collection, so the companion
/// exposes the three settings that change the feel. Names, ranges and defaults are from Microsoft's
/// touchpad tuning guidelines; everything lives under the per-user key and applies only to
/// precision touchpads, never to a mouse.
/// </summary>
public static class TrackpadSettings
{
    public const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\PrecisionTouchPad";
    /// <summary>1 to 20, default 10; the Settings slider shows 1 to 10 in steps of two.</summary>
    public const string CursorSpeedValue = "CursorSpeed";
    /// <summary>Accidental activation prevention, 0 to 4, default 2; 0 is the most sensitive.</summary>
    public const string SensitivityValue = "AAPThreshold";
    /// <summary>0 is the Windows default (contacts moving up scroll content down), 1 reverses it.</summary>
    public const string ScrollDirectionValue = "ScrollDirection";

    public static readonly string[] ValueNames = [CursorSpeedValue, SensitivityValue, ScrollDirectionValue];
    /// <summary>The four choices the Settings page offers, in registry order 0 to 3.</summary>
    public static readonly string[] SensitivityLabels = ["Most sensitive", "High sensitivity", "Medium sensitivity", "Low sensitivity"];
    public const int CursorSpeedNotches = 10;

    public static TrackpadState Read(ITrackpadStore store) => new(
        CursorSpeedNotch(store.Read(CursorSpeedValue)),
        SensitivityIndex(store.Read(SensitivityValue)),
        store.Read(ScrollDirectionValue) is int direction && direction != 0);

    public static void SetCursorSpeed(ITrackpadStore store, int notch) =>
        store.Write(CursorSpeedValue, CursorSpeedFromNotch(notch));

    public static void SetSensitivity(ITrackpadStore store, int index) =>
        store.Write(SensitivityValue, Clamp(index, 0, SensitivityLabels.Length - 1));

    public static void SetScrollReversed(ITrackpadStore store, bool reversed) =>
        store.Write(ScrollDirectionValue, reversed ? 1 : 0);

    /// <summary>Deleting the values is what "default" means to Windows; it never writes them itself.</summary>
    public static void Reset(ITrackpadStore store)
    {
        foreach (var name in ValueNames) store.Delete(name);
    }

    /// <summary>Slider notch 1 to 10 to the registry's 2 to 20.</summary>
    public static int CursorSpeedFromNotch(int notch) => Clamp(notch, 1, CursorSpeedNotches) * 2;

    /// <summary>Registry 1 to 20 (or absent) to the slider notch; odd values round up like the Settings page.</summary>
    public static int CursorSpeedNotch(int? value) =>
        value is int speed ? Clamp((speed + 1) / 2, 1, CursorSpeedNotches) : TrackpadState.Defaults.CursorSpeedNotch;

    /// <summary>Registry 0 to 4 (or absent) to the four labels; the unexposed 4 shows as the lowest.</summary>
    public static int SensitivityIndex(int? value) =>
        value is int threshold ? Clamp(threshold, 0, SensitivityLabels.Length - 1) : TrackpadState.Defaults.SensitivityIndex;

    // .NET Framework has no Math.Clamp.
    private static int Clamp(int value, int minimum, int maximum) => value < minimum ? minimum : value > maximum ? maximum : value;
}
