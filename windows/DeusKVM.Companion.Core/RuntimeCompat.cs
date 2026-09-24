using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DeusKVM.Companion.Core;

// Shared, testable adapters keep the wire protocol and policies identical on both runtimes.
public static class RuntimeCompat
{
    public static string ProcessPath { get; } = ReadProcessPath();
    private static string ReadProcessPath() { using var process = Process.GetCurrentProcess(); return process.MainModule!.FileName!; }
    public static int ProcessId { get; } = ReadProcessId();
    private static int ReadProcessId() { using var process = Process.GetCurrentProcess(); return process.Id; }
#if NETFRAMEWORK
    public static long TickCount64 => (long)GetTickCount64();
    [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
#else
    public static long TickCount64 => Environment.TickCount64;
#endif
    public static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));
    public static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));
    public static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count]; using var rng = RandomNumberGenerator.Create(); rng.GetBytes(bytes); return bytes;
    }
    public static byte[] Sha256(Stream stream) { using var hash = SHA256.Create(); return hash.ComputeHash(stream); }
    public static byte[] Sha256(byte[] bytes) { using var hash = SHA256.Create(); return hash.ComputeHash(bytes); }
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static void ZeroMemory(byte[] bytes) => Array.Clear(bytes, 0, bytes.Length);
    public static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");

    public static bool IsFullyQualifiedWindowsPath(string path) =>
        path.Length >= 3 && ((char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')) ||
            (path[0] == '\\' && path[1] == '\\'));
    public static TValue GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> map, TKey key) where TKey : notnull =>
        map.TryGetValue(key, out var value) ? value : default!;
    public static void MoveReplace(string source, string destination)
    {
        if (File.Exists(destination)) File.Replace(source, destination, null);
        else File.Move(source, destination);
    }
    public static bool Remove<TKey, TValue>(this Dictionary<TKey, TValue> map, TKey key, out TValue value) where TKey : notnull
    {
        if (!map.TryGetValue(key, out value!)) return false;
        return map.Remove(key);
    }

    // Our protocol uses one 32-byte SHA-256 output block (RFC 5869 extract + expand).
    public static byte[] DeriveFileKey(byte[] secret, byte[] salt, byte[] info)
    {
        using var extract = new HMACSHA256(salt);
        var prk = extract.ComputeHash(secret);
        try
        {
            using var expand = new HMACSHA256(prk);
            return expand.ComputeHash(info.Concat(new byte[] { 1 }).ToArray());
        }
        finally { ZeroMemory(prk); }
    }

    // CommandLineToArgvW / CRT quoting, including embedded quotes and trailing backslashes.
    public static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes);
            result.Append(ch); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    public static void SetArguments(this ProcessStartInfo start, IEnumerable<string> args) =>
        start.Arguments = string.Join(" ", args.Select(QuoteArgument));

    public static async Task WaitForExitAsync(this Process process, CancellationToken stop = default)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Exited(object? sender, EventArgs args) => done.TrySetResult(true);
        process.EnableRaisingEvents = true;
        process.Exited += Exited;
        try
        {
            if (process.HasExited) return;
            using var registration = stop.Register(() => done.TrySetCanceled());
            await done.Task.ConfigureAwait(false);
        }
        finally { process.Exited -= Exited; }
    }
    public static async Task WaitAsync(this Task task, TimeSpan timeout, CancellationToken stop = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var timer = Task.Delay(timeout, deadline.Token);
        if (await Task.WhenAny(task, timer).ConfigureAwait(false) != task)
        { stop.ThrowIfCancellationRequested(); throw new TimeoutException(); }
        deadline.Cancel();
        await task.ConfigureAwait(false);
    }
    public static async Task<T> WaitAsync<T>(this Task<T> task, TimeSpan timeout, CancellationToken stop = default)
    { await WaitAsync((Task)task, timeout, stop).ConfigureAwait(false); return await task.ConfigureAwait(false); }
    public static async Task ConnectAsync(this System.Net.Sockets.TcpClient client, System.Net.IPAddress address, int port, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        using var registration = stop.Register(client.Close);
        try { await client.ConnectAsync(address, port).ConfigureAwait(false); }
        catch (Exception) when (stop.IsCancellationRequested) { throw new OperationCanceledException(stop); }
        stop.ThrowIfCancellationRequested();
    }
    public static async Task<int> ReadAsync(this Stream stream, Memory<byte> memory, CancellationToken stop)
    {
        if (!MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)memory, out var segment))
            throw new ArgumentException("Array-backed I/O required", nameof(memory));
        stop.ThrowIfCancellationRequested();
        using var registration = stop.Register(stream.Dispose);
        try { return await stream.ReadAsync(segment.Array!, segment.Offset, segment.Count, stop).ConfigureAwait(false); }
        catch (Exception) when (stop.IsCancellationRequested) { throw new OperationCanceledException(stop); }
    }
    public static async Task WriteAsync(this Stream stream, ReadOnlyMemory<byte> memory, CancellationToken stop)
    {
        if (!MemoryMarshal.TryGetArray(memory, out var segment))
            throw new ArgumentException("Array-backed I/O required", nameof(memory));
        stop.ThrowIfCancellationRequested();
        using var registration = stop.Register(stream.Dispose);
        try { await stream.WriteAsync(segment.Array!, segment.Offset, segment.Count, stop).ConfigureAwait(false); }
        catch (Exception) when (stop.IsCancellationRequested) { throw new OperationCanceledException(stop); }
    }
    public static async Task ReadExactlyAsync(this Stream stream, Memory<byte> memory, CancellationToken stop)
    {
        while (memory.Length != 0)
        {
            var count = await ReadAsync(stream, memory, stop).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            memory = memory.Slice(count);
        }
    }
    public static void ReadExactly(this Stream stream, byte[] bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        { var count = stream.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
    }
}
