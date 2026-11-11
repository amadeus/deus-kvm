using System.Runtime.InteropServices;

namespace DeusKVM.Companion;

/// <summary>
/// Whether the foreground window belongs to a process at a higher integrity level than this one,
/// such as an elevated terminal. Windows (UIPI) then silently discards the input this process
/// injects, cursor motion included, while SendInput still reports success; the pointer has to go
/// through the SYSTEM secure-input worker instead. The answer is cached per foreground window, so
/// a call is one GetForegroundWindow unless the foreground changed. Safe from any thread.
/// </summary>
internal sealed class ForegroundIntegrity
{
    private sealed class Entry(IntPtr window, bool outranks)
    {
        public readonly IntPtr Window = window;
        public readonly bool Outranks = outranks;
    }

    private const int Medium = 0x2000;
    private readonly int own = Level(GetCurrentProcess()) is var level && level > 0 ? level : Medium;
    private Entry last = new(IntPtr.Zero, false);

    public bool Outranks()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false; // between activations: nothing to be blocked by
        var entry = Volatile.Read(ref last);
        if (entry.Window == window) return entry.Outranks;
        var outranks = Check(window);
        Volatile.Write(ref last, new Entry(window, outranks));
        return outranks;
    }

    private bool Check(IntPtr window)
    {
        if (GetWindowThreadProcessId(window, out var pid) == 0 || pid == 0) return false;
        if (pid == (uint)RuntimeCompat.ProcessId) return false;
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        // A process this one cannot even query (another user's, a protected one) is relayed too:
        // the SYSTEM worker can inject for it, and the Mac falls back to Bluetooth without one.
        if (process == IntPtr.Zero) return true;
        try { var level = Level(process); return level < 0 || level > own; }
        finally { CloseHandle(process); }
    }

    // The mandatory integrity RID of a process token (0x2000 medium, 0x3000 high), or -1.
    private static int Level(IntPtr process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token)) return -1;
        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var needed);
            if (needed <= 0 || needed > 1024) return -1;
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _)) return -1;
                var sid = Marshal.ReadIntPtr(buffer); // TOKEN_MANDATORY_LABEL.Label.Sid
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return count == 0 ? -1 : Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000, TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int type, IntPtr info, int length, out int needed);
    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
}
