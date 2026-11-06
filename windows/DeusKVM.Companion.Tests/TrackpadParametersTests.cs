using System.Runtime.InteropServices;
using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class TrackpadParametersTests
{
    [Fact]
    public void BlockMatchesWinuserLayout()
    {
        Assert.Equal(TrackpadParameters.Size, Marshal.SizeOf<TrackpadParameters>());
        Assert.Equal(44, TrackpadParameters.Size);
        Assert.Equal(1u, TrackpadParameters.ForRead.VersionNumber);
        Assert.Equal(0, (int)Marshal.OffsetOf<TrackpadParameters>(nameof(TrackpadParameters.VersionNumber)));
        Assert.Equal(12, (int)Marshal.OffsetOf<TrackpadParameters>(nameof(TrackpadParameters.SystemFlags)));
        Assert.Equal(16, (int)Marshal.OffsetOf<TrackpadParameters>(nameof(TrackpadParameters.SettingFlags)));
        Assert.Equal(20, (int)Marshal.OffsetOf<TrackpadParameters>(nameof(TrackpadParameters.SensitivityLevel)));
        Assert.Equal(24, (int)Marshal.OffsetOf<TrackpadParameters>(nameof(TrackpadParameters.CursorSpeed)));
        Assert.Equal(40, (int)Marshal.OffsetOf<TrackpadParameters>(nameof(TrackpadParameters.RightClickZoneHeight)));
    }

    [Fact]
    public void FlagsFollowTheDeclarationOrderFromBitZero()
    {
        var block = new TrackpadParameters { SystemFlags = 0b0001_1101 };
        Assert.True(block.TouchpadPresent);
        Assert.True(block.ExternalMousePresent);
        Assert.True(block.TouchpadEnabled);
        Assert.True(block.TouchpadActive);
        block.SystemFlags = 0b10;
        Assert.False(block.TouchpadPresent);
        Assert.Equal(1u << 9, TrackpadParameters.ScrollDirectionReversedBit);
        block.SettingFlags = 0b11_1111_1111;
        Assert.True(block.ScrollDirectionReversed);
        block.ScrollDirectionReversed = false;
        Assert.Equal(0b01_1111_1111u, block.SettingFlags);
        block.ScrollDirectionReversed = true;
        Assert.Equal(0b11_1111_1111u, block.SettingFlags);
    }

    [Fact]
    public void StateAndApplyUseTheDialogScalesAndKeepOtherSettings()
    {
        var block = new TrackpadParameters { CursorSpeed = 10, SensitivityLevel = 2, SettingFlags = 0b01_1111_1111, FeedbackIntensity = 75 };
        Assert.Equal(TrackpadState.Defaults, block.State);
        block.Apply(new TrackpadState(2, 0, true));
        Assert.Equal(4u, block.CursorSpeed);
        Assert.Equal(0u, block.SensitivityLevel);
        Assert.True(block.ScrollDirectionReversed);
        Assert.Equal(0b11_1111_1111u, block.SettingFlags); // other setting bits are untouched
        Assert.Equal(75u, block.FeedbackIntensity);
        Assert.Equal(new TrackpadState(2, 0, true), block.State);
        block.SensitivityLevel = 4;
        Assert.Equal(3, block.State.SensitivityIndex); // least sensitive shows as the lowest choice
    }

    [Fact]
    public void DetectionLineNamesWhatWindowsFound()
    {
        Assert.Equal("Windows reports a precision touchpad: no", new TrackpadParameters().Detection);
        Assert.Equal("Windows reports a precision touchpad: yes", new TrackpadParameters { SystemFlags = 0b1_1001 }.Detection);
        Assert.Equal("Windows reports a precision touchpad: yes (inactive)", new TrackpadParameters { SystemFlags = 0b1 }.Detection);
        Assert.Equal("Windows reports a precision touchpad: yes (inactive, a mouse is connected)", new TrackpadParameters { SystemFlags = 0b101 }.Detection);
    }
}
