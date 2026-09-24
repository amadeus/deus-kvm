using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DeusKVM.Companion.Core;

public sealed class NetworkUnavailableException() : IOException("File transfer requires a local network connection. Copy the file again when both computers are reachable.");

// Lazy, persistent connection. Creation and clipboard metadata inspection do no I/O.
public sealed class NetworkFileReader(FileClipboardOffer offer, Action<string>? diagnostic = null) : IFileBlockReader
{
    private readonly CancellationTokenSource lifetime = new();
    private TcpClient? client;
    private NetworkStream? stream;
    private FileNetworkCrypto? outgoing, incoming;
    private bool delivered;
    private long lastProgress;
    public void Dispose() { lifetime.Cancel(); client?.Dispose(); outgoing?.Dispose(); incoming?.Dispose(); }

    public byte[] Read(uint offset)
    {
        try { return ReadAsync(offset).GetAwaiter().GetResult(); }
        catch (Exception error) when (!delivered && !lifetime.IsCancellationRequested &&
            error is SocketException or IOException or OperationCanceledException or CryptographicException)
        {
            diagnostic?.Invoke("network-unavailable transfer-failed");
            throw new NetworkUnavailableException();
        }
    }

    private async Task Connect()
    {
        var endpoint = offer.Network ?? throw new NetworkUnavailableException();
        if (!endpoint.Valid) throw new NetworkUnavailableException();
        foreach (var host in endpoint.Hosts)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            attempt.CancelAfter(1000);
            var next = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            client = next;
            FileNetworkCrypto? send = null, receive = null;
            try
            {
                await next.ConnectAsync(IPAddress.Parse(host), endpoint.Port, attempt.Token).ConfigureAwait(false);
                var wire = next.GetStream();
                var clientNonce = RuntimeCompat.RandomBytes(32); var serverNonce = new byte[32];
                await wire.WriteAsync(clientNonce, attempt.Token).ConfigureAwait(false);
                await wire.ReadExactlyAsync(serverNonce, attempt.Token).ConfigureAwait(false);
                var secret = Convert.FromBase64String(endpoint.Key);
                try
                {
                    send = new(secret, clientNonce, serverNonce, "client");
                    receive = new(secret, clientNonce, serverNonce, "server");
                }
                finally { RuntimeCompat.ZeroMemory(secret); }
                outgoing = send; incoming = receive; stream = wire;
                return;
            }
            catch (Exception error) when (error is SocketException or IOException or OperationCanceledException)
            { send?.Dispose(); receive?.Dispose(); next.Dispose(); }
        }
        throw new NetworkUnavailableException();
    }

    private async Task<byte[]> ReadAsync(uint offset)
    {
        lifetime.Token.ThrowIfCancellationRequested();
        if (offset > offer.Size) throw new InvalidDataException("Invalid file offset");
        var count = Math.Min(FileNetworkCrypto.MaximumBlock, offer.Size - (int)offset);
        if (count == 0) return [];
        if (stream is null) await Connect().ConfigureAwait(false);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        request.CancelAfter(15000);
        var plain = ClipboardTransfer.Header(offer.Epoch, offer.Sequence, offset).Concat(new byte[4]).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(12), (uint)count);
        var encrypted = outgoing!.Seal(plain);
        var header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)encrypted.Length);
        await stream!.WriteAsync(header.Concat(encrypted).ToArray(), request.Token).ConfigureAwait(false);
        await stream!.ReadExactlyAsync(header, request.Token).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (size is < 17 or > FileNetworkCrypto.MaximumBlock + 17) throw new InvalidDataException("Invalid network response size");
        var ciphertext = new byte[(int)size];
        await stream!.ReadExactlyAsync(ciphertext, request.Token).ConfigureAwait(false);
        var response = incoming!.Open(ciphertext);
        // Authentication succeeded: source errors must not silently downgrade.
        delivered = true;
        if (response[0] != 0 || response.Length != count + 1) throw new IOException("Mac file changed or became unavailable");
        var now = RuntimeCompat.TickCount64;
        if (offset == 0 || offset + count == offer.Size || now - lastProgress >= 1000)
        { lastProgress = now; diagnostic?.Invoke($"network-block offset={offset} count={count} size={offer.Size}"); }
        return response.AsSpan(1).ToArray();
    }
}
