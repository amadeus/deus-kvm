using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DeusKVM.Companion;

/// <summary>
/// The PC's end of the Mac's pointer stream (NETWORK_POINTER_PLAN.md): connects to the offered
/// listener, runs the nonce handshake under the "input" key purpose, delivers each record from
/// the receiving thread and acknowledges once per read (cumulative, so a 1000 Hz mouse does not
/// cost an acknowledgement per step). Reconnects with backoff while the offer stands; disposing
/// ends it. Deliveries arrive on the thread pool; the caller must be safe for that.
/// </summary>
internal sealed class NetworkInputClient : IDisposable
{
    private static readonly int[] Backoff = [500, 1000, 2000, 5000];
    private readonly FileNetworkOffer offer;
    // Returns whether the PC could inject; a false answer flags the next acknowledgement.
    private readonly Func<InputEvent, bool> deliver;
    private readonly Action<string> status;
    private readonly CancellationTokenSource lifetime = new();
    private TcpClient? client;

    public NetworkInputClient(FileNetworkOffer offer, Func<InputEvent, bool> deliver, Action<string> status)
    {
        this.offer = offer; this.deliver = deliver; this.status = status;
        _ = Task.Run(Run);
    }

    public void Dispose() { lifetime.Cancel(); client?.Dispose(); }

    private async Task Run()
    {
        var attempt = 0;
        while (!lifetime.IsCancellationRequested)
        {
            try { await Session().ConfigureAwait(false); attempt = 0; }
            catch (Exception error) when (error is SocketException or IOException or OperationCanceledException or
                CryptographicException or InvalidDataException or ObjectDisposedException or FormatException)
            { if (!lifetime.IsCancellationRequested) status("closed (" + error.Message + ")"); }
            if (lifetime.IsCancellationRequested) break;
            try { await Task.Delay(Backoff[Math.Min(attempt++, Backoff.Length - 1)], lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task Session()
    {
        foreach (var host in offer.Hosts)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            attempt.CancelAfter(1000);
            var next = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            try { await next.ConnectAsync(IPAddress.Parse(host), offer.Port, attempt.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is SocketException or IOException or OperationCanceledException)
            { next.Dispose(); continue; }
            client = next;
            try { await Stream(next).ConfigureAwait(false); }
            finally { client = null; next.Dispose(); }
            return;
        }
        throw new SocketException(10061); // nothing reachable: WSAECONNREFUSED
    }

    private async Task Stream(TcpClient tcp)
    {
        var wire = tcp.GetStream();
        var clientNonce = RuntimeCompat.RandomBytes(32);
        var serverNonce = new byte[32];
        await wire.WriteAsync(clientNonce, lifetime.Token).ConfigureAwait(false);
        await wire.ReadExactlyAsync(serverNonce, lifetime.Token).ConfigureAwait(false);
        var secret = Convert.FromBase64String(offer.Key);
        FileNetworkCrypto incoming, outgoing;
        try
        {
            incoming = new FileNetworkCrypto(secret, clientNonce, serverNonce, "server", "input");
            outgoing = new FileNetworkCrypto(secret, clientNonce, serverNonce, "client", "input");
        }
        finally { RuntimeCompat.ZeroMemory(secret); }
        using (incoming)
        using (outgoing)
        {
            status("up");
            var buffer = new byte[4096];
            var filled = 0;
            var ack = new byte[InputRecord.SealedAckLength];
            while (true)
            {
                // One read takes whatever arrived: a single step at rest, a burst under load.
                var read = await wire.ReadAsync(buffer, filled, buffer.Length - filled, lifetime.Token).ConfigureAwait(false);
                if (read <= 0) throw new IOException("Stream closed");
                filled += read;
                var offset = 0;
                uint? last = null;
                var accepted = true;
                while (filled - offset >= 1 && filled - offset >= 1 + buffer[offset])
                {
                    int length = buffer[offset];
                    if (length < 16) throw new InvalidDataException("Short record");
                    var plain = incoming.Open(buffer.AsSpan(offset + 1, length).ToArray());
                    if (!InputRecord.TryParse(plain, out var record)) throw new InvalidDataException("Unknown record");
                    accepted &= deliver(record);
                    last = record.Sequence;
                    offset += 1 + length;
                }
                if (offset > 0) { Buffer.BlockCopy(buffer, offset, buffer, 0, filled - offset); filled -= offset; }
                if (filled == buffer.Length) throw new InvalidDataException("Oversized record");
                if (last is not { } sequence) continue;
                outgoing.Seal(InputRecord.Ack(sequence, InputRecord.Clock(), !accepted), ack);
                await wire.WriteAsync(ack, 0, ack.Length, lifetime.Token).ConfigureAwait(false);
            }
        }
    }
}
