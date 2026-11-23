using System.ServiceProcess;
using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

internal sealed class CompanionService : ServiceBase
{
    private CancellationTokenSource? shutdown;
    private Task? runner;
    private ServiceControlServer? control; // the tray's remote control
    private volatile WorkerStatus worker = new("Starting", "Starting the Mac link", DateTimeOffset.UtcNow);
    private readonly object logLock = new();
    private WorkerStatus? savedWorker;
    private (uint Id, string State) savedConsole;
    private long savedAt;

    public CompanionService()
    {
        ServiceName = Paths.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanHandleSessionChangeEvent = true;
        CanHandlePowerEvent = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        shutdown = new CancellationTokenSource();
        control = new ServiceControlServer(Log, () => Snapshot(ConsoleSession.Read(), worker, worker.At)); // live, not status.json
        runner = RunAsync(shutdown.Token);
    }

    protected override void OnStop()
    {
        Log("Stop requested; automatic startup preference is unchanged.");
        shutdown?.Cancel();
        control?.Dispose(); control = null;
        if (runner is not null && !runner.Wait(TimeSpan.FromSeconds(10)))
            throw new System.TimeoutException("The link did not stop within ten seconds.");
        shutdown?.Dispose();
        shutdown = null;
    }

    protected override void OnShutdown() => OnStop();
    protected override void OnSessionChange(SessionChangeDescription change) =>
        Log($"Session {change.SessionId}: {change.Reason}");
    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        Log($"Power: {powerStatus}");
        return true;
    }

    private async Task RunAsync(CancellationToken stop)
    {
        try
        {
            Log($"Service started as {ProcessIdentity.Name}; session {ProcessIdentity.Session}");
            while (!stop.IsCancellationRequested)
            {
                await RunLinkAsync(stop);
                await Task.Delay(TimeSpan.FromSeconds(2), stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            Log($"Fatal service error: {error}");
            // a nonzero process exit lets SCM recovery restart an unexpected failure.
            Environment.Exit(1);
        }
        finally
        {
            worker = new("Stopped", "Companion service is stopped.", DateTimeOffset.UtcNow);
            SaveStatus(force: true);
        }
    }

    // The link (discovery, Mac connections, priority, the session helpers) runs in this process on
    // its own STA thread with a message loop, which its sessions post their work to. A link that
    // stops reporting is a hang this process cannot undo from outside: the service exits nonzero
    // and SCM recovery starts it again.
    private async Task RunLinkAsync(CancellationToken stop)
    {
        worker = new("Starting", "Looking for Macs on the network", DateTimeOffset.UtcNow);
        var watchdog = new WorkerWatchdog();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        LinkWorker? link = null;
        string? previous = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var running = new LinkWorker(next =>
                {
                    worker = next;
                    watchdog.Observe(next.State);
                    if (next.DiscoveryEvent is { } discovery) Log(discovery);
                    var summary = $"{next.State}: {next.Detail}; last discovery={next.LastDiscovery:O}";
                    if (summary != previous) { Log(summary); previous = summary; }
                });
                Volatile.Write(ref link, running);
                if (stop.IsCancellationRequested) running.RequestStop();
                Application.Run(running);
                exited.TrySetResult(running.ExitCode);
            }
            catch (Exception error) { Log($"Link failed: {error}"); exited.TrySetResult(1); }
        }) { IsBackground = true, Name = "DeusKVM link" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Log("Link started");
        using var cancel = stop.Register(() => Volatile.Read(ref link)?.RequestStop());
        while (!exited.Task.IsCompleted)
        {
            SaveStatus();
            if (!stop.IsCancellationRequested && watchdog.IsExpired())
            {
                Log("The link is unresponsive; restarting the service.");
                SaveStatus(force: true);
                Environment.Exit(1);
            }
            await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
        }
        Log($"Link stopped: {await exited.Task}");
    }

    // Readers need changes promptly and otherwise only a refresh well inside ServicePolicy.IsFresh's
    // 45 s window; rewriting every 2 s cost a file replace (and antivirus scan) for unchanged state.
    private void SaveStatus(bool force = false)
    {
        var console = ConsoleSession.Read();
        var current = worker;
        if (!force && ReferenceEquals(current, savedWorker) && console == savedConsole &&
            RuntimeCompat.TickCount64 - savedAt < 15000) return;
        try
        {
            JsonFiles.Write(Paths.Status, Snapshot(console, current));
            savedWorker = current; savedConsole = console; savedAt = RuntimeCompat.TickCount64;
        }
        // A transient lock (for example antivirus on status.json.new) retries on the next pass.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // The live copy (for the tray's watch) is dated by the link's last report, which repeats every
    // 10 s, so it changes only when something does; the file gets the time it was written.
    private static ServiceSnapshot Snapshot((uint Id, string State) console, WorkerStatus current, DateTimeOffset? at = null) =>
        new(RuntimeCompat.ProcessId, ProcessIdentity.Name, ProcessIdentity.Session, console.Id, console.State, current, at ?? DateTimeOffset.UtcNow);

    private void Log(string text)
    {
        lock (logLock)
        {
            Directory.CreateDirectory(Paths.DataDirectory);
            if (File.Exists(Paths.Log) && new FileInfo(Paths.Log).Length > 2 * 1024 * 1024)
                RuntimeCompat.MoveReplace(Paths.Log, Paths.Log + ".1");
            File.AppendAllText(Paths.Log, $"{DateTimeOffset.Now:O} {text}{Environment.NewLine}");
        }
    }
}
