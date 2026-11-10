using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace DeusKVM.Companion.Core;

// Windows dials the Mac only after a paste request; no Windows listening port/firewall rule.
// Blocking socket I/O on a worker thread with a large send buffer: the .NET Framework async
// path (IOCP completion and a thread-pool hop per read and write) held transfers to a few MB/s
// on a gigabit link, while plain blocking sockets on the same machine reach line rate.
public static class NetworkFileSender
{
    private const int SendBuffer = 4 * 1024 * 1024;

    public static Task Send(FileClipboardOffer request, Func<uint, int, byte[]?> read, CancellationToken stop) =>
        Task.Run(() => SendBlocking(request, read, stop), CancellationToken.None);

    private static void SendBlocking(FileClipboardOffer request, Func<uint, int, byte[]?> read, CancellationToken stop)
    {
        var endpoint = request.Network;
        if (endpoint?.Valid != true) throw new IOException("Invalid file receiver");
        foreach (var host in endpoint.Hosts)
        {
            stop.ThrowIfCancellationRequested();
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { socket.SendBufferSize = SendBuffer; } catch (SocketException) { }
            // Cancellation closes the socket, which ends any blocked send or receive.
            using var registration = stop.Register(() => { try { socket.Close(); } catch (ObjectDisposedException) { } });
            if (!TryConnect(socket, host, endpoint.Port)) { stop.ThrowIfCancellationRequested(); continue; }
            try { Serve(socket, request, endpoint, read, stop); return; }
            catch (Exception error) when (stop.IsCancellationRequested && error is SocketException or IOException or ObjectDisposedException)
            { throw new OperationCanceledException(stop); }
            catch (SocketException error) { throw new IOException(error.Message, error); }
        }
        throw new IOException("File transfer requires a local network connection");
    }

    private static bool TryConnect(Socket socket, string host, int port)
    {
        try
        {
            var pending = socket.BeginConnect(IPAddress.Parse(host), port, null, null);
            if (!pending.AsyncWaitHandle.WaitOne(2000)) return false;
            socket.EndConnect(pending);
            return true;
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException) { return false; }
    }

    private static void Serve(Socket socket, FileClipboardOffer request, FileNetworkOffer endpoint, Func<uint, int, byte[]?> read, CancellationToken stop)
    {
        socket.ReceiveTimeout = 2000; socket.SendTimeout = 15000;
        var client = RuntimeCompat.RandomBytes(32); var server = new byte[32];
        SendAll(socket, client, client.Length); ReceiveExactly(socket, server, 0, server.Length);
        var key = Convert.FromBase64String(endpoint.Key);
        using var incoming = new FileNetworkCrypto(key, client, server, "server");
        using var outgoing = new FileNetworkCrypto(key, client, server, "client");
        RuntimeCompat.ZeroMemory(key);
        socket.ReceiveTimeout = 15000;
        var header = new byte[4];
        var record = new byte[32];
        // One status+block plaintext and one sealed frame per transfer, reused for every block.
        var reply = new byte[1 + FileNetworkCrypto.MaximumBlock];
        var frame = new byte[4 + reply.Length + 16];
        while (true)
        {
            // The Mac closes the connection once it has every block: a clean end, not an error.
            if (socket.Receive(header, 0, 1, SocketFlags.None) == 0) return;
            ReceiveExactly(socket, header, 1, 3);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 32) throw new IOException("Invalid file request length");
            ReceiveExactly(socket, record, 0, record.Length);
            var plain = incoming.Open(record);
            if (ClipboardTransfer.Read(plain, 0) != request.Epoch || ClipboardTransfer.Read(plain, 4) != request.Sequence)
                throw new IOException("Expired file request");
            var offset = ClipboardTransfer.Read(plain, 8); var count = ClipboardTransfer.Read(plain, 12);
            if (offset > request.Size || count is 0 or > FileNetworkCrypto.MaximumBlock) throw new IOException("Invalid file range");
            stop.ThrowIfCancellationRequested();
            var bytes = read(offset, (int)count);
            stop.ThrowIfCancellationRequested();
            reply[0] = bytes is null ? (byte)1 : (byte)0;
            if (bytes is not null) Buffer.BlockCopy(bytes, 0, reply, 1, bytes.Length);
            var length = outgoing.Seal(reply.AsSpan(0, 1 + (bytes?.Length ?? 0)), frame.AsSpan(4));
            BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)length);
            SendAll(socket, frame, 4 + length);
            if (bytes is null) return;
        }
    }

    private static void SendAll(Socket socket, byte[] data, int count)
    {
        var sent = 0;
        while (sent < count)
        {
            var n = socket.Send(data, sent, count - sent, SocketFlags.None);
            if (n <= 0) throw new IOException("Network connection closed");
            sent += n;
        }
    }

    private static void ReceiveExactly(Socket socket, byte[] into, int offset, int count)
    {
        var got = 0;
        while (got < count)
        {
            var n = socket.Receive(into, offset + got, count - got, SocketFlags.None);
            if (n <= 0) throw new IOException("Network connection closed");
            got += n;
        }
    }
}
