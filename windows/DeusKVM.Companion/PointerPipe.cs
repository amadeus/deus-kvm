using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using DeusKVM.Companion.Core;
using Microsoft.Win32.SafeHandles;

namespace DeusKVM.Companion;

/// <summary>
/// Service end of the pointer pipe: the desktop worker's network pointer records, on their way to
/// the SYSTEM secure-input worker that injects all of them. Separate from the JSON desktop pipe and
/// on plain blocking I/O with its own thread, because .NET Framework's async pipe I/O completes
/// through the thread pool, which costs a thread handoff on every read. Same DACL as the desktop
/// pipe (the console user and SYSTEM, never the network) and the client must be the desktop worker
/// this service launched. When the worker goes away, held buttons are released.
/// </summary>
internal sealed class PointerPipeServer : IDisposable
{
    private readonly string name;
    private readonly NamedPipeServerStream pipe;
    private readonly Action<byte[]> records;
    private volatile bool stopped;

    public PointerPipeServer(string name, PipeSecurity security, Action<byte[]> records)
    {
        this.name = name;
        this.records = records;
        // Duplex but only read here (and only written by the client), so the synchronous handle never
        // has a read and a write competing for it.
        pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None, 0, 16384, security);
    }

    public void Start(int pid) =>
        new Thread(() => Run(pid)) { IsBackground = true, Name = "DeusKVM pointer relay", Priority = ThreadPriority.AboveNormal }.Start();

    private void Run(int pid)
    {
        var connected = false;
        try
        {
            pipe.WaitForConnection();
            if (stopped || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var peer) || peer != pid) return;
            connected = true;
            var buffer = new byte[8192];
            var filled = 0;
            while (!stopped)
            {
                var read = pipe.Read(buffer, filled, buffer.Length - filled);
                if (read <= 0) break;
                filled += read;
                var frames = SecureFrame.Parse(buffer, filled, out var consumed);
                foreach (var body in frames) records(body);
                if (consumed > 0) { Buffer.BlockCopy(buffer, consumed, buffer, 0, filled - consumed); filled -= consumed; }
                if (filled == buffer.Length) break; // a frame larger than the buffer: malformed
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidDataException or InvalidOperationException) { }
        finally { if (connected) records(SecureInputRecord.ReleaseRecord); }
    }

    public void Dispose()
    {
        stopped = true;
        // A wait for a client that never came is released by connecting to it once ourselves.
        try { using var wake = new NamedPipeClientStream(".", name, PipeDirection.InOut); wake.Connect(0); }
        catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
        pipe.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}

/// <summary>
/// Desktop worker end of the pointer pipe. The network thread only appends to a buffer and signals;
/// a dedicated thread writes everything queued since its last write as record-aligned frames, with
/// blocking writes, so the network thread never waits on the pipe. Each batch starts with a mark
/// carrying the arrival time of its oldest record, so the SYSTEM worker can time the whole path.
/// The server must be the service that launched this worker.
/// </summary>
internal sealed class PointerPipeClient
{
    private const int Limit = 16384;
    private readonly object gate = new();
    private readonly AutoResetEvent pending = new(false);
    private readonly Action<string> note;
    private byte[] buffer = new byte[4096];
    private int buffered;
    private volatile bool connected;
    private long writingSince; // Stopwatch ticks while a write is in progress, else 0

    public PointerPipeClient(string name, int parent, Action<string> note)
    {
        this.note = note;
        new Thread(() => Run(name, parent)) { IsBackground = true, Name = "DeusKVM pointer relay", Priority = ThreadPriority.AboveNormal }.Start();
    }

    public bool Connected => connected;

    // A write that has not finished in 250 ms (the service or the SYSTEM worker is stuck), or a full
    // buffer: the caller stops relaying and falls back.
    public bool Stuck(long now)
    {
        var since = Interlocked.Read(ref writingSince);
        if (since != 0 && now - since > Stopwatch.Frequency / 4) return true;
        lock (gate) return buffered >= Limit - 64;
    }

    // Any thread; never blocks on the pipe. False when the record could not be queued.
    public bool Enqueue(byte[] record, long arrived)
    {
        if (!connected) return false;
        lock (gate)
        {
            var mark = buffered == 0 ? SecureInputRecord.EncodeMark(arrived) : null;
            var length = record.Length + (mark?.Length ?? 0);
            if (buffered + length > Limit) return false;
            if (buffered + length > buffer.Length) Array.Resize(ref buffer, Math.Max(buffer.Length * 2, buffered + length));
            if (mark is not null) { Buffer.BlockCopy(mark, 0, buffer, buffered, mark.Length); buffered += mark.Length; }
            Buffer.BlockCopy(record, 0, buffer, buffered, record.Length);
            buffered += record.Length;
        }
        pending.Set();
        return true;
    }

    private void Run(string name, int parent)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(10000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server) || server != parent)
                throw new InvalidDataException("Unexpected pointer pipe server");
            connected = true;
            note("pointer relay: connected");
            while (true)
            {
                pending.WaitOne();
                byte[] records;
                lock (gate)
                {
                    if (buffered == 0) continue;
                    records = new byte[buffered];
                    Buffer.BlockCopy(buffer, 0, records, 0, buffered);
                    buffered = 0;
                }
                var frames = SecureFrame.Batch(records, records.Length);
                Interlocked.Exchange(ref writingSince, Stopwatch.GetTimestamp());
                pipe.Write(frames, 0, frames.Length);
                Interlocked.Exchange(ref writingSince, 0);
            }
        }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException or ObjectDisposedException or UnauthorizedAccessException)
        {
            connected = false;
            Interlocked.Exchange(ref writingSince, 0);
            note("pointer relay: unavailable, injecting directly (" + error.Message + ")");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
