using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;

namespace DeusKVM.Companion;

/// <summary>
/// The service's control pipe, so the tray is a remote control: status (and a live watch),
/// saving a Mac from Add Mac, the startup preferences and stopping the service, with no UAC
/// prompt. SYSTEM and Administrators have full access, interactive users read/write; network
/// clients are denied. Install, update, removal and starting a stopped service stay with SCM
/// and the elevated command line. Each connection carries one request (or one watch).
/// </summary>
internal sealed class ServiceControlServer : IDisposable, IServiceControlTarget
{
    private const int Instances = 8, Watchers = 4;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private const int WatchPollMs = 500, WatchRepeatMs = 5000;
    private readonly CancellationTokenSource stop = new();
    private readonly Func<ServiceSnapshot?> snapshot;
    private readonly Action<string> log;
    private int watchers;

    /// <param name="snapshot">The current status; by default the service's status.json.</param>
    public ServiceControlServer(Action<string> log, Func<ServiceSnapshot?>? snapshot = null)
    {
        this.log = log;
        this.snapshot = snapshot ?? StatusFile;
        _ = Task.Run(AcceptAsync);
    }

    // status.json, reread only after the service rewrites it (watchers poll twice a second).
    private DateTime statusWritten;
    private ServiceSnapshot? statusRead;
    private readonly object statusGate = new();
    private ServiceSnapshot? StatusFile()
    {
        lock (statusGate)
        {
            var written = File.GetLastWriteTimeUtc(Paths.Status);
            if (written != statusWritten) { statusRead = JsonFiles.Read<ServiceSnapshot>(Paths.Status); statusWritten = written; }
            return statusRead;
        }
    }

    internal static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        // Whoever is signed in at the console (standard users included), not services or batch jobs.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    private async Task AcceptAsync()
    {
        string? lastError = null;
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(ServiceControlProtocol.PipeName, PipeDirection.InOut, Instances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, Security());
                using (stop.Token.Register(pipe.Dispose)) await pipe.WaitForConnectionAsync().ConfigureAwait(false);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => ServeAsync(connected));
                lastError = null;
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                pipe?.Dispose();
                if (stop.IsCancellationRequested) return;
                // Every instance busy, or the name briefly unavailable: try again shortly.
                if (error.Message != lastError) log($"Control pipe: {error.Message}");
                lastError = error.Message;
                try { await Task.Delay(500, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        using (timeout.Token.Register(pipe.Dispose)) // net48 pipe reads ignore the token; closing ends them
        {
            try
            {
                timeout.CancelAfter(RequestTimeout);
                var line = await ServiceControlProtocol.ReadLineAsync(pipe, ServiceControlProtocol.MaximumRequest, timeout.Token).ConfigureAwait(false);
                if (line is null) return;
                ServiceControlRequest request;
                try { request = ServiceControlProtocol.Parse(line); }
                catch (ArgumentException error)
                {
                    await Reply(pipe, new ServiceControlResponse(false, error.Message), timeout.Token).ConfigureAwait(false);
                    return;
                }
                if (request.Command == ServiceControlProtocol.Watch)
                {
                    timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    await WatchAsync(pipe, timeout.Token).ConfigureAwait(false);
                    return;
                }
                var response = ServiceControlProtocol.Handle(request, this);
                if (request.Command != ServiceControlProtocol.Status)
                    log($"Control pipe: {request.Command}{(response.Ok ? "" : " failed: " + response.Error)}");
                await Reply(pipe, response, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException) { }
        }
    }

    // A status line now, then whenever it changes, and every few seconds so the tray knows the
    // service is still there. Ends when the client goes away or the service stops.
    private async Task WatchAsync(NamedPipeServerStream pipe, CancellationToken cancellation)
    {
        if (Interlocked.Increment(ref watchers) > Watchers)
        {
            Interlocked.Decrement(ref watchers);
            await Reply(pipe, new ServiceControlResponse(false, "Too many status watchers."), cancellation).ConfigureAwait(false);
            return;
        }
        try
        {
            string? sent = null;
            var sentAt = 0L;
            while (!cancellation.IsCancellationRequested && pipe.IsConnected)
            {
                var line = ServiceControlProtocol.Serialize(new ServiceControlResponse(true, Status: Status()));
                if (line != sent || RuntimeCompat.TickCount64 - sentAt >= WatchRepeatMs)
                {
                    await ServiceControlProtocol.WriteLineAsync(pipe, line, cancellation).ConfigureAwait(false);
                    sent = line;
                    sentAt = RuntimeCompat.TickCount64;
                }
                await Task.Delay(WatchPollMs, cancellation).ConfigureAwait(false);
            }
        }
        finally { Interlocked.Decrement(ref watchers); }
    }

    private static Task Reply(Stream pipe, ServiceControlResponse response, CancellationToken cancellation) =>
        ServiceControlProtocol.WriteLineAsync(pipe, ServiceControlProtocol.Serialize(response), cancellation);

    public ServiceSnapshot? Status()
    {
        try { return snapshot(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    public void Configure(CompanionSettings mac) => ServiceSettings.Configure(mac);
    public void SetAutomaticStartup(bool automatic) => ServiceSettings.SetAutomaticStartup(automatic);
    public void SetTrayStartup(bool enabled) => ServiceSettings.SetTrayStartup(enabled);

    // Through SCM, as any stop is, after the reply has gone: OnStop disposes this server.
    public void StopService() => _ = Task.Run(async () =>
    {
        await Task.Delay(200).ConfigureAwait(false);
        try
        {
            using var service = new ServiceController(Paths.ServiceName);
            if (service.Status == ServiceControllerStatus.Running) service.Stop();
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { log($"Control pipe: stop failed: {error.Message}"); }
    });

    // Not disposed: connections still running link their timeouts to it; cancelling ends them.
    public void Dispose() => stop.Cancel();
}
