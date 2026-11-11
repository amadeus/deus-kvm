using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DeusKVM.Companion;

/// <summary>
/// The PC's end of the Mac's pointer stream (NETWORK_POINTER_PLAN.md): connects to the offered
/// listener, runs the nonce handshake under the "input" key purpose, and delivers each record.
/// It runs on a dedicated thread with blocking sockets rather than the thread pool, so a pointer
/// step is not delayed by a scheduler wake-up, and it acknowledges each read before injecting, so
/// the round trip the Mac measures excludes the injection work (which periodically probes the
/// input desktop). The acknowledgement is cumulative, so a 1000 Hz mouse costs one ack per read,
/// not one per step. Reconnects with backoff while the offer stands; disposing ends it.
/// Deliveries arrive on this thread; the caller must be safe for that.
/// </summary>
internal sealed class NetworkInputClient : IDisposable
{
    private static readonly int[] Backoff = [500, 1000, 2000, 5000];
    private readonly FileNetworkOffer offer;
    // Returns whether the PC could inject; a false answer flags the next acknowledgement.
    private readonly Func<InputEvent, bool> deliver;
    private readonly Action<string> status;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Thread thread;
    private Socket? socket;
    private volatile bool up;
    // An edge-return request for the Mac (InputRecord.LeaveFlags) and until when (Stopwatch ticks)
    // the acknowledgements keep carrying it; set from the desktop thread.
    private long leave, leaveUntil;
    private static readonly long LeaveTicks = System.Diagnostics.Stopwatch.Frequency * 3 / 10;

    /// <summary>The stream is up: acknowledgements are flowing to the Mac.</summary>
    public bool Up => up;

    /// <summary>
    /// Carries an edge-return request on the acknowledgements for the next 300 ms (they go out
    /// with every read, and the cursor is being pushed at the edge, so the next one is a pointer
    /// step away). False when the stream is down.
    /// </summary>
    public bool Leave(uint flags)
    {
        if (!up) return false;
        Interlocked.Exchange(ref leave, flags);
        Interlocked.Exchange(ref leaveUntil, System.Diagnostics.Stopwatch.GetTimestamp() + LeaveTicks);
        return true;
    }

    private uint PendingLeave() =>
        System.Diagnostics.Stopwatch.GetTimestamp() < Interlocked.Read(ref leaveUntil) ? (uint)Interlocked.Read(ref leave) : 0;

    public NetworkInputClient(FileNetworkOffer offer, Func<InputEvent, bool> deliver, Action<string> status)
    {
        this.offer = offer; this.deliver = deliver; this.status = status;
        thread = new Thread(Run) { IsBackground = true, Name = "DeusKVM network input", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    public void Dispose()
    {
        lifetime.Cancel();
        try { socket?.Close(); } catch (ObjectDisposedException) { }
    }

    private void Run()
    {
        var attempt = 0;
        while (!lifetime.IsCancellationRequested)
        {
            try { Session(); attempt = 0; }
            catch (Exception error) when (error is SocketException or IOException or OperationCanceledException or
                CryptographicException or InvalidDataException or ObjectDisposedException or FormatException)
            { if (!lifetime.IsCancellationRequested) status("closed (" + error.Message + ")"); }
            if (lifetime.IsCancellationRequested) break;
            if (lifetime.Token.WaitHandle.WaitOne(Backoff[Math.Min(attempt++, Backoff.Length - 1)])) break;
        }
    }

    private void Session()
    {
        Socket? connected = null;
        foreach (var host in offer.Hosts)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            var next = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                var result = next.BeginConnect(IPAddress.Parse(host), offer.Port, null, null);
                if (!result.AsyncWaitHandle.WaitOne(1000) || !next.Connected) { next.Close(); continue; }
                next.EndConnect(result);
                connected = next;
                break;
            }
            catch (SocketException) { next.Close(); }
        }
        if (connected is null) throw new SocketException((int)SocketError.ConnectionRefused);
        socket = connected;
        try { Stream(connected); }
        finally { up = false; try { connected.Close(); } catch (ObjectDisposedException) { } socket = null; }
    }

    private void Stream(Socket wire)
    {
        var clientNonce = RuntimeCompat.RandomBytes(32);
        var serverNonce = new byte[32];
        wire.Send(clientNonce);
        ReceiveExactly(wire, serverNonce, 32);
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
            up = true;
            status("up");
            var buffer = new byte[4096];
            var filled = 0;
            var ack = new byte[InputRecord.SealedAckLength];
            var canInject = true; // last injection outcome; the ack reports it without waiting for this batch
            while (!lifetime.IsCancellationRequested)
            {
                var read = wire.Receive(buffer, filled, buffer.Length - filled, SocketFlags.None);
                if (read <= 0) throw new IOException("Stream closed");
                filled += read;
                var offset = 0;
                uint? last = null;
                var records = new List<InputEvent>();
                while (filled - offset >= 1 && filled - offset >= 1 + buffer[offset])
                {
                    int length = buffer[offset];
                    if (length < 16) throw new InvalidDataException("Short record");
                    var plain = incoming.Open(buffer.AsSpan(offset + 1, length).ToArray());
                    if (!InputRecord.TryParse(plain, out var record)) throw new InvalidDataException("Unknown record");
                    records.Add(record);
                    last = record.Sequence;
                    offset += 1 + length;
                }
                if (offset > 0) { Buffer.BlockCopy(buffer, offset, buffer, 0, filled - offset); filled -= offset; }
                if (filled == buffer.Length) throw new InvalidDataException("Oversized record");
                if (last is not { } sequence) continue;
                // Acknowledge first, so the round trip the Mac measures is the wire, not the
                // injection that follows; the blocked flag reports the previous batch's outcome.
                outgoing.Seal(InputRecord.Ack(sequence, InputRecord.Clock(), !canInject, PendingLeave()), ack);
                wire.Send(ack);
                var ok = true;
                foreach (var record in records) ok &= deliver(record);
                canInject = ok;
            }
        }
    }

    private void ReceiveExactly(Socket wire, byte[] into, int count)
    {
        var got = 0;
        while (got < count)
        {
            var read = wire.Receive(into, got, count - got, SocketFlags.None);
            if (read <= 0) throw new IOException("Handshake closed");
            got += read;
        }
    }
}
