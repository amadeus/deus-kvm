using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DeusKVM.Companion.Core;

// Windows dials the Mac only after a paste request; no Windows listening port/firewall rule.
public static class NetworkFileSender
{
    public static async Task Send(FileClipboardOffer request, Func<uint, int, byte[]?> read, CancellationToken stop)
    {
        var endpoint = request.Network;
        if (endpoint?.Valid != true) throw new IOException("Invalid file receiver");
        foreach (var host in endpoint.Hosts)
        {
            stop.ThrowIfCancellationRequested();
            using var socket = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            using var setup = CancellationTokenSource.CreateLinkedTokenSource(stop); setup.CancelAfter(2000);
            NetworkStream wire;
            try { await socket.ConnectAsync(IPAddress.Parse(host), endpoint.Port, setup.Token); wire = socket.GetStream(); }
            catch (Exception e) when (!stop.IsCancellationRequested && e is SocketException or OperationCanceledException) { continue; }
            var client = RuntimeCompat.RandomBytes(32); var server = new byte[32];
            await wire.WriteAsync(client, setup.Token); await wire.ReadExactlyAsync(server, setup.Token);
            var key = Convert.FromBase64String(endpoint.Key);
            using var incoming = new FileNetworkCrypto(key, client, server, "server");
            using var outgoing = new FileNetworkCrypto(key, client, server, "client");
            RuntimeCompat.ZeroMemory(key);
            var header = new byte[4];
            // One status+block plaintext and one sealed frame per transfer. LINQ Concat/ToArray
            // copies byte by byte on .NET Framework: milliseconds and large-heap garbage per block.
            var reply = new byte[1 + FileNetworkCrypto.MaximumBlock];
            var frame = new byte[4 + reply.Length + 16];
            while (true)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop); deadline.CancelAfter(15000);
                var n = await wire.ReadAsync(header.AsMemory(0, 1), deadline.Token);
                if (n == 0) return;
                await wire.ReadExactlyAsync(header.AsMemory(1), deadline.Token);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 32) throw new IOException("Invalid file request length");
                var record = new byte[32]; await wire.ReadExactlyAsync(record, deadline.Token);
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
                await wire.WriteAsync(frame.AsMemory(0, 4 + length), deadline.Token);
                if (bytes is null) return;
            }
        }
        throw new IOException("File transfer requires a local network connection");
    }
}
