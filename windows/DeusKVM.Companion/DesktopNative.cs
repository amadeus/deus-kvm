using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeusKVM.Companion;

internal static class DesktopNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RawDevice { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int Dx, Dy; public uint Data, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }
    // INPUT's union; MOUSEINPUT is its largest member, so the size matches the native struct.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyInput Key; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }

    // Pointer steps from the Mac's network stream are injected as relative mouse input, tagged
    // so the raw-input handler can tell them from the PC's own mouse (RAWMOUSE.ulExtraInformation).
    internal const uint MouseMove = 0x0001, InjectTag = 0x44455553; // "DEUS"
    internal static (uint Down, uint Up) ButtonFlags(byte button) => button switch
    {
        1 => (0x0002u, 0x0004u), // left
        2 => (0x0008u, 0x0010u), // right
        _ => (0x0020u, 0x0040u) // middle
    };
    internal static bool InjectMouse(int dx, int dy, uint flags)
    {
        var input = new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Dx = dx, Dy = dy, Flags = flags, Extra = (IntPtr)InjectTag } } };
        return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
    }

    // Moves the cursor to (x, y) through the input stack (MOVE | ABSOLUTE | VIRTUALDESK), tagged
    // like the Mac's steps: a real pointer move, as InactiveCursor.Park needs. Pixel coordinates
    // of a per-monitor-aware process.
    internal static bool MoveCursorTo(int x, int y)
    {
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        if (width <= 1 || height <= 1) return false;
        var dx = (int)((x - left) * 65535L / (width - 1));
        var dy = (int)((y - top) * 65535L / (height - 1));
        var input = new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Dx = dx, Dy = dy, Flags = 0x0001 | 0x8000 | 0x4000, Extra = (IntPtr)InjectTag } } };
        return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    // KEYEVENTF_EXTENDEDKEY, KEYEVENTF_KEYUP, KEYEVENTF_SCANCODE
    private const uint KeyExtended = 0x0001, KeyUp = 0x0002, KeyScanCode = 0x0008;

    // A key from the Mac's keyboard state: by scan code normally, so the PC's layout applies.
    // Untagged, like a real keyboard: remappers read dwExtraInfo (PowerToys skips any event with
    // bit 0 set as its own, and the mouse tag has it), and nothing here needs to tell keys apart.
    internal static bool InjectKey(KeyStroke key, bool up)
    {
        var flags = (key.Extended ? KeyExtended : 0) | (up ? KeyUp : 0) | (key.ByScan ? KeyScanCode : 0);
        var input = new Input { Type = 1, Data = new InputUnion { Key = new KeyInput { Vk = key.Vk, Scan = key.Scan, Flags = flags } } };
        return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
    }

    // MOUSEEVENTF_WHEEL, MOUSEEVENTF_HWHEEL; deltas in WHEEL_DELTA (120) units.
    internal static bool InjectWheel(int wheel, int pan)
    {
        var ok = true;
        if (wheel != 0) ok &= InjectMouse(0, 0, 0x0800, wheel);
        if (pan != 0) ok &= InjectMouse(0, 0, 0x1000, pan);
        return ok;
    }

    private static bool InjectMouse(int dx, int dy, uint flags, int data)
    {
        var input = new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Dx = dx, Dy = dy, Data = unchecked((uint)data), Flags = flags, Extra = (IntPtr)InjectTag } } };
        return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
    }

    // The user's keyboard repeat delay and rate (Control Panel), for repeating injected keys.
    internal static (int DelayMs, int PeriodMs) KeyRepeat()
    {
        if (!SystemParametersInfo(0x0016, 0, out var delay, 0)) delay = 1; // SPI_GETKEYBOARDDELAY
        if (!SystemParametersInfo(0x000A, 0, out var speed, 0)) speed = 31; // SPI_GETKEYBOARDSPEED
        return (Typematic.DelayMs(delay), Typematic.PeriodMs(speed));
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);

    internal static double Milliseconds() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;


    internal static bool IsDefaultDesktop() => DesktopState().Desktop == 0;

    // Desktop: 0 Default, 1 Winlogon (login and lock screens, UAC), 2 another. Blind: 0 when the
    // input desktop could be opened (the session agent, SYSTEM, can then inject and watch the edge
    // there), 4 when it could not (no input desktop, or a user process on a secure desktop).
    internal static (byte Blind, byte Desktop) DesktopState()
    {
        var desktop = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS
        if (desktop == IntPtr.Zero) return (4, 2);
        try
        {
            var name = new StringBuilder(256);
            if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _)) return (4, 2);
            if (name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase)) return (0, 0);
            return name.ToString().Equals("Winlogon", StringComparison.OrdinalIgnoreCase) ? ((byte)0, (byte)1) : ((byte)0, (byte)2);
        }
        finally { CloseDesktop(desktop); }
    }

    internal static int Dpi(int x, int y) {
        var monitor = MonitorFromPoint(new Point { X = x, Y = y }, 2);
        return GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? (int)dpi : 96;
    }

    // Read live from Windows each time. .NET Framework's Screen.AllScreens caches its bounds
    // until a display-change event, which can leave a layout read while a monitor slept.
    internal static MonitorInfo[] Monitors()
    {
        var result = new List<MonitorInfo>();
        MonitorEnum callback = (IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data) =>
        {
            if (Describe(monitor) is { } info) result.Add(info);
            return true;
        };
        return EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero) ? result.ToArray() : [];
    }

    // The monitor under a point, in the calling thread's coordinate space (as GetCursorPos).
    internal static MonitorInfo? MonitorAt(int x, int y)
    {
        var monitor = MonitorFromPoint(new Point { X = x, Y = y }, 0); // MONITOR_DEFAULTTONULL
        return monitor == IntPtr.Zero ? null : Describe(monitor);
    }

    private static MonitorInfo? Describe(IntPtr monitor)
    {
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;
        var bounds = info.Monitor;
        var dpi = GetDpiForMonitor(monitor, 0, out var value, out _) == 0 ? (int)value : 96;
        return new MonitorInfo(info.Device, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
            dpi, (info.Flags & 1) != 0);
    }

    // 0 unaware, 1 system, 2 per-monitor; -1 if unavailable. Diagnostics only.
    internal static int DpiAwareness()
    {
        try { return GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()); }
        catch (EntryPointNotFoundException) { return -1; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorEnum(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnum callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetPhysicalCursorPos(out Point point);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    internal static extern uint GetRawInputData(IntPtr raw, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClipCursor(out Rect rect);
    [DllImport("user32.dll")]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserObjectInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int length, out int needed);
}
