using System.Runtime.InteropServices;
using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

/// <summary>
/// Desktop names alone do not describe the lock screen: it can remain on Default. Query the
/// current process's console session, including when the helper starts after the session locked.
/// </summary>
internal static class SessionLock
{
    public static SessionLockState Read(int session)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, (uint)session, 25, out var buffer, out var bytes))
            return SessionLockState.Unknown;
        try { return Parse(buffer, bytes, session); }
        finally { WTSFreeMemory(buffer); }
    }

    internal static SessionLockState Parse(IntPtr buffer, uint bytes, int session)
    {
        if (buffer == IntPtr.Zero || bytes < Marshal.SizeOf<SessionInformation>()) return SessionLockState.Unknown;
        var info = Marshal.PtrToStructure<SessionInformation>(buffer);
        if (info.Level != 1 || info.Data.SessionId != session) return SessionLockState.Unknown;
        // Win7 reversed these flags; supported Windows 10/11 use the documented values.
        return info.Data.Flags switch { 0 => SessionLockState.Locked, 1 => SessionLockState.Unlocked, _ => SessionLockState.Unknown };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SessionInformation { public uint Level; public LevelOne Data; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LevelOne
    {
        public uint SessionId;
        public int State, Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string Station;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string User;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)] public string Domain;
        public long LogonTime, ConnectTime, DisconnectTime, LastInputTime, CurrentTime;
        public uint IncomingBytes, OutgoingBytes, IncomingFrames, OutgoingFrames, IncomingCompressedBytes, OutgoingCompressedBytes;
    }
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint session, int information, out IntPtr buffer, out uint bytes);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr buffer);
}
