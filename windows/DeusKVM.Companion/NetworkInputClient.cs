using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DeusKVM.Companion;

/// <summary>
/// The PC's end of the Mac's pointer stream (NETWORK_POINTER_PLAN.md): connects to the offered
/// listener, runs the nonce handshake under the "input" key purpose, delivers each record and
/// acknowledges it at once. Reconnects with backoff while the offer stands; disposing ends it.
/// Runs on the thread pool; the caller marshals deliveries to its own thread.
/// </summary>
internal sealed class NetworkInputClient : IDisposable
{
    private static readonly int[] Backoff = [500, 1000, 2000, 5000];
    private readonly FileNetworkOffer offer;
    private readonly Action<InputEvent> deliver;
    private readonly Action<string> status;
    private readonly CancellationTokenSource lifetime = new();
    private TcpClient? client;

    public NetworkInputClient(FileNetworkOffer offer, Action<InputEvent> deliver, Action<string> status)
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
            var header = new byte[1];
            var ack = new byte[InputRecord.SealedAckLength];
            while (true)
            {
                await wire.ReadExactlyAsync(header, lifetime.Token).ConfigureAwait(false);
                int length = header[0];
                if (length < 16) throw new InvalidDataException("Short record");
                var sealedRecord = new byte[length];
                await wire.ReadExactlyAsync(sealedRecord, lifetime.Token).ConfigureAwait(false);
                var plain = incoming.Open(sealedRecord);
                if (!InputRecord.TryParse(plain, out var record)) throw new InvalidDataException("Unknown record");
                deliver(record);
                outgoing.Seal(InputRecord.Ack(record.Sequence, InputRecord.Clock()), ack);
                await wire.WriteAsync(ack, lifetime.Token).ConfigureAwait(false);
            }
        }
    }
}
