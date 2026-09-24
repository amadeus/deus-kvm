using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeusKVM.Companion;

// Retain the handles returned by CreateProcessAsUser: do not reopen the user's
// process with Process.Handle (PROCESS_ALL_ACCESS). The desktop process cannot
// inherit the BLE worker's Session 0 job. Its pipe lifetime is monitored off the
// desktop thread so it exits if the service/worker crashes, even if UI is hung.
internal sealed class DesktopProcess : IDisposable
{
    private readonly SafeProcessHandle process;
    private readonly SafeFileHandle thread;
    public int Id { get; }

    private DesktopProcess(ProcessInfo info)
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

    public static DesktopProcess Launch(SafeAccessTokenHandle token, string pipeName, string address)
    {
        var exe = RuntimeCompat.ProcessPath!;
        var command = new StringBuilder($"\"{exe}\" --desktop-worker {pipeName} {RuntimeCompat.ProcessId} {address}");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
        if (!CreateEnvironmentBlock(out var environment, token, false)) throw NativeError("CreateEnvironmentBlock");
        try
        {
            // CREATE_BREAKAWAY_FROM_JOB | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED.
            // The service's BLE job permits explicit breakaway; ordinary children remain contained.
            const uint flags = 0x01000000 | 0x400 | 4;
            if (!CreateProcessAsUser(token, exe, command, IntPtr.Zero, IntPtr.Zero, false, flags,
                environment, AppContext.BaseDirectory, ref startup, out var result))
                throw NativeError("CreateProcessAsUser");
            return new DesktopProcess(result);
        }
        finally { DestroyEnvironmentBlock(environment); }
    }

    private static Win32Exception NativeError(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{operation}: {new Win32Exception(error).Message}");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
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
