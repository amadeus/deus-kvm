using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

/// <summary>A Mac to try: from Bonjour (with its id) or from mac-hosts.txt (id learned on connect).</summary>
internal sealed record DiscoveredMac(string Name, Guid? Id, IPEndPoint[] Endpoints, string? ManualHost = null);

/// <summary>
/// Finds Macs on the local network: a background thread sends Bonjour queries for
/// <c>_deuskvm._tcp</c> on every IPv4 interface (often at first, then every
/// <see cref="SteadyIntervalMs"/>), completes partial answers with follow-up queries, and adds
/// the hosts listed in <c>%ProgramData%\DeusKVM\mac-hosts.txt</c> (one <c>host[:port]</c> per
/// line, for networks without multicast, such as Tailscale). Each round's result is posted to
/// the creating thread's context.
/// </summary>
internal sealed class NetworkMacDiscovery : IDisposable
{
    public const int SteadyIntervalMs = 10000;
    private readonly SynchronizationContext context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    private readonly Action<IReadOnlyList<DiscoveredMac>> found;
    private readonly Action<string> log;
    private readonly string? hostsFile;
    private readonly CancellationTokenSource stop = new();
    private readonly Thread thread;

    public NetworkMacDiscovery(Action<IReadOnlyList<DiscoveredMac>> found, Action<string> log, string? hostsFile)
    {
        this.found = found; this.log = log; this.hostsFile = hostsFile;
        thread = new Thread(Run) { IsBackground = true, Name = "DeusKVM Mac discovery" };
        thread.Start();
    }

    private void Run()
    {
        var browser = new MdnsBrowser(Mdns.ServiceName);
        var rounds = 0;
        string? lastError = null;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveTimeout = 200 };
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                while (!stop.IsCancellationRequested)
                {
                    Broadcast(socket, [browser.Query()]);
                    Listen(socket, browser, 700);
                    Broadcast(socket, browser.FollowUps(DateTime.UtcNow));
                    Listen(socket, browser, 800);
                    var macs = browser.Services(DateTime.UtcNow)
                        .Select(service => new DiscoveredMac(Clean(service.Instance), service.Id,
                            service.Addresses.Select(address => new IPEndPoint(address, service.Port)).ToArray()))
                        .Concat(ManualHosts()).ToArray();
                    context.Post(_ => { if (!stop.IsCancellationRequested) found(macs); }, null);
                    lastError = null;
                    rounds++;
                    stop.Token.WaitHandle.WaitOne(rounds < 5 ? 1500 : SteadyIntervalMs);
                }
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                if (error.Message != lastError) context.Post(_ => log("Mac discovery: " + error.Message), null);
                lastError = error.Message;
                stop.Token.WaitHandle.WaitOne(5000);
            }
        }
    }

    // One query per IPv4 interface that is up, so a PC on both Ethernet and Wi-Fi asks on both.
    private static void Broadcast(Socket socket, IReadOnlyList<byte[]> queries)
    {
        if (queries.Count == 0) return;
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up && item.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                item.SupportsMulticast)
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Select(item => item.Address).Where(address => address.AddressFamily == AddressFamily.InterNetwork).ToArray();
        foreach (var local in interfaces)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                foreach (var query in queries) socket.SendTo(query, Mdns.Group);
            }
            catch (SocketException) { } // an interface without a route to the group
        }
    }

    private void Listen(Socket socket, MdnsBrowser browser, int milliseconds)
    {
        var buffer = new byte[9000];
        var until = RuntimeCompat.TickCount64 + milliseconds;
        while (RuntimeCompat.TickCount64 < until && !stop.IsCancellationRequested)
        {
            try
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                var count = socket.ReceiveFrom(buffer, ref from);
                browser.Receive(buffer.AsSpan(0, count).ToArray(), DateTime.UtcNow);
            }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.TimedOut or SocketError.ConnectionReset or SocketError.MessageSize) { }
        }
    }

    private IEnumerable<DiscoveredMac> ManualHosts()
    {
        if (hostsFile is null || !File.Exists(hostsFile)) return [];
        var macs = new List<DiscoveredMac>();
        foreach (var raw in File.ReadAllLines(hostsFile).Take(16))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            var (host, port) = SplitHost(line);
            try
            {
                var addresses = IPAddress.TryParse(host, out var literal) ? [literal] :
                    Dns.GetHostAddresses(host).Where(address => address.AddressFamily == AddressFamily.InterNetwork).ToArray();
                if (addresses.Length > 0)
                    macs.Add(new DiscoveredMac(host, null, addresses.Select(address => new IPEndPoint(address, port)).ToArray(), host.ToLowerInvariant()));
            }
            catch (SocketException) { } // not resolvable right now
        }
        return macs;
    }

    internal static (string Host, int Port) SplitHost(string line)
    {
        var colon = line.LastIndexOf(':');
        return colon > 0 && int.TryParse(line.Substring(colon + 1), out var port) && port is > 0 and <= 65535
            ? (line.Substring(0, colon), port) : (line, ControlLink.DefaultPort);
    }

    private static string Clean(string name)
    {
        var text = new string(name.Where(character => !char.IsControl(character)).Take(256).ToArray()).Trim();
        return text.Length == 0 ? "Mac" : text;
    }

    public void Dispose()
    {
        stop.Cancel();
        thread.Join(2000);
        stop.Dispose();
    }
}
