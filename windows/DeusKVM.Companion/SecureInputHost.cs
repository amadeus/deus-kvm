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
/// a SYSTEM-only pipe to the worker and forwards relative pointer records to it. Records are
/// written by a dedicated thread with blocking writes, coalescing whatever queued meanwhile, so a
/// busy service thread or the thread pool never delays the pointer on the secure desktop.
/// Connection changes and every injection status the worker reports are raised as they happen
/// (on a background thread), so the desktop worker can fall back to Bluetooth or resume the relay
/// without waiting for the next refresh. The only channel that reaches the worker is this
/// SYSTEM-only pipe: no process without SYSTEM rights can cause secure-desktop injection.
/// </summary>
internal sealed class SecureInputHost(Action<string>? note = null, Action<bool>? connectedChanged = null,
    Action<bool>? status = null) : IDisposable
{
    private const int MaxPending = 65536;
    private readonly object gate = new();
    private readonly AutoResetEvent pending = new(false); // never disposed: Forward may race Dispose
    private byte[] buffer = new byte[4096];
    private int buffered;
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

    // Records (any number, whole records) to inject on the secure desktop; any thread. Queued for
    // the writer thread; dropped while no worker is connected or behind a stuck worker.
    public void Forward(byte[] records)
    {
        if (records.Length == 0 || !connected) return;
        lock (gate)
        {
            if (buffered + records.Length > MaxPending) return;
            if (buffered + records.Length > buffer.Length)
                Array.Resize(ref buffer, Math.Max(buffer.Length * 2, buffered + records.Length));
            Buffer.BlockCopy(records, 0, buffer, buffered, records.Length);
            buffered += records.Length;
        }
        pending.Set();
    }

    private async Task RunAsync(NamedPipeServerStream connection, int pid, CancellationToken stop)
    {
        var up = false;
        try
        {
            await connection.WaitForConnectionAsync(stop).WaitAsync(TimeSpan.FromSeconds(10), stop);
            if (!GetNamedPipeClientProcessId(connection.SafePipeHandle, out var peer) || peer != pid)
                throw new InvalidDataException("Unexpected secure pipe client");
            lock (gate) buffered = 0;
            connected = up = true;
            note?.Invoke("secure worker: ready");
            connectedChanged?.Invoke(true);
            new Thread(() => WriteLoop(connection, stop))
                { IsBackground = true, Name = "DeusKVM secure relay", Priority = ThreadPriority.AboveNormal }.Start();
            var input = new byte[256];
            var filled = 0;
            while (!stop.IsCancellationRequested)
            {
                var read = await connection.ReadAsync(input.AsMemory(filled, input.Length - filled), stop);
                if (read <= 0) break;
                filled += read;
                var frames = SecureFrame.Parse(input, filled, out var consumed);
                foreach (var frame in frames) if (frame.Length == 1) Report(frame[0] == 1);
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
                pending.Set(); // the writer notices the closed or cancelled connection
                connectedChanged?.Invoke(false);
            }
        }
    }

    // Blocking writes on their own thread: everything queued since the last write goes out as one
    // write of record-aligned frames.
    private void WriteLoop(Stream connection, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested && connected)
            {
                pending.WaitOne(250);
                byte[] records;
                int count;
                lock (gate)
                {
                    if (buffered == 0) continue;
                    count = buffered;
                    records = new byte[count];
                    Buffer.BlockCopy(buffer, 0, records, 0, count);
                    buffered = 0;
                }
                var frames = SecureFrame.Batch(records, count);
                if (frames.Length > 0) connection.Write(frames, 0, frames.Length);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            try { connection.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    // Every status the worker sends is passed on: the worker reports at the start of each burst
    // and on every change, and the desktop worker times failures per secure-desktop episode.
    private void Report(bool injecting)
    {
        if (injecting != lastInjecting)
        {
            lastInjecting = injecting;
            note?.Invoke(injecting ? "secure worker: injecting" : "secure worker: cannot inject here");
        }
        status?.Invoke(injecting);
    }

    public void Stop()
    {
        var was = connected;
        connected = false;
        lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
        pipe?.Dispose(); pipe = null;
        process?.Dispose(); process = null;
        sessionId = uint.MaxValue;
        lock (gate) buffered = 0;
        pending.Set();
        if (was) connectedChanged?.Invoke(false);
    }

    public void Dispose() { disposed = true; Stop(); }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
