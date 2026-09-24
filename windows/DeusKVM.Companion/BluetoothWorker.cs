using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

internal sealed class BluetoothWorker : ApplicationContext
{
    private readonly CancellationTokenSource shutdown = new();
    private readonly StreamWriter output = new(Console.OpenStandardOutput()) { AutoFlush = true };
    private readonly System.Windows.Forms.Timer heartbeat = new() { Interval = 10000 };
    private readonly Dictionary<long, Attempt> attempts = [];
    private readonly Dictionary<long, int> failures = [];
    private readonly List<(Task Work, long RetiredAt)> retired = [];
    private WorkerStatus status = new("Starting", "Watching paired Bluetooth devices", DateTimeOffset.UtcNow);
    private MacPriority priority = new();
    private readonly ControlArbiter arbiter = new();
    private AutomaticMacSettings saved = new(null, []);
    private CompanionSettings? pairingHint;
    private PairedMacDiscovery? discovery;
    private KnownMacRecovery? recovery;
    private bool started;
    private long lastTick;
    public int ExitCode { get; private set; }

    private sealed class Attempt(MacSession link, Task work)
    {
        public MacSession Link { get; } = link;
        public Task Work { get; set; } = work;
        public long Started { get; set; } = RuntimeCompat.TickCount64;
        public long RetryAt { get; set; }
        public int Failures { get; set; }
        public bool Attaching { get; set; }
        public bool Remembered { get; set; }
        public uint? AvailabilityEpoch { get; set; }
        public long ProcessedRequest { get; set; }
    }

    public BluetoothWorker()
    {
        heartbeat.Tick += (_, _) => Emit(status);
        heartbeat.Start(); Application.Idle += Start;
    }

    private async void Start(object? sender, EventArgs args)
    {
        if (started) return;
        started = true; Application.Idle -= Start;
        try { await RunAsync(shutdown.Token); }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            Emit(status with { State = "Error", Detail = $"{error.Message} (HRESULT 0x{error.HResult:X8})." });
            ExitCode = 1;
        }
        finally { ExitThread(); }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        pairingHint = JsonFiles.Read<CompanionSettings>(Paths.Settings);
        saved = JsonFiles.Read<AutomaticMacSettings>(Paths.AutomaticMacs) ?? AutomaticMacSettings.Migrate(pairingHint);
        saved.Validate();
        priority = new MacPriority(saved.LastActiveId, requireAvailability: true);
        discovery = new PairedMacDiscovery(priority, message => Emit(status with { DiscoveryEvent = message }));
        recovery = new KnownMacRecovery(message => Emit(status with { DiscoveryEvent = message }));
        recovery.Refresh(saved.Macs);
        while (!stop.IsCancellationRequested)
        {
            if (discovery.Error is { } error) throw new IOException(error);
            RefreshPairingHint();
            foreach (var connection in new[] { arbiter.Owner, arbiter.Requested }.OfType<long>())
                if (!priority.Connections.Any(mac => mac.Connection == connection)) arbiter.Disconnected(connection);
            foreach (var pair in attempts.ToArray())
            {
                if (priority.IsCurrent(pair.Value.Link.Mac)) continue;
                arbiter.Disconnected(pair.Key);
                Retire(pair.Value); attempts.Remove(pair.Key); failures.Remove(pair.Key);
            }
            retired.RemoveAll(item => item.Work.IsCompleted);
            if (priority.Active is null && retired.Any(item => RuntimeCompat.TickCount64 - item.RetiredAt > 60000))
                throw new TimeoutException("A disconnected device's Bluetooth operation stalled; recycling the worker.");
            foreach (var attempt in attempts.Values.ToArray()) AdvanceAttempt(attempt);

            // Serialize GATT discovery. The message loop and disconnect handling
            // remain responsive while the WinRT operation runs.
            if (priority.Enumerated && retired.Count < 4 && !attempts.Values.Any(item => !item.Work.IsCompleted))
            {
                var candidate = priority.DiscoveryCandidates.FirstOrDefault(mac => !attempts.ContainsKey(mac.Connection));
                if (candidate is not null) Open(candidate);
            }
            if (RuntimeCompat.TickCount64 - lastTick >= 3000)
            {
                lastTick = RuntimeCompat.TickCount64;
                recovery.Refresh(saved.Macs);
                foreach (var attempt in attempts.Values.Where(item => item.Work.IsCompleted && item.Attaching && item.RetryAt == 0))
                    attempt.Link.Tick();
            }
            Elect();
            RefreshStatus();
            await Task.Delay(250, stop);
        }
    }

    private void Open(ConnectedMac mac)
    {
        var link = new MacSession(mac,
            connected => { if (priority.IsCurrent(mac)) discovery?.ConnectionChanged(mac.Id, connected); },
            detail => { if (priority.Active?.Connection == mac.Connection) Emit(status with { Detail = detail }); });
        attempts[mac.Connection] = new Attempt(link, link.Open()) { Failures = failures.GetValueOrDefault(mac.Connection) };
    }

    private void AdvanceAttempt(Attempt attempt)
    {
        var link = attempt.Link;
        if (!attempt.Work.IsCompleted)
        {
            if (RuntimeCompat.TickCount64 - attempt.Started > 45000)
            {
                if (priority.Active?.Connection == link.Mac.Connection)
                    throw new TimeoutException($"Bluetooth operation stalled for {link.Mac.Name}; recycling the worker.");
                // An unrelated device's stalled probe must not interrupt a working
                // Mac. Quarantine it until disconnect; recycle later if still hung.
                priority.Reject(link.Mac); Retire(attempt); attempts.Remove(link.Mac.Connection);
                Emit(status with { DiscoveryEvent = $"Discovery timed out for {link.Mac.Name}; skipping this connection without replacing the active Mac." });
            }
            return;
        }
        // Open/Attach contain native exceptions; observe unexpected task faults too.
        attempt.Work.GetAwaiter().GetResult();
        if (attempt.RetryAt != 0)
        {
            if (RuntimeCompat.TickCount64 < attempt.RetryAt) return;
            priority.Reconsider(link.Mac);
            Retire(attempt); attempts.Remove(link.Mac.Connection);
            // Defer opening to the normal serialized discovery pass.
            return;
        }
        if (link.Error is not null || link.NeedsReconnect)
        {
            attempt.RetryAt = RuntimeCompat.TickCount64 + (long)ServicePolicy.RetryDelay(++attempt.Failures).TotalMilliseconds;
            failures[link.Mac.Connection] = attempt.Failures;
            priority.SetAvailable(link.Mac, false);
            if (priority.Active?.Connection != link.Mac.Connection) priority.Reject(link.Mac);
            link.Dispose();
            Emit(status with { DiscoveryEvent = $"Retrying {link.Mac.Name}: {link.Error ?? "GATT services or companion connection changed"}" });
            return;
        }
        if (link.Unsupported)
        {
            if (priority.Active?.Connection == link.Mac.Connection)
            {
                attempt.RetryAt = RuntimeCompat.TickCount64 + 5000;
                link.Dispose();
            }
            else priority.Reject(link.Mac);
            return;
        }
        if (!link.Verified) return;
        if (attempt.Attaching) failures.Remove(link.Mac.Connection);
        if (!attempt.Remembered)
        {
            Remember(link.Mac, false); attempt.Remembered = true;
            Emit(status with { DiscoveryEvent = $"Verified DeusKVM Mac: {link.Mac.Name}; services={link.ServiceCount}" });
        }
        if (!attempt.Attaching)
        {
            attempt.Attaching = true; attempt.Started = RuntimeCompat.TickCount64; attempt.Work = link.Attach();
        }
    }

    private void Elect()
    {
        foreach (var attempt in attempts.Values)
        {
            var link = attempt.Link;
            if (link.Ready) attempt.AvailabilityEpoch = link.AvailabilityEpoch;
            priority.SetAvailable(link.Mac, link.Available && link.SupportsTakeover && attempt.RetryAt == 0);
        }
        // Availability flag 2 carries Enable + Request atomically; it cannot be
        // mistaken for an unsolicited new connection while a later frame waits.
        foreach (var attempt in attempts.Values.OrderBy(item => item.Link.RequestSerial))
        {
            if (attempt.Link.RequestSerial <= attempt.ProcessedRequest) continue;
            attempt.ProcessedRequest = attempt.Link.RequestSerial;
            if (attempt.Link.Available) arbiter.Request(attempt.Link.Mac.Connection);
        }
        if (arbiter.Requested is { } requested && attempts.TryGetValue(requested, out var requester) &&
            requester.Link.Ready && !requester.Link.Available) arbiter.CancelRequest();

        if (arbiter.Owner is { } owner && attempts.TryGetValue(owner, out var previous))
        {
            if (arbiter.Requested is not null || !previous.Link.Available)
                arbiter.BeginRelease(previous.AvailabilityEpoch ?? 0);
            if (arbiter.Releasing is not null)
            {
                previous.Link.RequestDisable(arbiter.ReleaseEpoch);
                if (previous.Link.ReleaseAcknowledged is { } epoch && arbiter.Acknowledge(owner, epoch))
                    priority.SetAvailable(previous.Link.Mac, false);
            }
            else if (!previous.Link.Active)
                previous.Link.SetActive(true); // same physical owner after GATT recovery
        }

        if (arbiter.Owner is null && arbiter.Releasing is null)
        {
            var next = arbiter.Requested is { } target
                ? priority.Candidates.FirstOrDefault(mac => mac.Connection == target) : priority.Next;
            if (next is not null && attempts.TryGetValue(next.Connection, out var winner) && arbiter.Grant(next.Connection))
            {
                priority.SelectRequested(next);
                winner.Link.SetActive(true);
                Remember(next, true);
                Emit(status with { State = "Connected", DeviceName = next.Name, DeviceId = next.Id,
                    Detail = "Active Mac; preparing edge and clipboard session", LastDiscovery = DateTimeOffset.UtcNow,
                    ServiceCount = winner.Link.ServiceCount, HidServiceFound = true,
                    DiscoveryEvent = $"Active Mac: {next.Name}; connection={next.Connection}" });
            }
        }
        // A connected non-owner is disabled on the Mac itself. Explicit Enable
        // is the only way it can replace an existing owner, with Bluetooth intact.
        if (arbiter.Owner is not null || arbiter.Requested is not null)
            foreach (var attempt in attempts.Values)
                if (attempt.Link.Mac.Connection != arbiter.Owner && attempt.Link.Mac.Connection != arbiter.Requested &&
                    attempt.Link.Ready && attempt.Link.Available && attempt.Link.SupportsTakeover)
                    attempt.Link.RequestDisable();
    }

    private void RefreshPairingHint()
    {
        var next = JsonFiles.Read<CompanionSettings>(Paths.Settings);
        if (next == pairingHint) return;
        pairingHint = next;
        if (next is null) return;
        saved = saved.Remember(next); JsonFiles.Write(Paths.AutomaticMacs, saved);
        // Pairing can finish while a first discovery is still seeing incomplete
        // services. Recheck that candidate, without replacing the active session.
        foreach (var mac in priority.Connections.Where(item => item.Id == next.DeviceId))
        {
            if (priority.Active?.Connection == mac.Connection) continue;
            priority.Reconsider(mac);
            if (attempts.Remove(mac.Connection, out var attempt)) Retire(attempt);
        }
    }

    private void Remember(ConnectedMac mac, bool active)
    {
        saved = saved.Remember(new CompanionSettings(mac.Id, mac.Name), active);
        JsonFiles.Write(Paths.AutomaticMacs, saved);
    }

    private void RefreshStatus()
    {
        var active = priority.Connections.FirstOrDefault(mac => mac.Connection == arbiter.Owner);
        var waiting = priority.Candidates.Where(mac => mac.Connection != active?.Connection &&
            attempts.TryGetValue(mac.Connection, out var attempt) && attempt.Link.Verified)
            .Select(mac => mac.Name).ToArray();
        var paused = attempts.Values.Where(item => item.Link.Ready && !item.Link.Available).Select(item => item.Link.Mac.Name).ToArray();
        var next = status with { DeviceName = active?.Name, DeviceId = active?.Id, WaitingMacs = waiting, PausedMacs = paused };
        if (arbiter.Releasing is not null)
            next = next with { State = "Waiting", Detail = $"Waiting for {active?.Name ?? "previous Mac"} to release control" };
        else if (active is null)
            next = next with { State = priority.Enumerated ? "Waiting" : "Starting",
                Detail = paused.Length > 0 ? "Enable DeusKVM and allow this PC on a Mac to take control" :
                    priority.DiscoveryCandidates.Length > 0 ? "Checking connected Macs; enable DeusKVM on an updated Mac app" : "Waiting for a paired Mac to connect",
                HidServiceFound = false, ServiceCount = 0, LastDiscovery = null };
        else if (attempts.TryGetValue(active.Connection, out var attempt))
            next = next with { State = attempt.RetryAt != 0 ? "Retrying" : !attempt.Work.IsCompleted ? "Opening" : "Connected",
                Detail = attempt.RetryAt != 0 ? $"Retrying {active.Name}; connection priority is retained. {attempt.Link.Error}" : next.Detail };
        if (next.State != status.State || next.Detail != status.Detail || next.DeviceId != status.DeviceId ||
            !waiting.SequenceEqual(status.WaitingMacs ?? []) || !paused.SequenceEqual(status.PausedMacs ?? [])) Emit(next);
    }

    private void Retire(Attempt attempt)
    {
        attempt.Link.Dispose();
        if (!attempt.Work.IsCompleted) retired.Add((attempt.Work, RuntimeCompat.TickCount64));
    }

    private void Emit(WorkerStatus next)
    {
        next = next with { At = DateTimeOffset.UtcNow, Identity = WindowsIdentity.GetCurrent().Name,
            ProcessSession = Process.GetCurrentProcess().SessionId };
        output.WriteLine(JsonSerializer.Serialize(next));
        status = next with { DiscoveryEvent = null };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            shutdown.Cancel(); heartbeat.Dispose(); discovery?.Dispose(); recovery?.Dispose();
            foreach (var attempt in attempts.Values) attempt.Link.Dispose();
            attempts.Clear(); output.Dispose(); shutdown.Dispose();
        }
        base.Dispose(disposing);
    }
}
