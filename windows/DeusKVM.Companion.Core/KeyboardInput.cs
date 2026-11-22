namespace DeusKVM.Companion.Core;

/// <summary>
/// A key as SendInput takes it: by scan code (<see cref="Vk"/> 0, the normal case, so the PC's
/// keyboard layout decides what it types, exactly as for a Bluetooth or USB keyboard), or by
/// virtual key for the few keys whose scan code SendInput cannot express (Pause, Num Lock) and
/// for media keys.
/// </summary>
public readonly record struct KeyStroke(ushort Vk, ushort Scan, bool Extended)
{
    public bool ByScan => Vk == 0;
    public static KeyStroke Code(ushort scan, bool extended = false) => new(0, scan, extended);
}

public enum KeyActionKind : byte { Press, Release, Sas, Lock }

/// <summary>What the keyboard state asks the injector to do; Sas and Lock carry no key.</summary>
public readonly record struct KeyAction(KeyActionKind Kind, KeyStroke Key = default);

/// <summary>
/// HID usages (page 7 keyboard, page 12 consumer) to what SendInput takes, following
/// Microsoft's HID-to-PS/2 table (the same translation kbdhid applies to a real keyboard).
/// </summary>
public static class HidKeys
{
    public const byte Delete = 0x4C, L = 0x0F;
    // Modifier bits 0-7: left Ctrl, Shift, Alt, GUI, then right Ctrl, Shift, Alt, GUI.
    public const byte Ctrl = 0x11, Alt = 0x44, Gui = 0x88;

    private static readonly KeyStroke[] Modifiers =
    [
        KeyStroke.Code(0x1D), KeyStroke.Code(0x2A), KeyStroke.Code(0x38), KeyStroke.Code(0x5B, true),
        KeyStroke.Code(0x1D, true), KeyStroke.Code(0x36), KeyStroke.Code(0x38, true), KeyStroke.Code(0x5C, true)
    ];

    public static KeyStroke Modifier(int bit) => Modifiers[bit];

    private static readonly Dictionary<byte, KeyStroke> Keys = Build();

    private static Dictionary<byte, KeyStroke> Build()
    {
        var keys = new Dictionary<byte, KeyStroke>();
        void Plain(byte usage, ushort scan) => keys[usage] = KeyStroke.Code(scan);
        void Extended(byte usage, ushort scan) => keys[usage] = KeyStroke.Code(scan, true);
        // A-Z (0x04-0x1D)
        byte[] letters = [0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
            0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C];
        for (var i = 0; i < letters.Length; i++) Plain((byte)(0x04 + i), letters[i]);
        for (var i = 0; i < 10; i++) Plain((byte)(0x1E + i), (ushort)(0x02 + i)); // 1-9, 0
        Plain(0x28, 0x1C); Plain(0x29, 0x01); Plain(0x2A, 0x0E); Plain(0x2B, 0x0F); Plain(0x2C, 0x39);
        Plain(0x2D, 0x0C); Plain(0x2E, 0x0D); Plain(0x2F, 0x1A); Plain(0x30, 0x1B); Plain(0x31, 0x2B);
        Plain(0x32, 0x2B); Plain(0x33, 0x27); Plain(0x34, 0x28); Plain(0x35, 0x29); Plain(0x36, 0x33);
        Plain(0x37, 0x34); Plain(0x38, 0x35); Plain(0x39, 0x3A);
        for (var i = 0; i < 10; i++) Plain((byte)(0x3A + i), (ushort)(0x3B + i)); // F1-F10
        Plain(0x44, 0x57); Plain(0x45, 0x58); // F11, F12
        Extended(0x46, 0x37); // Print Screen
        Plain(0x47, 0x46); // Scroll Lock
        keys[0x48] = new KeyStroke(0x13, 0x45, false); // Pause: E1 1D 45 has no scan-code form
        Extended(0x49, 0x52); Extended(0x4A, 0x47); Extended(0x4B, 0x49); Extended(0x4C, 0x53);
        Extended(0x4D, 0x4F); Extended(0x4E, 0x51); Extended(0x4F, 0x4D); Extended(0x50, 0x4B);
        Extended(0x51, 0x50); Extended(0x52, 0x48);
        keys[0x53] = new KeyStroke(0x90, 0x45, true); // Num Lock: by virtual key, as Windows expects
        Extended(0x54, 0x35); Plain(0x55, 0x37); Plain(0x56, 0x4A); Plain(0x57, 0x4E); Extended(0x58, 0x1C);
        byte[] keypad = [0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49, 0x52, 0x53]; // 1-9, 0, .
        for (var i = 0; i < keypad.Length; i++) Plain((byte)(0x59 + i), keypad[i]);
        Plain(0x64, 0x56); // non-US backslash
        Extended(0x65, 0x5D); // Application (context menu)
        Extended(0x66, 0x5E); // Power
        Plain(0x67, 0x59); // keypad =
        ushort[] f13 = [0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x76]; // F13-F24
        for (var i = 0; i < f13.Length; i++) Plain((byte)(0x68 + i), f13[i]);
        Plain(0x85, 0x7E); // keypad comma
        Plain(0x87, 0x73); Plain(0x88, 0x70); Plain(0x89, 0x7D); Plain(0x8A, 0x79); Plain(0x8B, 0x7B); // International 1-5
        Plain(0x90, 0xF2); Plain(0x91, 0xF1); // LANG1 (Hangul/Kana), LANG2 (Hanja/Eisu)
        return keys;
    }

    public static KeyStroke? Key(byte usage) => Keys.TryGetValue(usage, out var key) ? key : null;

    /// <summary>Lock keys toggle on every press, so they must not auto-repeat; neither do Pause or Print Screen.</summary>
    public static bool Repeats(byte usage) => usage is not (0x39 or 0x47 or 0x48 or 0x53 or 0x46);

    /// <summary>A consumer usage as a media/volume virtual key with its E0 scan code; null if Windows has none (brightness).</summary>
    public static KeyStroke? Consumer(ushort usage) => usage switch
    {
        0xCD => new KeyStroke(0xB3, 0x22, true), // play/pause
        0xB5 => new KeyStroke(0xB0, 0x19, true), // next track
        0xB6 => new KeyStroke(0xB1, 0x10, true), // previous track
        0xB7 => new KeyStroke(0xB2, 0x24, true), // stop
        0xE2 => new KeyStroke(0xAD, 0x20, true), // mute
        0xE9 => new KeyStroke(0xAF, 0x30, true), // volume up
        0xEA => new KeyStroke(0xAE, 0x2E, true), // volume down
        _ => null
    };
}

/// <summary>
/// The PC's view of the Mac's keyboard: each record is the whole state, and this turns the
/// difference from the last one into key actions in a safe order (releases, modifier changes,
/// presses). Ctrl+Alt+Delete becomes <see cref="KeyActionKind.Sas"/> and Win+L
/// <see cref="KeyActionKind.Lock"/>, because Windows ignores both when typed through SendInput;
/// the Delete or L of those chords is never injected. Not thread-safe; the caller serializes.
/// </summary>
public sealed class KeyboardState
{
    private byte modifiers;
    private readonly List<byte> held = []; // injected, in press order
    private readonly HashSet<byte> swallowed = []; // held on the Mac, not injected
    private byte? repeating;

    /// <summary>Changes whenever the key to auto-repeat changes, so a repeat timer restarts its delay.</summary>
    public int RepeatSerial { get; private set; }

    /// <summary>The newest held key that auto-repeats, or null.</summary>
    public KeyStroke? Repeating => repeating is { } usage ? HidKeys.Key(usage) : null;

    public bool AnyHeld => modifiers != 0 || held.Count > 0;

    public IReadOnlyList<KeyAction> Apply(byte nextModifiers, ReadOnlySpan<byte> keys)
    {
        var actions = new List<KeyAction>();
        var next = new HashSet<byte>();
        foreach (var usage in keys) if (usage > 3) next.Add(usage); // 0 none, 1-3 error codes
        for (var i = held.Count - 1; i >= 0; i--)
        {
            if (next.Contains(held[i])) continue;
            actions.Add(new KeyAction(KeyActionKind.Release, HidKeys.Key(held[i])!.Value));
            held.RemoveAt(i);
        }
        swallowed.RemoveWhere(usage => !next.Contains(usage));
        for (var bit = 0; bit < 8; bit++)
            if ((modifiers & 1 << bit) != 0 && (nextModifiers & 1 << bit) == 0)
                actions.Add(new KeyAction(KeyActionKind.Release, HidKeys.Modifier(bit)));
        for (var bit = 0; bit < 8; bit++)
            if ((modifiers & 1 << bit) == 0 && (nextModifiers & 1 << bit) != 0)
                actions.Add(new KeyAction(KeyActionKind.Press, HidKeys.Modifier(bit)));
        modifiers = nextModifiers;
        byte? pressed = null;
        foreach (var usage in keys)
        {
            if (usage <= 3 || held.Contains(usage) || swallowed.Contains(usage)) continue;
            if (usage == HidKeys.Delete && (modifiers & HidKeys.Ctrl) != 0 && (modifiers & HidKeys.Alt) != 0)
            { actions.Add(new KeyAction(KeyActionKind.Sas)); swallowed.Add(usage); continue; }
            if (usage == HidKeys.L && (modifiers & HidKeys.Gui) != 0)
            { actions.Add(new KeyAction(KeyActionKind.Lock)); swallowed.Add(usage); continue; }
            if (HidKeys.Key(usage) is not { } key) { swallowed.Add(usage); continue; }
            actions.Add(new KeyAction(KeyActionKind.Press, key));
            held.Add(usage);
            pressed = usage;
        }
        // As on a real keyboard only the newest key repeats: a new press takes over (or stops the
        // repeat, for a lock key), and releasing the repeating key does not resume an older one.
        var repeat = repeating is { } current && held.Contains(current) ? current : (byte?)null;
        if (pressed is { } newest) repeat = HidKeys.Repeats(newest) ? newest : null;
        if (repeat != repeating || pressed is not null && repeat == pressed) RepeatSerial++;
        repeating = repeat;
        return actions;
    }

    /// <summary>Releases for everything injected and still held; the state is empty afterwards.</summary>
    public IReadOnlyList<KeyAction> ReleaseAll() => Apply(0, []);
}

/// <summary>The one media key the Mac holds; a change releases the old key before pressing the new one.</summary>
public sealed class ConsumerState
{
    private KeyStroke? held;

    public IReadOnlyList<KeyAction> Apply(ushort usage)
    {
        var next = HidKeys.Consumer(usage);
        if (next == held) return [];
        var actions = new List<KeyAction>();
        if (held is { } previous) actions.Add(new KeyAction(KeyActionKind.Release, previous));
        if (next is { } key) actions.Add(new KeyAction(KeyActionKind.Press, key));
        held = next;
        return actions;
    }

    public IReadOnlyList<KeyAction> ReleaseAll() => Apply(0);
}

/// <summary>
/// Windows does not auto-repeat injected keys (repeat comes from the keyboard driver), so the
/// companion repeats the held key itself at the user's own Control Panel delay and rate.
/// </summary>
public static class Typematic
{
    /// <summary>SPI_GETKEYBOARDDELAY 0-3: 250 ms steps from 250 to 1000 ms.</summary>
    public static int DelayMs(int setting) => (Math.Max(0, Math.Min(3, setting)) + 1) * 250;

    /// <summary>SPI_GETKEYBOARDSPEED 0-31: about 2.5 to 30 repeats per second.</summary>
    public static int PeriodMs(int setting) => (int)Math.Round(1000 / (2.5 + Math.Max(0, Math.Min(31, setting)) * 27.5 / 31));
}
