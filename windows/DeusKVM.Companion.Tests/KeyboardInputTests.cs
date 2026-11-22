using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class KeyboardInputTests
{
    private static KeyAction Press(KeyStroke key) => new(KeyActionKind.Press, key);
    private static KeyAction Release(KeyStroke key) => new(KeyActionKind.Release, key);
    private static KeyStroke Usage(byte usage) => HidKeys.Key(usage)!.Value;
    private const byte LeftShift = 0x02, LeftCtrl = 0x01, LeftAlt = 0x04, RightAlt = 0x40, LeftGui = 0x08;

    [Fact]
    public void UsagesMapToScanCodesLikeARealKeyboard()
    {
        Assert.Equal(KeyStroke.Code(0x1E), Usage(0x04)); // A
        Assert.Equal(KeyStroke.Code(0x2C), Usage(0x1D)); // Z
        Assert.Equal(KeyStroke.Code(0x0B), Usage(0x27)); // 0
        Assert.Equal(KeyStroke.Code(0x58), Usage(0x45)); // F12
        Assert.Equal(KeyStroke.Code(0x76), Usage(0x73)); // F24
        Assert.Equal(KeyStroke.Code(0x53, true), Usage(0x4C)); // Delete
        Assert.Equal(KeyStroke.Code(0x48, true), Usage(0x52)); // Up
        Assert.Equal(KeyStroke.Code(0x37, true), Usage(0x46)); // Print Screen
        Assert.Equal(KeyStroke.Code(0x1C, true), Usage(0x58)); // keypad Enter
        Assert.Equal(KeyStroke.Code(0x53), Usage(0x63)); // keypad .
        Assert.Equal(KeyStroke.Code(0x5D, true), Usage(0x65)); // Application
        Assert.Equal(KeyStroke.Code(0x56), Usage(0x64)); // non-US backslash
        Assert.Equal(KeyStroke.Code(0x2B), Usage(0x32)); // non-US # shares the backslash position
        Assert.Equal(new KeyStroke(0x13, 0x45, false), Usage(0x48)); // Pause by virtual key
        Assert.Equal(new KeyStroke(0x90, 0x45, true), Usage(0x53)); // Num Lock by virtual key
        Assert.Null(HidKeys.Key(0x00));
        Assert.Null(HidKeys.Key(0xA5));
        Assert.Equal(KeyStroke.Code(0x38, true), HidKeys.Modifier(6)); // right Alt (AltGr)
        Assert.Equal(KeyStroke.Code(0x5B, true), HidKeys.Modifier(3)); // left Windows
        Assert.Equal(new KeyStroke(0xAF, 0x30, true), HidKeys.Consumer(0xE9));
        Assert.Null(HidKeys.Consumer(0x6F)); // brightness: Windows has no key for it
    }

    [Fact]
    public void StateChangesBecomeReleasesThenModifiersThenPresses()
    {
        var state = new KeyboardState();
        Assert.Equal([Press(HidKeys.Modifier(1)), Press(Usage(0x04))], state.Apply(LeftShift, [0x04]));
        Assert.Empty(state.Apply(LeftShift, [0x04]));
        Assert.Equal([Release(Usage(0x04)), Release(HidKeys.Modifier(1)), Press(HidKeys.Modifier(0)), Press(Usage(0x05))],
            state.Apply(LeftCtrl, [0x05]));
        Assert.True(state.AnyHeld);
        Assert.Equal([Release(Usage(0x05)), Release(HidKeys.Modifier(0))], state.ReleaseAll());
        Assert.False(state.AnyHeld);
        Assert.Empty(state.ReleaseAll());
    }

    [Fact]
    public void ManyKeysAndErrorCodesAreHandled()
    {
        var state = new KeyboardState();
        byte[] eight = [0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B];
        Assert.Equal(8, state.Apply(0, eight).Count); // no six-key limit on the network
        Assert.Empty(state.Apply(0, [0x01, .. eight])); // a rollover error code is ignored
        Assert.Single(state.Apply(0, [0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A]));
    }

    [Fact]
    public void CtrlAltDeleteAndWinLBecomeActionsWithoutTheirKey()
    {
        var state = new KeyboardState();
        state.Apply(LeftCtrl | LeftAlt, []);
        Assert.Equal([new KeyAction(KeyActionKind.Sas)], state.Apply(LeftCtrl | LeftAlt, [HidKeys.Delete]));
        Assert.Empty(state.Apply(LeftCtrl | LeftAlt, [HidKeys.Delete])); // held, not repeated
        Assert.Empty(state.Apply(LeftCtrl | LeftAlt, [])); // the Delete was never pressed, so it is not released
        Assert.Equal([new KeyAction(KeyActionKind.Sas)], state.Apply(LeftCtrl | LeftAlt, [HidKeys.Delete]));
        state.ReleaseAll();
        // AltGr+Ctrl counts too (right Alt); Delete alone is an ordinary key.
        state.Apply(LeftCtrl | RightAlt, []);
        Assert.Equal([new KeyAction(KeyActionKind.Sas)], state.Apply(LeftCtrl | RightAlt, [HidKeys.Delete]));
        state.ReleaseAll();
        Assert.Equal([Press(Usage(HidKeys.Delete))], state.Apply(0, [HidKeys.Delete]));
        state.ReleaseAll();
        state.Apply(LeftGui, []);
        Assert.Equal([new KeyAction(KeyActionKind.Lock)], state.Apply(LeftGui, [HidKeys.L]));
        Assert.Equal([Release(HidKeys.Modifier(3))], state.Apply(0, []));
    }

    [Fact]
    public void OnlyTheNewestKeyRepeatsAndLockKeysNever()
    {
        var state = new KeyboardState();
        Assert.Null(state.Repeating);
        state.Apply(0, [0x04]);
        Assert.Equal(Usage(0x04), state.Repeating);
        var first = state.RepeatSerial;
        state.Apply(LeftShift, [0x04]); // a modifier change keeps the repeat going
        Assert.Equal(first, state.RepeatSerial);
        state.Apply(LeftShift, [0x04, 0x05]);
        Assert.Equal(Usage(0x05), state.Repeating);
        Assert.NotEqual(first, state.RepeatSerial);
        state.Apply(LeftShift, [0x04]); // releasing the newest does not resume the older one
        Assert.Null(state.Repeating);
        state.Apply(LeftShift, [0x04, 0x39]); // Caps Lock pressed last: nothing repeats
        Assert.Null(state.Repeating);
        state.ReleaseAll();
        var before = state.RepeatSerial;
        state.Apply(0, [0x04]);
        state.Apply(0, []);
        state.Apply(0, [0x04]); // the same key pressed again restarts its delay
        Assert.True(state.RepeatSerial >= before + 3);
    }

    [Fact]
    public void TheMediaKeyIsReleasedBeforeTheNextOne()
    {
        var media = new ConsumerState();
        var up = HidKeys.Consumer(0xE9)!.Value;
        var mute = HidKeys.Consumer(0xE2)!.Value;
        Assert.Equal([Press(up)], media.Apply(0xE9));
        Assert.Empty(media.Apply(0xE9));
        Assert.Equal([Release(up), Press(mute)], media.Apply(0xE2));
        Assert.Equal([Release(mute)], media.Apply(0x6F)); // brightness: nothing to press
        Assert.Empty(media.ReleaseAll());
    }

    [Fact]
    public void RepeatTimingFollowsTheControlPanelSettings()
    {
        Assert.Equal(250, Typematic.DelayMs(0));
        Assert.Equal(1000, Typematic.DelayMs(3));
        Assert.Equal(400, Typematic.PeriodMs(0));
        Assert.Equal(33, Typematic.PeriodMs(31));
        Assert.Equal(33, Typematic.PeriodMs(99));
    }

    [Fact]
    public void KeyboardMediaAndScrollRecordsParseTheMacsLayout()
    {
        Assert.True(InputRecord.TryParse([4, 7, 0, 0, 0, 9, 0, 0, 0, 0x82, 2, 0x04, 0x4C], out var keys));
        Assert.True(keys.IsKeyboard);
        Assert.Equal(0x82, keys.Modifiers);
        Assert.Equal(new byte[] { 0x04, 0x4C }, keys.Keys);
        Assert.True(InputRecord.TryParse([4, 7, 0, 0, 0, 9, 0, 0, 0, 0, 0], out var none));
        Assert.Empty(none.Keys!);
        Assert.False(InputRecord.TryParse([4, 7, 0, 0, 0, 9, 0, 0, 0, 0, 2, 0x04], out _)); // count disagrees with length
        Assert.True(InputRecord.TryParse([5, 7, 0, 0, 0, 9, 0, 0, 0, 0xE9, 0x00], out var media));
        Assert.True(media.IsConsumer);
        Assert.Equal(0xE9, media.Usage);
        Assert.True(InputRecord.TryParse([6, 7, 0, 0, 0, 9, 0, 0, 0, 0xFD, 0x01], out var scroll));
        Assert.True(scroll.IsScroll);
        Assert.Equal(-3, scroll.Wheel);
        Assert.Equal(1, scroll.Pan);
        Assert.False(InputRecord.TryParse([6, 7, 0, 0, 0, 9, 0, 0, 0, 1], out _));
    }

    [Fact]
    public void KeyWheelAndSasRelayRecordsRoundTrip()
    {
        var delete = Usage(HidKeys.Delete);
        var record = SecureInputRecord.EncodeKey(delete, up: true);
        Assert.Equal(new byte[] { 5, 0, 0, 0x53, 0, 3 }, record);
        Assert.True(SecureInputRecord.TryParseKey(record, out var key, out var up));
        Assert.Equal(delete, key);
        Assert.True(up);
        Assert.True(SecureInputRecord.TryParseKey(SecureInputRecord.EncodeKey(Usage(0x48), up: false), out var pause, out up));
        Assert.Equal(new KeyStroke(0x13, 0x45, false), pause);
        Assert.False(up);
        var wheel = SecureInputRecord.EncodeWheel(-360, 120);
        Assert.True(SecureInputRecord.TryParseWheel(wheel, out var vertical, out var horizontal));
        Assert.Equal((-360, 120), (vertical, horizontal));
        Assert.Equal(6, SecureInputRecord.Length(SecureInputRecord.Key));
        Assert.Equal(5, SecureInputRecord.Length(SecureInputRecord.Wheel));
        Assert.Equal(1, SecureInputRecord.Length(SecureInputRecord.Sas));
        // The batcher keeps every new kind (an unknown length would drop the rest of a run).
        byte[] run = [.. record, .. wheel, .. SecureInputRecord.SasRecord, .. SecureInputRecord.EncodeMotion(1, 1)];
        var frames = SecureFrame.Batch(run, run.Length);
        Assert.Equal(2 + run.Length, frames.Length);
    }
}
