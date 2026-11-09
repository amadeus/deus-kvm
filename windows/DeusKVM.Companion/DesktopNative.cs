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
    private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceEntry { public IntPtr Handle; public uint Type; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int Dx, Dy; public uint Data, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public MouseInput Mouse; }

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
        var input = new Input { Type = 0, Mouse = new MouseInput { Dx = dx, Dy = dy, Flags = flags, Extra = (IntPtr)InjectTag } };
        return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
    }
    internal static double Milliseconds() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    internal static bool IsMacMouse(IntPtr device, string address)
    {
        uint size = 0;
        if (GetRawInputDeviceInfo(device, 0x20000007, null, ref size) == uint.MaxValue || size > 4096 || size == 0) return false;
        var name = new StringBuilder((int)size);
        if (GetRawInputDeviceInfo(device, 0x20000007, name, ref size) == uint.MaxValue) return false;
        // Resolve the HID interface to its PnP ancestry; match the selected BLE device's address exactly.
        var key = new PropertyKey { Format = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), Id = 256 };
        var buffer = new byte[8192];
        uint bytes = (uint)buffer.Length;
        if (CM_Get_Device_Interface_Property(name.ToString(), ref key, out _, buffer, ref bytes, 0) != 0) return false;
        var instance = Encoding.Unicode.GetString(buffer, 0, (int)bytes).TrimEnd('\0');
        if (CM_Locate_DevNode(out var node, instance, 0) != 0) return false;
        for (var depth = 0; depth < 16; depth++)
        {
            var id = new StringBuilder(4096);
            if (CM_Get_Device_ID(node, id, id.Capacity, 0) != 0) return false;
            var parts = id.ToString().Split('\\');
            if (parts.Length >= 2 && parts[0].Equals("BTHLE", StringComparison.OrdinalIgnoreCase) &&
                parts[1].Equals("DEV_" + address, StringComparison.OrdinalIgnoreCase)) return true;
            if (CM_Get_Parent(out var parent, node, 0) != 0) return false;
            node = parent;
        }
        return false;
    }

    internal static bool IsDefaultDesktop() => DesktopState().Desktop == 0;

    internal static (byte Blind, byte Desktop) DesktopState()
    {
        var desktop = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS
        if (desktop == IntPtr.Zero) return (4, 2);
        try
        {
            var name = new StringBuilder(256);
            if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _)) return (4, 2);
            if (name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase)) return (0, 0);
            return name.ToString().Equals("Winlogon", StringComparison.OrdinalIgnoreCase) ? ((byte)1, (byte)1) : ((byte)4, (byte)2);
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
    internal static extern uint GetRawInputDeviceList([Out] DeviceEntry[]? devices, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW")]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder? data, ref uint size);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_PropertyW")]
    private static extern uint CM_Get_Device_Interface_Property(string name, ref PropertyKey key, out uint type,
        [Out] byte[] value, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
    private static extern uint CM_Locate_DevNode(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_IDW")]
    private static extern uint CM_Get_Device_ID(uint node, StringBuilder id, int length, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
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
