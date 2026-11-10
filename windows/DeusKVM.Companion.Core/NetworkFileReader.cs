using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DeusKVM.Companion.Core;

public sealed class NetworkUnavailableException() : IOException("File transfer requires a local network connection. Copy the file again when both computers are reachable.");

// Lazy, persistent connection. Creation and clipboard metadata inspection do no I/O.
// Blocking socket I/O on the caller's thread: the .NET Framework async path (IOCP completion,
// thread-pool continuation, a pumping wait on the clipboard STA per block) held pastes to a few
// MB/s on a gigabit link, while plain blocking reads on the same machine reach line rate.
public sealed class NetworkFileReader(FileClipboardOffer offer, Action<string>? diagnostic = null) : IFileBlockReader
{
    // Sequential reads keep many block requests ahead; the large receive buffer lets the replies
    // land in the kernel while the consumer is still writing the previous block, so a read rarely
    // waits on the wire. Replies stay in request order.
    private const int ReadAhead = 16;
    private const int ReceiveBuffer = 4 * 1024 * 1024;
    private readonly Queue<(uint Offset, int Count)> requested = new();
    private readonly byte[] header = new byte[4];
    private readonly byte[] sealedBlock = new byte[FileNetworkCrypto.MaximumBlock + 17];
    private readonly byte[] plainBlock = new byte[FileNetworkCrypto.MaximumBlock + 1];
    private readonly byte[] request = new byte[4 + 16 + 16];
    private volatile Socket? socket;
    private volatile bool disposed;
    private FileNetworkCrypto? outgoing, incoming;
    private bool delivered;
    private long requestedEnd, sequentialEnd = -1;
    private long started, waitTicks, openTicks, lastReport, bytes;

    public void Dispose()
    {
        disposed = true;
        try { socket?.Close(); } catch (ObjectDisposedException) { }
        outgoing?.Dispose(); incoming?.Dispose();
    }

    public byte[] Read(uint offset)
    {
        try { return ReadBlock(offset); }
        catch (Exception error) when (disposed && error is not OperationCanceledException)
        { throw new OperationCanceledException("File transfer canceled", error); }
        catch (Exception error) when (!delivered && !disposed &&
            error is SocketException or IOException or CryptographicException or ObjectDisposedException)
        {
            diagnostic?.Invoke("network-unavailable transfer-failed");
            throw new NetworkUnavailableException();
        }
    }

    private byte[] ReadBlock(uint offset)
    {
        if (disposed) throw new OperationCanceledException();
        if (offset > offer.Size) throw new InvalidDataException("Invalid file offset");
        var count = Math.Min(FileNetworkCrypto.MaximumBlock, offer.Size - (int)offset);
        if (count == 0) return [];
        if (socket is null) Connect();
        // A seek abandons read-ahead; replies already requested are still read to keep the record counter in order.
        while (requested.Count > 0 && requested.Peek().Offset != offset) Receive(requested.Dequeue(), keep: false);
        if (requested.Count == 0) Request(offset, count);
        if (offset == 0 || offset == sequentialEnd)
            while (requested.Count <= ReadAhead && requestedEnd < offer.Size)
                Request((uint)requestedEnd, Math.Min(FileNetworkCrypto.MaximumBlock, offer.Size - (int)requestedEnd));
        var block = Receive(requested.Dequeue(), keep: true)!;
        sequentialEnd = offset + count;
        Report(offset, count);
        return block;
    }

    private void Connect()
    {
        var endpoint = offer.Network ?? throw new NetworkUnavailableException();
        if (!endpoint.Valid) throw new NetworkUnavailableException();
        foreach (var host in endpoint.Hosts)
        {
            if (disposed) throw new OperationCanceledException();
            var next = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            // Before connect, so the window scale negotiated in the handshake can use it.
            try { next.ReceiveBufferSize = ReceiveBuffer; } catch (SocketException) { }
            socket = next; // Dispose can close it while connecting
            try
            {
                var pending = next.BeginConnect(IPAddress.Parse(host), endpoint.Port, null, null);
                if (!pending.AsyncWaitHandle.WaitOne(1000)) throw new SocketException((int)SocketError.TimedOut);
                next.EndConnect(pending);
                QuickAck(next);
                next.ReceiveTimeout = 2000; next.SendTimeout = 2000;
                var clientNonce = RuntimeCompat.RandomBytes(32); var serverNonce = new byte[32];
                SendAll(next, clientNonce, clientNonce.Length);
                ReceiveExactly(next, serverNonce, serverNonce.Length);
                var secret = Convert.FromBase64String(endpoint.Key);
                try
                {
                    outgoing = new(secret, clientNonce, serverNonce, "client");
                    incoming = new(secret, clientNonce, serverNonce, "server");
                }
                finally { RuntimeCompat.ZeroMemory(secret); }
                next.ReceiveTimeout = 15000; next.SendTimeout = 15000;
                return;
            }
            catch (Exception error) when (!disposed && error is SocketException or IOException or ObjectDisposedException)
            {
                outgoing?.Dispose(); incoming?.Dispose(); outgoing = null; incoming = null;
                try { next.Close(); } catch (ObjectDisposedException) { }
                socket = null;
            }
        }
        throw new NetworkUnavailableException();
    }

    // Each 256 KiB block is one burst that ends with a lone tail segment; Windows holds its ACK
    // for the 200 ms delayed-ACK timer, which stalls the sender between blocks. Ask the stack to
    // ACK every segment. SIO_TCP_SET_ACK_FREQUENCY; a no-op where the control code is unsupported.
    private static void QuickAck(Socket target)
    {
        try { target.IOControl(unchecked((int)0x98000017), BitConverter.GetBytes(1), null); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        catch (PlatformNotSupportedException) { }
    }

    private void Request(uint offset, int count)
    {
        var plain = new byte[16];
        ClipboardTransfer.Header(offer.Epoch, offer.Sequence, offset).CopyTo(plain, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(12), (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)outgoing!.Seal(plain, request.AsSpan(4)));
        SendAll(socket!, request, request.Length);
        requested.Enqueue((offset, count)); requestedEnd = offset + count;
    }

    private byte[]? Receive((uint Offset, int Count) block, bool keep)
    {
        var waited = Stopwatch.GetTimestamp();
        ReceiveExactly(socket!, header, 4);
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (size is < 17 or > FileNetworkCrypto.MaximumBlock + 17) throw new InvalidDataException("Invalid network response size");
        ReceiveExactly(socket!, sealedBlock, size);
        var opened = Stopwatch.GetTimestamp();
        waitTicks += opened - waited;
        var plain = plainBlock.AsSpan(0, size - 16);
        incoming!.Open(sealedBlock.AsSpan(0, size), plain);
        openTicks += Stopwatch.GetTimestamp() - opened;
        // Authentication succeeded: source errors must not silently downgrade.
        delivered = true;
        if (plain[0] != 0 || plain.Length != block.Count + 1) throw new IOException("Mac file changed or became unavailable");
        if (!keep) return null;
        var result = new byte[block.Count];
        plain.Slice(1).CopyTo(result);
        return result;
    }

    private static void SendAll(Socket target, byte[] data, int count)
    {
        try
        {
            var sent = 0;
            while (sent < count)
            {
                var n = target.Send(data, sent, count - sent, SocketFlags.None);
                if (n <= 0) throw new IOException("Network connection closed");
                sent += n;
            }
        }
        catch (SocketException error) { throw new IOException(error.Message, error); }
    }

    private static void ReceiveExactly(Socket source, byte[] into, int count)
    {
        try
        {
            var got = 0;
            while (got < count)
            {
                var n = source.Receive(into, got, count - got, SocketFlags.None);
                if (n <= 0) throw new IOException("Network connection closed");
                got += n;
            }
        }
        catch (SocketException error) { throw new IOException(error.Message, error); }
    }

    // Once a second, plus the first and last block: throughput and where the time went
    // (waiting on the socket vs decrypting). Counts and timings only.
    private void Report(uint offset, int count)
    {
        var now = Stopwatch.GetTimestamp();
        if (started == 0) started = now;
        bytes += count;
        var last = offset + count == offer.Size;
        if (offset != 0 && !last && now - lastReport < Stopwatch.Frequency) return;
        lastReport = now;
        var seconds = Math.Max((now - started) / (double)Stopwatch.Frequency, 0.000001);
        var mb = bytes / 1_000_000.0;
        diagnostic?.Invoke(FormattableString.Invariant(
            $"network-block offset={offset} count={count} size={offer.Size} MB={mb:F1} s={seconds:F2} MBps={mb / seconds:F1} waitMs={waitTicks * 1000.0 / Stopwatch.Frequency:F0} openMs={openTicks * 1000.0 / Stopwatch.Frequency:F0}"));
    }
}
