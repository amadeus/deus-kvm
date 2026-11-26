using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

/// <summary>
/// One desktop agent and one clipboard helper for the service, shared by all connected Macs.
/// Per-Mac channels carry source identity; lifecycle messages are broadcast, input and clipboard
/// replies are routed to their source. All methods run on the link worker's STA.
/// </summary>
internal sealed class DesktopRouter : IDisposable
{
    private readonly SynchronizationContext context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    private readonly Dictionary<long, Peer> peers = [];
    private readonly DesktopHost agent;
    private readonly DesktopHost clipboard;
    private DesktopMessage? screens, state, clipboardAvailability;
    private long nextStream;
    private bool disposed;
    private readonly DesktopOwnershipRoute route = new();
    public long? Owner => route.Owner;
    public long Ownership => route.Generation;

    public DesktopRouter()
    {
        agent = new DesktopHost(DesktopRole.Agent, message => context.Post(_ => AgentEvent(message), null));
        clipboard = new DesktopHost(DesktopRole.Clipboard, message => context.Post(_ => ClipboardEvent(message), null));
    }

    public Peer Register(long source, Action<DesktopMessage> agentEvent, Action<DesktopMessage> clipboardEvent)
    {
        if (peers.ContainsKey(source)) throw new InvalidOperationException("Duplicate desktop source");
        var peer = new Peer(this, source, checked(++nextStream), agentEvent, clipboardEvent);
        peers.Add(source, peer);
        return peer;
    }

    public void Refresh()
    {
        if (disposed || !peers.Values.Any(peer => peer.Enabled)) return;
        agent.Refresh(); clipboard.Refresh();
    }

    private bool Current(Peer peer) => !disposed && peers.TryGetValue(peer.Source, out var current) && ReferenceEquals(current, peer);

    private void AgentEvent(DesktopMessage message)
    {
        if (disposed || message.HostGeneration != agent.Generation) return;
        if (message.Kind is "connected" or "disconnected" && route.RestartAgent(message.HostGeneration))
            PublishOwner();
        if (message.Kind == "connected")
        {
            agent.Send(new DesktopMessage("generation", Ownership: Ownership));
            foreach (var peer in peers.Values) if (peer.Enabled)
                agent.Send(new DesktopMessage("source", Source: peer.Source, Stream: peer.Stream));
        }
        if (message.Kind == "screens") screens = message;
        if (message.Kind == "state") state = message;
        if (message.Kind == "disconnected") { screens = state = null; }
        if (message.Kind == "ownership")
        {
            if (route.TrySelect(message.Source, message.Stream, message.Ownership, message.HostGeneration,
                (source, stream) => peers.TryGetValue(source, out var peer) && peer.Enabled && peer.Stream == stream))
                PublishOwner();
            return;
        }
        if (message.Source != 0)
        {
            if (peers.TryGetValue(message.Source, out var peer) && peer.Enabled && message.Stream == peer.Stream)
                peer.AgentEvent(message);
            return;
        }
        foreach (var peer in peers.Values.ToArray()) if (peer.Enabled) peer.AgentEvent(message);
    }

    private void PublishOwner()
    {
        var source = Owner ?? 0;
        var generation = Ownership;
        // Revoke the previous clipboard lease before asking any endpoint to publish a new one.
        clipboard.Send(new DesktopMessage("clipboard-select", Source: source, Ownership: generation));
        if (source != 0) clipboard.Send(new DesktopMessage("cursor-reveal", Source: source, Ownership: generation));
        foreach (var peer in peers.Values.ToArray())
            if (peer.Enabled) peer.AgentEvent(new DesktopMessage("ownership", Source: source, Ownership: generation, Ok: peer.Source == source));
        // No signed-in clipboard exists on secure/login desktops. Input can proceed there.
        if (state?.Desktop == 1)
            agent.Send(new DesktopMessage("clipboard-ready", Source: source, Ownership: generation, Ok: true, SecureReady: true));
    }

    private void ClipboardEvent(DesktopMessage message)
    {
        if (disposed || message.HostGeneration != clipboard.Generation) return;
        if (message.Kind == "disconnected")
        {
            clipboardAvailability = null;
            agent.Send(new DesktopMessage("clipboard-lost"));
        }
        if (message.Kind == "clipboard-availability") clipboardAvailability = message;
        if (message.Kind == "clipboard-ready")
        {
            if (route.MatchesClipboard(message.Source, message.Ownership, message.Epoch) && peers.TryGetValue(message.Source, out var readyPeer))
                readyPeer.ClipboardEvent(message);
            return;
        }
        if (message.Kind is "connected" or "disconnected" or "clipboard-availability")
        {
            foreach (var peer in peers.Values.ToArray()) if (peer.Enabled) peer.ClipboardEvent(message);
            return;
        }
        if (Owner is { } owner && peers.TryGetValue(owner, out var selected) && selected.Enabled &&
            message.Source == owner && message.Ownership == Ownership)
            selected.ClipboardEvent(message);
    }

    public sealed class Peer : IDisposable
    {
        private readonly DesktopRouter router;
        internal Action<DesktopMessage> AgentEvent { get; }
        internal Action<DesktopMessage> ClipboardEvent { get; }
        public long Source { get; }
        public long Stream { get; }
        public bool Enabled { get; private set; }
        public Channel Agent { get; }
        public Channel Clipboard { get; }
        internal Peer(DesktopRouter router, long source, long stream, Action<DesktopMessage> agentEvent, Action<DesktopMessage> clipboardEvent)
        {
            this.router = router; Source = source; Stream = stream; AgentEvent = agentEvent; ClipboardEvent = clipboardEvent;
            Agent = new Channel(this, false); Clipboard = new Channel(this, true);
        }
        public void SetEnabled(bool enabled)
        {
            if (!router.Current(this) || Enabled == enabled) return;
            Enabled = enabled;
            if (!enabled) { router.agent.Send(new DesktopMessage("remove", Source: Source, Stream: Stream)); return; }
            router.Refresh();
            router.agent.Send(new DesktopMessage("source", Source: Source, Stream: Stream));
            if (router.screens is { } screens) AgentEvent(screens);
            if (router.state is { } state) AgentEvent(state);
            if (router.clipboardAvailability is { } availability) ClipboardEvent(availability);
        }
        public void Dispose()
        {
            if (!router.Current(this)) return;
            SetEnabled(false); router.peers.Remove(Source);
        }
        public sealed class Channel(Peer peer, bool isClipboard)
        {
            private DesktopRouter Router => peer.router;
            private DesktopHost Host => isClipboard ? Router.clipboard : Router.agent;
            public bool Connected => peer.Enabled && Host.Connected;
            public string Detail => Host.Detail;
            public void Refresh() => Router.Refresh();
            public void Send(DesktopMessage message)
            {
                if (!peer.Enabled || !Router.Current(peer)) return;
                if (isClipboard && Router.Owner != peer.Source) return;
                if (isClipboard && message.Kind == "clipboard-select") Router.route.ExpectClipboard(peer.Source, Router.Ownership, message.Epoch);
                Host.Send(message with { Source = peer.Source, Stream = peer.Stream, Ownership = Router.Ownership });
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        peers.Clear(); agent.Dispose(); clipboard.Dispose();
    }
}
