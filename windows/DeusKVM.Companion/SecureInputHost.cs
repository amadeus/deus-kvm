using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using DeusKVM.Companion.Core;
using Microsoft.Win32.SafeHandles;

namespace DeusKVM.Companion;

/// <summary>
/// Service-side supervisor for the SYSTEM secure-input worker (NETWORK_POINTER_PLAN.md). It owns
/// a SYSTEM-only pipe to the worker, forwards relative pointer records to it, and exposes whether
/// the worker can inject on the current (possibly secure) desktop. If the worker cannot be
/// started or reports it cannot inject, `Ready` is false and the caller keeps the Bluetooth
/// fallback. The only channel that reaches the worker is this SYSTEM-only pipe: no process
/// without SYSTEM rights can cause secure-desktop injection.
/// </summary>
internal sealed class SecureInputHost(Action<string>? note = null) : IDisposable
{
    private SecureInputProcess? process;
    private NamedPipeServerStream? pipe;
    private CancellationTokenSource? lifetime;
    private Channel<byte[]>? outgoing;
    private uint sessionId = uint.MaxValue;
    private volatile bool connected;
    private volatile bool workerReady;
    private bool disposed;

    public bool Ready => connected && workerReady;

    // Keeps a worker running for the console session; started on demand, replaced if the session
    // changes or the worker exits. Never throws: a failure leaves Ready false.
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
            outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
                { FullMode = BoundedChannelFullMode.DropOldest });
            _ = RunAsync(pipe, process.Id, outgoing, lifetime.Token);
            process.Resume();
            note?.Invoke("secure worker: starting");
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            note?.Invoke("secure worker: unavailable, Bluetooth fallback kept (" + error.Message + ")");
            Stop();
        }
    }

    // One batch of records to inject on the secure desktop. Dropped silently when no worker is
    // connected (the caller only forwards while Ready).
    public void Forward(byte[] records)
    {
        if (records.Length == 0) return;
        try { outgoing?.Writer.TryWrite(SecureFrame.Wrap(records)); }
        catch (InvalidOperationException) { }
    }

    private async Task RunAsync(NamedPipeServerStream connection, int pid, Channel<byte[]> queue, CancellationToken stop)
    {
        try
        {
            await connection.WaitForConnectionAsync(stop).WaitAsync(TimeSpan.FromSeconds(10), stop);
            if (!GetNamedPipeClientProcessId(connection.SafePipeHandle, out var peer) || peer != pid)
                throw new InvalidDataException("Unexpected secure pipe client");
            connected = true; workerReady = true; // connected and not yet told otherwise
            note?.Invoke("secure worker: ready");
            var writer = WriteAsync();
            try
            {
                var buffer = new byte[256];
                var filled = 0;
                while (!stop.IsCancellationRequested)
                {
                    var read = await connection.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), stop);
                    if (read <= 0) break;
                    filled += read;
                    var frames = SecureFrame.Parse(buffer, filled, out var consumed);
                    foreach (var frame in frames) if (frame.Length == 1) SetReady(frame[0] == 1);
                    if (consumed > 0) { Buffer.BlockCopy(buffer, consumed, buffer, 0, filled - consumed); filled -= consumed; }
                    if (filled == buffer.Length) break;
                }
            }
            finally { queue.Writer.TryComplete(); await writer; }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException or InvalidDataException)
        {
            if (!stop.IsCancellationRequested) note?.Invoke("secure worker: disconnected (" + error.Message + ")");
        }
        finally { connected = false; workerReady = false; }

        async Task WriteAsync()
        {
            try { await foreach (var frame in queue.Reader.ReadAllAsync(stop)) await connection.WriteAsync(frame, stop); }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { connection.Dispose(); }
        }
    }

    private void SetReady(bool ready)
    {
        if (workerReady == ready) return;
        workerReady = ready;
        note?.Invoke(ready ? "secure worker: injecting" : "secure worker: cannot inject here, Bluetooth fallback");
    }

    public void Stop()
    {
        connected = false; workerReady = false;
        lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
        outgoing?.Writer.TryComplete(); outgoing = null;
        pipe?.Dispose(); pipe = null;
        process?.Dispose(); process = null;
        sessionId = uint.MaxValue;
    }

    public void Dispose() { disposed = true; Stop(); }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
