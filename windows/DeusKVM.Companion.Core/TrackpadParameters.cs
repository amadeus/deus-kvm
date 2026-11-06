using System.Runtime.InteropServices;

namespace DeusKVM.Companion.Core;

/// <summary>
/// Windows' TOUCHPAD_PARAMETERS_V1 (winuser.h, Windows 11 24H2 and later): the block
/// SystemParametersInfo reads and writes the user's precision touchpad settings with, which is
/// how the Settings page applies them without a sign-in. Eleven 32-bit words; the two bitfield
/// groups are kept as whole words, bit 0 first as MSVC lays them out. The first word group is
/// system information Windows ignores on a write; the second and the trailing values are the
/// user's settings.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TrackpadParameters
{
    public const uint Version1 = 1;
    public const int Size = 44;

    public uint VersionNumber;
    public uint MaxSupportedContacts;
    public uint LegacyTouchpadFeatures;
    /// <summary>touchpadPresent, legacyTouchpadPresent, externalMousePresent, touchpadEnabled, touchpadActive, feedbackSupported, clickForceSupported.</summary>
    public uint SystemFlags;
    /// <summary>allowActiveWhenMousePresent, feedbackEnabled, tapEnabled, tapAndDragEnabled, twoFingerTapEnabled, rightClickZoneEnabled, mouseAccelSettingHonored, panEnabled, zoomEnabled, scrollDirectionReversed.</summary>
    public uint SettingFlags;
    /// <summary>TOUCHPAD_SENSITIVITY_LEVEL: 0 most sensitive to 4 least sensitive.</summary>
    public uint SensitivityLevel;
    /// <summary>1 to 20, the same scale as the registry value.</summary>
    public uint CursorSpeed;
    public uint FeedbackIntensity;
    public uint ClickForceSensitivity;
    public uint RightClickZoneWidth;
    public uint RightClickZoneHeight;

    public const uint TouchpadPresentBit = 1u << 0;
    public const uint ExternalMousePresentBit = 1u << 2;
    public const uint TouchpadEnabledBit = 1u << 3;
    public const uint TouchpadActiveBit = 1u << 4;
    public const uint ScrollDirectionReversedBit = 1u << 9;

    /// <summary>What a caller passes to a read: the version, nothing else.</summary>
    public static TrackpadParameters ForRead => new() { VersionNumber = Version1 };

    public readonly bool TouchpadPresent => (SystemFlags & TouchpadPresentBit) != 0;
    public readonly bool ExternalMousePresent => (SystemFlags & ExternalMousePresentBit) != 0;
    public readonly bool TouchpadEnabled => (SystemFlags & TouchpadEnabledBit) != 0;
    public readonly bool TouchpadActive => (SystemFlags & TouchpadActiveBit) != 0;

    public bool ScrollDirectionReversed
    {
        readonly get => (SettingFlags & ScrollDirectionReversedBit) != 0;
        set => SettingFlags = value ? SettingFlags | ScrollDirectionReversedBit : SettingFlags & ~ScrollDirectionReversedBit;
    }

    /// <summary>The three settings the dialog shows, on its scales.</summary>
    public readonly TrackpadState State => new(
        TrackpadSettings.CursorSpeedNotch((int)CursorSpeed),
        TrackpadSettings.SensitivityIndex((int)SensitivityLevel),
        ScrollDirectionReversed);

    /// <summary>Changes only those three; every other setting keeps what the read returned.</summary>
    public void Apply(TrackpadState state)
    {
        CursorSpeed = (uint)TrackpadSettings.CursorSpeedFromNotch(state.CursorSpeedNotch);
        SensitivityLevel = (uint)TrackpadSettings.SensitivityIndex(state.SensitivityIndex);
        ScrollDirectionReversed = state.ScrollReversed;
    }

    /// <summary>One line for the dialog: what Windows says it detected.</summary>
    public readonly string Detection =>
        $"Windows reports a precision touchpad: {(TouchpadPresent ? "yes" : "no")}" +
        (TouchpadPresent && !TouchpadActive ? " (inactive" + (ExternalMousePresent ? ", a mouse is connected)" : ")") : "");
}
