using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using DeusKVM.Companion.Core;
using Microsoft.Win32.SafeHandles;

namespace DeusKVM.Companion;

/// <summary>
/// Service-side supervisor for the SYSTEM secure-input worker (NETWORK_POINTER_PLAN.md). It owns
/// a SYSTEM-only pipe to the worker and forwards every network pointer record to it, written
/// straight from the pointer pipe's reader thread (see <see cref="PointerPipeServer"/>), so neither
/// the service thread nor the thread pool sits in the path. Connection changes, every injection
/// status and the latency windows the worker reports are raised as they happen (on a background
/// thread). The only channel that reaches the worker is this SYSTEM-only pipe: no process without
/// SYSTEM rights can make it inject.
/// </summary>
internal sealed class SecureInputHost(Action<string>? note = null, Action<bool>? connectedChanged = null,
    Action<bool, string?>? status = null, Action<byte[]>? stats = null) : IDisposable
{
    private readonly object writeGate = new();
    private readonly OverlappedWriter writer = new();
    private volatile NamedPipeServerStream? active;
    private SecureInputProcess? process;
    private NamedPipeServerStream? pipe;
    private CancellationTokenSource? lifetime;
    private uint sessionId = uint.MaxValue;
    private volatile bool connected;
    private bool lastInjecting = true;
    private bool disposed;

    public bool Connected => connected;

    // Keeps a worker running for the console session; started on demand, replaced if the session
    // changes or the worker exits. Never throws: a failure leaves Connected false.
    public void Refresh()
    {
        if (disposed) return;
        var console = ConsoleSession.Read();
        if (console.Id == uint.MaxValue || console.State == "Signed out") { Stop(); return; }
        if (process is { HasExited: false } && sessionId == console.Id && pipe is not null) return;
        Stop();
        try
        {
            sessionId = console.Id;
            var name = "DeusKVM.Secure." + Guid.NewGuid().ToString("N");
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 16384, 16384, security);
            process = SecureInputProcess.Launch(console.Id, name);
            lifetime = new CancellationTokenSource();
            _ = RunAsync(pipe, process.Id, lifetime.Token);
            process.Resume();
            note?.Invoke("secure worker: starting");
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            note?.Invoke("secure worker: unavailable, Bluetooth fallback kept (" + error.Message + ")");
            Stop();
        }
    }

    // Records (whole records, any number) to inject; any thread, normally the pointer pipe's reader.
    // A blocking write: a stuck worker backs up into the desktop worker, which then falls back.
    // Dropped while no worker is connected.
    public void Forward(byte[] records)
    {
        if (records.Length == 0 || !connected || active is not { } connection) return;
        var frames = SecureFrame.Batch(records, records.Length);
        if (frames.Length == 0) return;
        lock (writeGate)
        {
            try { writer.Write(connection.SafePipeHandle, frames); }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
            {
                try { connection.Dispose(); } catch (ObjectDisposedException) { } // the reader notices and reports it
            }
        }
    }

    private async Task RunAsync(NamedPipeServerStream connection, int pid, CancellationToken stop)
    {
        var up = false;
        try
        {
            await connection.WaitForConnectionAsync(stop).WaitAsync(TimeSpan.FromSeconds(10), stop);
            if (!GetNamedPipeClientProcessId(connection.SafePipeHandle, out var peer) || peer != pid)
                throw new InvalidDataException("Unexpected secure pipe client");
            active = connection;
            connected = up = true;
            note?.Invoke("secure worker: ready");
            connectedChanged?.Invoke(true);
            var input = new byte[256];
            var filled = 0;
            while (!stop.IsCancellationRequested)
            {
                var read = await connection.ReadAsync(input.AsMemory(filled, input.Length - filled), stop);
                if (read <= 0) break;
                filled += read;
                var frames = SecureFrame.Parse(input, filled, out var consumed);
                foreach (var frame in frames)
                {
                    if (frame.Length == 1) Report(frame[0] == 1, null);
                    else if (frame.Length == 6 && frame[0] == 0) Report(false, Failure(frame[1], BitConverter.ToInt32(frame, 2)));
                    else if (frame.Length == SecureRelayStats.Length && frame[0] == SecureRelayStats.Tag) stats?.Invoke(frame);
                }
                if (consumed > 0) { Buffer.BlockCopy(input, consumed, input, 0, filled - consumed); filled -= consumed; }
                if (filled == input.Length) break;
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException or InvalidDataException)
        {
            if (!stop.IsCancellationRequested) note?.Invoke("secure worker: disconnected (" + error.Message + ")");
        }
        finally
        {
            if (up)
            {
                connected = false;
                active = null;
                connectedChanged?.Invoke(false);
            }
        }
    }

    // Every status the worker sends is passed on: it reports at the start of each burst, on every
    // change, and repeats a failure every 100 ms while it lasts.
    private void Report(bool injecting, string? failure)
    {
        if (injecting != lastInjecting)
        {
            lastInjecting = injecting;
            note?.Invoke(injecting ? "secure worker: injecting" : "secure worker: cannot inject here (" + failure + ")");
        }
        status?.Invoke(injecting, failure);
    }

    private static string Failure(byte step, int error) => step switch
    {
        1 => "OpenInputDesktop",
        2 => "GetUserObjectInformation",
        3 => "SetThreadDesktop",
        4 => "SendInput",
        _ => "step " + step
    } + " error " + error;

    public void Stop()
    {
        var was = connected;
        connected = false;
        active = null;
        lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
        pipe?.Dispose(); pipe = null;
        process?.Dispose(); process = null;
        sessionId = uint.MaxValue;
        if (was) connectedChanged?.Invoke(false);
    }

    public void Dispose() { disposed = true; Stop(); }

    /// <summary>
    /// A synchronous write on the overlapped pipe handle without .NET's completion path: .NET
    /// Framework binds the handle to the thread pool's completion port and finishes even a
    /// synchronous Write there, a thread handoff per write. An event with its low bit set keeps the
    /// completion off the port; this thread waits for it directly. One write at a time (writeGate).
    /// </summary>
    private sealed class OverlappedWriter
    {
        private readonly IntPtr overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>()); // lives as long as the process
        private readonly IntPtr completed = CreateEvent(IntPtr.Zero, true, false, null);

        public void Write(SafePipeHandle handle, byte[] data)
        {
            if (completed == IntPtr.Zero) throw new IOException("No completion event");
            var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                Marshal.StructureToPtr(new NativeOverlapped { EventHandle = (IntPtr)((long)completed | 1) }, overlapped, false);
                if (!WriteFile(handle, pinned.AddrOfPinnedObject(), data.Length, IntPtr.Zero, overlapped))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 997) throw new IOException("Secure pipe write failed", new Win32Exception(error)); // not ERROR_IO_PENDING
                }
                // Waits for completion even when the write failed late, so the buffer is never unpinned early.
                if (!GetOverlappedResult(handle, overlapped, out var written, true) || written != data.Length)
                    throw new IOException("Secure pipe write failed", new Win32Exception(Marshal.GetLastWin32Error()));
            }
            finally { pinned.Free(); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteFile(SafePipeHandle handle, IntPtr buffer, int count, IntPtr written, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetOverlappedResult(SafePipeHandle handle, IntPtr overlapped, out int transferred,
            [MarshalAs(UnmanagedType.Bool)] bool wait);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateEvent(IntPtr attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
            [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
