using System.Net;
using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

// Owns one connection lifetime: the TCP connect and handshake (off the worker's thread), then
// the protocol session. A superseded connect may finish, but its result is disposed here and
// cannot reach a replacement session.
internal sealed class MacSession : IDisposable
{
    private readonly ControlIdentity identity;
    private readonly Func<Guid, bool> trusted;
    private readonly Action<string> detail;
    private ControlConnection? connection;
    private ControlSession? control;
    private bool disposed;
    public ConnectedMac Mac { get; }
    public IPEndPoint[] Endpoints { get; }
    /// <summary>The Mac's id, proven by the handshake.</summary>
    public Guid PeerId { get; private set; }
    public bool Verified { get; private set; }
    public bool Unsupported { get; private set; }
    public bool Attached => control?.Attached == true;
    public bool Ready => control?.Ready == true;
    public bool Available => control?.Available == true;
    public bool SupportsSelection => control?.SupportsSelection == true;
    public bool SupportsTakeover => control?.SupportsTakeover == true;
    public long RequestSerial => control?.RequestSerial ?? 0;
    public uint? ReleaseAcknowledged => control?.ReleaseAcknowledged;
    public void RequestDisable(uint? epoch = null) => control?.RequestDisable(epoch);
    public uint AvailabilityEpoch => control?.AvailabilityEpoch ?? 0;
    public bool Active => control?.Active == true;
    public void SetActive(bool active) => control?.SetActive(active);
    public bool NeedsReconnect => control?.NeedsReconnect == true;
    public int ServiceCount => Verified ? 1 : 0;
    public string? Error { get; private set; }

    public MacSession(ConnectedMac mac, IPEndPoint[] endpoints, ControlIdentity identity, Func<Guid, bool> trusted, Action<string> detail)
    {
        Mac = mac; Endpoints = endpoints; this.identity = identity; this.trusted = trusted; this.detail = detail;
    }

    public async Task Open()
    {
        try
        {
            var opened = await Task.Run(Connect);
            if (disposed) { opened.Dispose(); return; }
            PeerId = opened.PeerId;
            // A Bonjour entry names the Mac it advertises; the handshake must prove it is that Mac.
            if (!trusted(PeerId) || (Guid.TryParse(Mac.Id, out var advertised) && advertised != PeerId))
            {
                opened.Dispose();
                Unsupported = true;
                Error = $"{Mac.Name} is not an allowed Mac (id {PeerId}); add it with Add Mac…";
                return;
            }
            connection = opened;
            Verified = true;
        }
        catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or System.Security.Cryptography.CryptographicException)
        { if (!disposed) Error = error.Message; }
    }

    // Each endpoint in turn (a Mac on Ethernet and Wi-Fi has two); the last failure is reported.
    private ControlConnection Connect()
    {
        IOException? last = null;
        foreach (var endpoint in Endpoints)
        {
            try { return ControlConnection.Connect(endpoint, identity); }
            catch (IOException error) { last = error; }
        }
        throw last ?? new IOException($"No address for {Mac.Name}");
    }

    public Task Attach()
    {
        if (disposed || !Verified || connection is null) return Task.CompletedTask;
        control = new ControlSession(detail);
        control.Attach(connection);
        connection = null; // the session owns it now
        return Task.CompletedTask;
    }

    public void Tick()
    {
        if (!disposed) control?.Tick();
    }

    public void RefreshDesktop()
    {
        if (!disposed) control?.RefreshDesktop();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        control?.Dispose(); control = null;
        connection?.Dispose(); connection = null;
    }
}
