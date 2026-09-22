using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

internal sealed class CompanionService : ServiceBase
{
    private CancellationTokenSource? shutdown;
    private Task? runner;
    private volatile WorkerStatus worker = new("Starting", "Starting Bluetooth recovery", DateTimeOffset.UtcNow);
    private readonly object logLock = new();

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
        runner = RunAsync(shutdown.Token);
    }

    protected override void OnStop()
    {
        Log("Stop requested; automatic startup preference is unchanged.");
        shutdown?.Cancel();
        if (runner is not null && !runner.Wait(TimeSpan.FromSeconds(10)))
            throw new System.TimeoutException("Bluetooth worker did not stop within ten seconds.");
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
            Log($"Service started as {WindowsIdentity.GetCurrent().Name}; session {Process.GetCurrentProcess().SessionId}");
            while (!stop.IsCancellationRequested)
            {
                await RunWorkerAsync(stop);
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
            SaveStatus();
        }
    }

    private async Task RunWorkerAsync(CancellationToken stop)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory
            }
        };
        process.StartInfo.ArgumentList.Add("--ble-worker");
        worker = new("Starting", "Watching paired Macs under the service account.", DateTimeOffset.UtcNow);
        using var job = new WorkerJob(allowBreakaway: true);
        process.Start();
        job.Add(process);
        Log($"Bluetooth worker started: PID {process.Id}");
        var watchdog = new WorkerWatchdog();
        using var readerStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var output = ReadOutputAsync();
        var errors = process.StandardError.ReadToEndAsync(readerStop.Token);
        try
        {
            while (!stop.IsCancellationRequested && !process.HasExited)
            {
                SaveStatus();
                if (watchdog.IsExpired())
                {
                    Log("Bluetooth worker is unresponsive; replacing it.");
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stop);
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            readerStop.Cancel();
            try { await output; } catch (OperationCanceledException) { }
            try
            {
                var stderr = await errors;
                if (!string.IsNullOrWhiteSpace(stderr)) Log($"Worker stderr: {stderr}");
            }
            catch (OperationCanceledException) { }
            Log($"Bluetooth worker exited: {process.ExitCode}");
        }

        async Task ReadOutputAsync()
        {
            string? previous = null;
            while (await process.StandardOutput.ReadLineAsync(readerStop.Token) is { } line)
            {
                if (line.Length > 16384) throw new InvalidDataException("Worker response is too large.");
                var next = JsonSerializer.Deserialize<WorkerStatus>(line)
                    ?? throw new InvalidDataException("Empty worker response.");
                worker = next;
                watchdog.Observe(next.State);
                if (next.DiscoveryEvent is { } discovery) Log(discovery);
                var summary = $"{next.State}: {next.Detail}; last discovery={next.LastDiscovery:O}";
                if (summary != previous) { Log(summary); previous = summary; }
            }
        }
    }

    private void SaveStatus()
    {
        var console = ConsoleSession.Read();
        JsonFiles.Write(Paths.Status, new ServiceSnapshot(Environment.ProcessId,
            WindowsIdentity.GetCurrent().Name, Process.GetCurrentProcess().SessionId,
            console.Id, console.State, worker, DateTimeOffset.UtcNow));
    }

    private void Log(string text)
    {
        lock (logLock)
        {
            Directory.CreateDirectory(Paths.DataDirectory);
            if (File.Exists(Paths.Log) && new FileInfo(Paths.Log).Length > 2 * 1024 * 1024)
                File.Move(Paths.Log, Paths.Log + ".1", overwrite: true);
            File.AppendAllText(Paths.Log, $"{DateTimeOffset.Now:O} {text}{Environment.NewLine}");
        }
    }
}
