using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DeusKVM.Companion;

/// <summary>
/// The PC's end of the Mac's pointer stream: connects to the offered
/// listener, runs the nonce handshake under the "input" key purpose, and delivers each record.
/// It runs on a dedicated thread with blocking sockets rather than the thread pool, so a pointer
/// step is not delayed by a scheduler wake-up. Acknowledgements are cumulative and paced by
/// <see cref="AckPacer"/>: routine ones at most every 16 ms (each wakes a thread on the Mac), and
/// at once for a ping, a change in whether the PC can inject, or a new edge-return request. One
/// sent at its read goes out before injecting, so the round trip the Mac measures excludes the
/// injection work (which periodically probes the input desktop); later ones are flagged deferred.
/// Reconnects with backoff while the offer stands; disposing ends it.
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
    private long leave, leaveUntil, leaveSerial;
    private static readonly long LeaveTicks = System.Diagnostics.Stopwatch.Frequency * 3 / 10;

    /// <summary>The stream is up: acknowledgements are flowing to the Mac.</summary>
    public bool Up => up;

    /// <summary>
    /// Carries an edge-return request on the acknowledgements for the next 300 ms. A new request
    /// is acknowledged right after the injection that raised it, and routine acknowledgements
    /// repeat it. False when the stream is down.
    /// </summary>
    public bool Leave(uint flags)
    {
        if (!up) return false;
        Interlocked.Exchange(ref leave, flags);
        Interlocked.Exchange(ref leaveUntil, System.Diagnostics.Stopwatch.GetTimestamp() + LeaveTicks);
        Interlocked.Increment(ref leaveSerial);
        return true;
    }

    /// <summary>The request in force: its flags and serial, or zeros.</summary>
    private (uint Flags, long Serial) PendingLeave() =>
        System.Diagnostics.Stopwatch.GetTimestamp() < Interlocked.Read(ref leaveUntil)
            ? ((uint)Interlocked.Read(ref leave), Interlocked.Read(ref leaveSerial)) : (0, 0);

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
            var pacer = new AckPacer(System.Diagnostics.Stopwatch.Frequency);
            void Acknowledge(uint sequence, uint received, bool deferred)
            {
                var request = PendingLeave();
                outgoing.Seal(InputRecord.Ack(sequence, received, !canInject, request.Flags, deferred), ack);
                wire.Send(ack);
                pacer.Sent(sequence, !canInject, request.Serial, System.Diagnostics.Stopwatch.GetTimestamp());
            }
            while (!lifetime.IsCancellationRequested)
            {
                // A paced acknowledgement waits only until its deadline for the next read.
                if (pacer.Deadline is { } deadline && pacer.Pending is { } owed)
                {
                    var wait = (deadline - System.Diagnostics.Stopwatch.GetTimestamp()) * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
                    if (wait <= 0 || !wire.Poll((int)Math.Min(wait, int.MaxValue), SelectMode.SelectRead))
                    {
                        Acknowledge(owed, pacer.PendingReceived, deferred: true);
                        continue;
                    }
                }
                var read = wire.Receive(buffer, filled, buffer.Length - filled, SocketFlags.None);
                if (read <= 0) throw new IOException("Stream closed");
                filled += read;
                var offset = 0;
                uint? last = null;
                var ping = false;
                var records = new List<InputEvent>();
                while (filled - offset >= 1 && filled - offset >= 1 + buffer[offset])
                {
                    int length = buffer[offset];
                    if (length < 16) throw new InvalidDataException("Short record");
                    var plain = incoming.Open(buffer.AsSpan(offset + 1, length).ToArray());
                    if (!InputRecord.TryParse(plain, out var record)) throw new InvalidDataException("Unknown record");
                    records.Add(record);
                    last = record.Sequence;
                    ping |= record.Type == InputRecord.Ping;
                    offset += 1 + length;
                }
                if (offset > 0) { Buffer.BlockCopy(buffer, offset, buffer, 0, filled - offset); filled -= offset; }
                if (filled == buffer.Length) throw new InvalidDataException("Oversized record");
                if (last is not { } sequence) continue;
                // Acknowledge first when due, so the round trip the Mac measures is the wire, not
                // the injection that follows; the blocked flag reports the previous batch's outcome.
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (pacer.Read(sequence, InputRecord.Clock(), ping, !canInject, PendingLeave().Serial, now))
                    Acknowledge(sequence, pacer.PendingReceived, deferred: false);
                var ok = true;
                foreach (var record in records) ok &= deliver(record);
                canInject = ok;
                // An edge-return request this batch raised goes to the Mac now, not a step later.
                if (pacer.Fresh(PendingLeave().Serial))
                    Acknowledge(pacer.Pending ?? pacer.LastAcked ?? sequence, pacer.Pending is null ? InputRecord.Clock() : pacer.PendingReceived, deferred: true);
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
