using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class TrackpadSettingsTests
{
    private sealed class MemoryStore : ITrackpadStore
    {
        public readonly Dictionary<string, int> Values = new(StringComparer.Ordinal);
        public int? Read(string name) => Values.TryGetValue(name, out var value) ? value : null;
        public void Write(string name, int value) => Values[name] = value;
        public void Delete(string name) => Values.Remove(name);
    }

    [Fact]
    public void MissingValuesReadAsTheWindowsDefaults()
    {
        var state = TrackpadSettings.Read(new MemoryStore());
        Assert.Equal(TrackpadState.Defaults, state);
        Assert.True(state.IsDefault);
        Assert.Equal(5, state.CursorSpeedNotch);
        Assert.Equal("Medium sensitivity", TrackpadSettings.SensitivityLabels[state.SensitivityIndex]);
        Assert.False(state.ScrollReversed);
    }

    [Theory]
    [InlineData(1, 1)] [InlineData(2, 1)] [InlineData(3, 2)] [InlineData(10, 5)] [InlineData(19, 10)] [InlineData(20, 10)]
    [InlineData(0, 1)] [InlineData(99, 10)]
    public void CursorSpeedNotchIsHalfTheRegistryValueRoundedUpAndClamped(int registry, int notch)
    {
        Assert.Equal(notch, TrackpadSettings.CursorSpeedNotch(registry));
    }

    [Fact]
    public void CursorSpeedNotchesWriteEvenValuesAndRoundTrip()
    {
        var store = new MemoryStore();
        for (var notch = 1; notch <= TrackpadSettings.CursorSpeedNotches; notch++)
        {
            TrackpadSettings.SetCursorSpeed(store, notch);
            Assert.Equal(notch * 2, store.Values[TrackpadSettings.CursorSpeedValue]);
            Assert.Equal(notch, TrackpadSettings.Read(store).CursorSpeedNotch);
        }
        TrackpadSettings.SetCursorSpeed(store, 0);
        Assert.Equal(2, store.Values[TrackpadSettings.CursorSpeedValue]);
        TrackpadSettings.SetCursorSpeed(store, 11);
        Assert.Equal(20, store.Values[TrackpadSettings.CursorSpeedValue]);
    }

    [Fact]
    public void SensitivityUsesTheFourSettingsChoicesAndShowsTheHiddenFifthAsLow()
    {
        var store = new MemoryStore();
        Assert.Equal(4, TrackpadSettings.SensitivityLabels.Length);
        Assert.Equal("Most sensitive", TrackpadSettings.SensitivityLabels[0]);
        TrackpadSettings.SetSensitivity(store, 3);
        Assert.Equal(3, store.Values[TrackpadSettings.SensitivityValue]);
        TrackpadSettings.SetSensitivity(store, 9);
        Assert.Equal(3, store.Values[TrackpadSettings.SensitivityValue]);
        store.Values[TrackpadSettings.SensitivityValue] = 4;
        Assert.Equal(3, TrackpadSettings.Read(store).SensitivityIndex);
        store.Values[TrackpadSettings.SensitivityValue] = 0;
        Assert.Equal(0, TrackpadSettings.Read(store).SensitivityIndex);
    }

    [Fact]
    public void ScrollDirectionIsAFlagAndAnyNonZeroReadsAsReversed()
    {
        var store = new MemoryStore();
        TrackpadSettings.SetScrollReversed(store, true);
        Assert.Equal(1, store.Values[TrackpadSettings.ScrollDirectionValue]);
        Assert.True(TrackpadSettings.Read(store).ScrollReversed);
        TrackpadSettings.SetScrollReversed(store, false);
        Assert.Equal(0, store.Values[TrackpadSettings.ScrollDirectionValue]);
        Assert.False(TrackpadSettings.Read(store).ScrollReversed);
        store.Values[TrackpadSettings.ScrollDirectionValue] = 7;
        Assert.True(TrackpadSettings.Read(store).ScrollReversed);
    }

    [Fact]
    public void ResetDeletesExactlyTheThreeValuesAndLeavesOthersAlone()
    {
        var store = new MemoryStore();
        TrackpadSettings.SetCursorSpeed(store, 2);
        TrackpadSettings.SetSensitivity(store, 0);
        TrackpadSettings.SetScrollReversed(store, true);
        store.Values["TapsEnabled"] = 1;
        Assert.False(TrackpadSettings.Read(store).IsDefault);
        TrackpadSettings.Reset(store);
        Assert.Equal(["TapsEnabled"], store.Values.Keys);
        Assert.True(TrackpadSettings.Read(store).IsDefault);
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\PrecisionTouchPad", TrackpadSettings.KeyPath);
        Assert.Equal(["CursorSpeed", "AAPThreshold", "ScrollDirection"], TrackpadSettings.ValueNames);
    }
}
