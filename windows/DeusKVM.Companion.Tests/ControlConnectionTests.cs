using System.Net;
using System.Net.Sockets;
using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class ControlConnectionTests
{
    // A minimal Mac: accepts, answers the hello, then exchanges frames like ControlServer.swift.
    private sealed class FakeMac : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public ControlIdentity Identity { get; } = ControlIdentity.Create();
        public IPEndPoint Endpoint => (IPEndPoint)listener.LocalEndpoint;
        public Socket? Peer { get; private set; }
        public FileNetworkCrypto? Outgoing { get; private set; }
        public FileNetworkCrypto? Incoming { get; private set; }
        public byte[]? PcIdentity { get; private set; }

        public FakeMac() => listener.Start();

        public Task Serve() => Task.Run(() =>
        {
            Peer = listener.AcceptSocket();
            var hello = Read(ControlLink.HelloLength);
            Assert.True(ControlLink.TryReadHello(hello, out var pc, out var clientNonce));
            PcIdentity = pc;
            var nonce = RuntimeCompat.RandomBytes(32);
            Peer.Send(ControlLink.Hello(Identity.Public, nonce));
            var secret = Identity.Agree(pc);
            Outgoing = new FileNetworkCrypto(secret, clientNonce, nonce, "server", "control");
            Incoming = new FileNetworkCrypto(secret, clientNonce, nonce, "client", "control");
        });

        public byte[] Read(int count)
        {
            var buffer = new byte[count];
            var got = 0;
            while (got < count) { var n = Peer!.Receive(buffer, got, count - got, SocketFlags.None); Assert.True(n > 0); got += n; }
            return buffer;
        }

        public Protocol.Packet ReadFrame()
        {
            var length = ControlLink.SealedLength(Read(4));
            return ControlLink.Open(Incoming!, Read(length));
        }

        public void Dispose() { Peer?.Dispose(); listener.Stop(); Outgoing?.Dispose(); Incoming?.Dispose(); Identity.Dispose(); }
    }

    [Fact]
    public async Task TheLinkCarriesPacketsBothWaysAndReportsTheClose()
    {
        using var mac = new FakeMac();
        using var pc = ControlIdentity.Create();
        var served = mac.Serve();
        using var link = ControlConnection.Connect(mac.Endpoint, pc);
        await served;
        Assert.Equal(mac.Identity.Public, link.PeerIdentity);
        Assert.Equal(pc.Public, mac.PcIdentity);
        var received = new TaskCompletionSource<Protocol.Packet>();
        var closed = new TaskCompletionSource<string>();
        link.Start(packet => received.TrySetResult(packet), reason => closed.TrySetResult(reason));

        mac.Peer!.Send(ControlLink.Frame(mac.Outgoing!, 1, (byte)Protocol.Message.Hello, "{\"v\":1}"u8.ToArray()));
        var packet = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((byte)Protocol.Message.Hello, packet.Type);
        Assert.Equal(1, packet.Stream);

        Assert.True(link.Send(0, (byte)Protocol.Message.Ping, [1, 2, 3, 4]));
        Assert.True(link.Send(1, (byte)Protocol.Message.Screens, new byte[40000]));
        var ping = mac.ReadFrame();
        Assert.Equal((byte)Protocol.Message.Ping, ping.Type);
        Assert.Equal(40000, mac.ReadFrame().Payload.Length); // order is kept

        mac.Peer.Shutdown(SocketShutdown.Both);
        Assert.Contains("closed", await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(link.Send(0, (byte)Protocol.Message.Ping, [0, 0, 0, 0]));
    }

    [Fact]
    public async Task SomethingThatIsNotAMacFailsTheHandshake()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var served = Task.Run(() =>
        {
            using var peer = listener.AcceptSocket();
            var buffer = new byte[ControlLink.HelloLength];
            peer.Receive(buffer);
            peer.Send(new byte[ControlLink.HelloLength]); // not a hello
        });
        using var pc = ControlIdentity.Create();
        Assert.Throws<IOException>(() => ControlConnection.Connect((IPEndPoint)listener.LocalEndpoint, pc));
        await served;
        listener.Stop();
    }
}
