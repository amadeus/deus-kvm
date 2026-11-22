using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace DeusKVM.Companion.Core;

/// <summary>
/// The PC's end of one companion link (<see cref="ControlLink"/>): a blocking TCP connect and
/// handshake, then a reading thread (packets and the close reason go to the callbacks, on that
/// thread) and a writing thread fed by <see cref="Send"/>, which never blocks the caller.
/// Packets are sealed in send order under a lock, so the AES-GCM counters stay in step.
/// </summary>
public sealed class ControlConnection : IDisposable
{
    /// <summary>Bytes waiting to be written before a Mac that stopped reading is dropped.</summary>
    public const int MaximumBacklog = 8 << 20;
    private readonly Socket socket;
    private readonly FileNetworkCrypto outgoing, incoming;
    private readonly BlockingCollection<byte[]> frames = new();
    private readonly object sealGate = new();
    private long backlog;
    private int closed;
    private Action<Protocol.Packet>? received;
    private Action<string>? onClosed;

    public byte[] PeerIdentity { get; }
    public Guid PeerId => ControlLink.PeerId(PeerIdentity);
    public EndPoint? Remote => socket.RemoteEndPoint;
    public long PendingBytes => Interlocked.Read(ref backlog);

    private ControlConnection(Socket socket, byte[] peer, FileNetworkCrypto outgoing, FileNetworkCrypto incoming)
    {
        this.socket = socket; PeerIdentity = peer; this.outgoing = outgoing; this.incoming = incoming;
    }

    /// <summary>Connects and completes the handshake within <paramref name="timeoutMs"/>; throws IOException otherwise.</summary>
    public static ControlConnection Connect(IPEndPoint endpoint, ControlIdentity identity, int timeoutMs = 5000)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            var pending = socket.BeginConnect(endpoint, null, null);
            if (!pending.AsyncWaitHandle.WaitOne(timeoutMs)) throw new IOException($"Connecting to {endpoint} timed out");
            socket.EndConnect(pending);
            socket.ReceiveTimeout = timeoutMs;
            socket.SendTimeout = 10000;
            KeepAlive(socket);
            var nonce = RuntimeCompat.RandomBytes(32);
            SendAll(socket, ControlLink.Hello(identity.Public, nonce));
            var hello = ReceiveExactly(socket, ControlLink.HelloLength) ?? throw new IOException("The Mac closed the link during the handshake");
            if (!ControlLink.TryReadHello(hello, out var peer, out var serverNonce)) throw new IOException("Not a DeusKVM Mac (bad hello)");
            var secret = identity.Agree(peer);
            var (send, receive) = ControlLink.ClientKeys(secret, nonce, serverNonce);
            RuntimeCompat.ZeroMemory(secret);
            socket.ReceiveTimeout = 0; // frames may be minutes apart; keepalive and the heartbeat watch the Mac
            return new ControlConnection(socket, peer, send, receive);
        }
        catch (Exception error) when (error is SocketException or System.Security.Cryptography.CryptographicException or ObjectDisposedException)
        {
            socket.Dispose();
            throw new IOException(error.Message, error);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Starts the reading and writing threads.</summary>
    public void Start(Action<Protocol.Packet> packet, Action<string> close)
    {
        received = packet; onClosed = close;
        new Thread(ReadLoop) { IsBackground = true, Name = "DeusKVM control link read" }.Start();
        new Thread(WriteLoop) { IsBackground = true, Name = "DeusKVM control link write" }.Start();
    }

    /// <summary>Queues one packet; false once the link is closing or its backlog is full (the link then closes).</summary>
    public bool Send(byte stream, byte type, byte[] payload)
    {
        if (Volatile.Read(ref closed) != 0) return false;
        byte[] frame;
        lock (sealGate)
        {
            if (Volatile.Read(ref closed) != 0) return false;
            frame = ControlLink.Frame(outgoing, stream, type, payload);
            if (Interlocked.Add(ref backlog, frame.Length) > MaximumBacklog) { Close("Mac link backlog full"); return false; }
            try { frames.Add(frame); }
            catch (InvalidOperationException) { return false; }
        }
        return true;
    }

    private void ReadLoop()
    {
        var reason = "Mac closed the link";
        try
        {
            var prefix = new byte[4];
            while (Volatile.Read(ref closed) == 0)
            {
                if (!ReceiveInto(socket, prefix)) break;
                var length = ControlLink.SealedLength(prefix);
                if (length < 0) { reason = "Invalid frame length from the Mac"; break; }
                var sealedBytes = new byte[length];
                if (!ReceiveInto(socket, sealedBytes)) break;
                received?.Invoke(ControlLink.Open(incoming, sealedBytes));
            }
        }
        catch (Exception error) when (error is SocketException or IOException or ObjectDisposedException or InvalidDataException or System.Security.Cryptography.CryptographicException)
        {
            reason = "Mac link: " + error.Message;
        }
        Close(reason);
    }

    private void WriteLoop()
    {
        try
        {
            foreach (var frame in frames.GetConsumingEnumerable())
            {
                SendAll(socket, frame);
                Interlocked.Add(ref backlog, -frame.Length);
            }
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException or IOException)
        {
            Close("Mac link write: " + error.Message);
        }
    }

    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        frames.CompleteAdding();
        try { socket.Shutdown(SocketShutdown.Both); } catch (Exception error) when (error is SocketException or ObjectDisposedException) { }
        socket.Close();
        onClosed?.Invoke(reason);
    }

    public void Dispose()
    {
        onClosed = null; received = null;
        Close("disposed");
        outgoing.Dispose(); incoming.Dispose();
    }

    // Windows: probe after 10 s idle, every 5 s, so a vanished Mac is noticed in about 20 s.
    private static void KeepAlive(Socket socket)
    {
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
#if !NETFRAMEWORK
        if (!OperatingSystem.IsWindows()) return; // the tests' loopback on other systems
#endif
        try
        {
            var values = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(values, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(values.AsSpan(4), 10000);
            BinaryPrimitives.WriteUInt32LittleEndian(values.AsSpan(8), 5000);
            socket.IOControl(IOControlCode.KeepAliveValues, values, null);
        }
        catch (Exception error) when (error is PlatformNotSupportedException or SocketException or NotSupportedException) { }
    }

    private static void SendAll(Socket socket, byte[] data)
    {
        var sent = 0;
        while (sent < data.Length) sent += socket.Send(data, sent, data.Length - sent, SocketFlags.None);
    }

    private static byte[]? ReceiveExactly(Socket socket, int count)
    {
        var buffer = new byte[count];
        return ReceiveInto(socket, buffer) ? buffer : null;
    }

    private static bool ReceiveInto(Socket socket, byte[] buffer)
    {
        var got = 0;
        while (got < buffer.Length)
        {
            var count = socket.Receive(buffer, got, buffer.Length - got, SocketFlags.None);
            if (count == 0) return false;
            got += count;
        }
        return true;
    }
}
