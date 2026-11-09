using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeusKVM.Companion;

/// <summary>
/// Launches `--secure-worker` as SYSTEM in the console session, so it can attach to and inject
/// on the secure desktop. Built from the service's own primary token: duplicate it, move the
/// copy to the console session (needs SE_TCB, which LocalSystem holds), and CreateProcessAsUser.
/// Any failure throws and the caller leaves the Bluetooth fallback in place.
/// </summary>
internal sealed class SecureInputProcess : IDisposable
{
    private readonly SafeProcessHandle process;
    private readonly SafeFileHandle thread;
    public int Id { get; }

    private SecureInputProcess(ProcessInfo info)
    {
        process = new SafeProcessHandle(info.Process, ownsHandle: true);
        thread = new SafeFileHandle(info.Thread, ownsHandle: true);
        Id = (int)info.ProcessId;
    }

    public bool HasExited => WaitForSingleObject(process, 0) != 0x102;

    public void Resume()
    {
        if (ResumeThread(thread) == uint.MaxValue) throw NativeError("ResumeThread");
        thread.Dispose();
    }

    public void Dispose()
    {
        if (!process.IsClosed)
        {
            if (!HasExited) TerminateProcess(process, 0);
            process.Dispose();
        }
        thread.Dispose();
    }

    public static SecureInputProcess Launch(uint consoleSession, string pipeName)
    {
        EnablePrivilege("SeTcbPrivilege");
        if (!OpenProcessToken(GetCurrentProcess(), TokenDuplicate | TokenQuery | TokenAssignPrimary | TokenAdjustDefault | TokenAdjustSessionId, out var own))
            throw NativeError("OpenProcessToken");
        using (own)
        {
            if (!DuplicateTokenEx(own, TokenAllAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var dup))
                throw NativeError("DuplicateTokenEx");
            using (dup)
            {
                var session = consoleSession;
                var handle = GCHandle.Alloc(session, GCHandleType.Pinned);
                try
                {
                    if (!SetTokenInformation(dup, TokenSessionId, handle.AddrOfPinnedObject(), sizeof(uint)))
                        throw NativeError("SetTokenInformation(session)");
                }
                finally { handle.Free(); }
                return Create(dup, pipeName);
            }
        }
    }

    private static SecureInputProcess Create(SafeAccessTokenHandle token, string pipeName)
    {
        var exe = RuntimeCompat.ProcessPath!;
        var command = new StringBuilder($"\"{exe}\" --secure-worker {pipeName} {RuntimeCompat.ProcessId}");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
        if (!CreateEnvironmentBlock(out var environment, token, false)) throw NativeError("CreateEnvironmentBlock");
        try
        {
            // CREATE_BREAKAWAY_FROM_JOB | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED | CREATE_NO_WINDOW.
            const uint flags = 0x01000000 | 0x400 | 4 | 0x08000000;
            if (!CreateProcessAsUser(token, exe, command, IntPtr.Zero, IntPtr.Zero, false, flags,
                environment, AppContext.BaseDirectory, ref startup, out var result))
                throw NativeError("CreateProcessAsUser");
            return new SecureInputProcess(result);
        }
        finally { DestroyEnvironmentBlock(environment); }
    }

    // Enables one privilege on the current process token (already held by LocalSystem, just
    // disabled by default); a failure here surfaces as the later SetTokenInformation failure.
    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token)) return;
        using (token)
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return;
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 /* SE_PRIVILEGE_ENABLED */ };
            AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static Win32Exception NativeError(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{operation}: {new Win32Exception(error).Message}");
    }

    private const uint TokenDuplicate = 0x0002, TokenQuery = 0x0008, TokenAssignPrimary = 0x0001,
        TokenAdjustDefault = 0x0080, TokenAdjustSessionId = 0x0100, TokenAdjustPrivileges = 0x0020, TokenAllAccess = 0xF01FF;
    private const int TokenSessionId = 12, TokenPrimary = 1, SecurityImpersonation = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public int Count; public Luid Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint access, IntPtr attributes,
        int impersonationLevel, int tokenType, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int infoClass, IntPtr info, int length);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges privileges, int length, IntPtr previous, IntPtr returnLength);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr block, SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);
    [DllImport("userenv.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr block);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateProcessAsUserW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string app, StringBuilder command,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags,
        IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
