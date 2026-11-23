using System.ComponentModel;
using System.IO.Pipes;
using System.ServiceProcess;

namespace DeusKVM.Companion;

/// <summary>
/// The tray's remote control for the service. Settings go over the service's control pipe and
/// start/stop through SCM (interactive users may start and stop the service), so none of them
/// prompt; only when the service is not running, or is an older build without the pipe, do they
/// fall back to the elevated command line (one UAC prompt). Install, update and removal always
/// elevate.
/// </summary>
internal static class ServiceRemote
{
    private const int ConnectMs = 1000;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The same commands the tray used to elevate, done without a prompt where possible.</summary>
    public static async Task RunAsync(params string[] args)
    {
        var done = args switch
        {
            ["--start"] => await Task.Run(StartThroughScm).ConfigureAwait(true),
            ["--stop"] => await StopAsync().ConfigureAwait(true),
            ["--startup", "auto" or "manual"] => await SendAsync(new(ServiceControlProtocol.Startup, Enabled: args[1] == "auto")).ConfigureAwait(true),
            ["--tray-startup", "on" or "off"] => await SendAsync(new(ServiceControlProtocol.TrayStartup, Enabled: args[1] == "on")).ConfigureAwait(true),
            _ => false
        };
        if (!done) await ServiceCommands.ElevateAsync(args).ConfigureAwait(true);
    }

    /// <summary>Saves the Mac chosen in Add Mac; the service picks it up from settings.json.</summary>
    public static async Task ConfigureAsync(CompanionSettings settings)
    {
        settings.Validate();
        if (!await SendAsync(new(ServiceControlProtocol.Configure, Mac: settings)).ConfigureAwait(true))
            await ServiceCommands.ElevateAsync("--configure", ServiceCommands.EncodeSettings(settings)).ConfigureAwait(true);
    }

    /// <summary>
    /// One request to the running service. False when there is no pipe to talk to (not running,
    /// or an older service); a refusal from the service is thrown with its message.
    /// </summary>
    public static async Task<bool> SendAsync(ServiceControlRequest request)
    {
        if (!Running()) return false;
        var response = await Task.Run(() => Exchange(request)).ConfigureAwait(true);
        if (response is null) return false;
        if (!response.Ok) throw new InvalidOperationException(response.Error ?? "The service refused the change.");
        return true;
    }

    private static ServiceControlResponse? Exchange(ServiceControlRequest request)
    {
        using var pipe = new NamedPipeClientStream(".", ServiceControlProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { pipe.Connect(ConnectMs); }
        catch (Exception error) when (error is System.TimeoutException or IOException or UnauthorizedAccessException) { return null; }
        using var timeout = new CancellationTokenSource(ReplyTimeout);
        using var close = timeout.Token.Register(pipe.Dispose);
        try
        {
            ServiceControlProtocol.WriteLineAsync(pipe, ServiceControlProtocol.Serialize(request), timeout.Token).GetAwaiter().GetResult();
            var line = ServiceControlProtocol.ReadLineAsync(pipe, ServiceControlProtocol.MaximumResponse, timeout.Token).GetAwaiter().GetResult()
                ?? throw new IOException("The service closed the connection without answering.");
            return ServiceControlProtocol.ParseResponse(line);
        }
        catch (ObjectDisposedException) { throw new IOException("The service did not answer in time."); }
    }

    private static bool Running()
    {
        try
        {
            using var service = new ServiceController(Paths.ServiceName);
            return service.Status == ServiceControllerStatus.Running;
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { return false; }
    }

    // SCM directly: the installer lets interactive users start and stop the service. False when
    // this install predates that (access denied), so the caller elevates instead.
    private static bool StartThroughScm()
    {
        using var service = new ServiceController(Paths.ServiceName);
        try { if (service.Status == ServiceControllerStatus.Stopped) service.Start(); }
        catch (InvalidOperationException error) when (error.InnerException is Win32Exception { NativeErrorCode: 5 }) { return false; }
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
        return true;
    }

    // The service stops itself when asked over the pipe; SCM directly if it cannot answer.
    private static async Task<bool> StopAsync()
    {
        var asked = await SendAsync(new(ServiceControlProtocol.Stop)).ConfigureAwait(true);
        return await Task.Run(() =>
        {
            using var service = new ServiceController(Paths.ServiceName);
            if (!asked)
            {
                try { if (service.Status != ServiceControllerStatus.Stopped) service.Stop(); }
                catch (InvalidOperationException error) when (error.InnerException is Win32Exception { NativeErrorCode: 5 }) { return false; }
            }
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            return true;
        }).ConfigureAwait(true);
    }
}

/// <summary>
/// Live service status for the tray: a background watch on the control pipe, reconnecting
/// while the service is stopped or restarting. <see cref="Latest"/> and <see cref="Changed"/>
/// are on the creating (UI) thread.
/// </summary>
internal sealed class ServiceStatusWatch : IDisposable
{
    private readonly SynchronizationContext context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    private readonly CancellationTokenSource stop = new();
    private readonly Thread thread;
    private NamedPipeClientStream? current;

    public ServiceSnapshot? Latest { get; private set; }
    /// <summary>The service is answering the watch (false: stopped, or an older service).</summary>
    public bool Live { get; private set; }
    public event Action? Changed;

    public ServiceStatusWatch()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "DeusKVM status watch" };
        thread.Start();
    }

    private void Run()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", ServiceControlProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                current = pipe;
                if (stop.IsCancellationRequested) return;
                pipe.Connect(1000);
                ServiceControlProtocol.WriteLineAsync(pipe, ServiceControlProtocol.Serialize(new ServiceControlRequest(ServiceControlProtocol.Watch)), stop.Token)
                    .GetAwaiter().GetResult();
                while (ServiceControlProtocol.ReadLineAsync(pipe, ServiceControlProtocol.MaximumResponse, stop.Token).GetAwaiter().GetResult() is { } line)
                {
                    var response = ServiceControlProtocol.ParseResponse(line);
                    if (!response.Ok) break; // too many watchers: retry later
                    Post(response.Status, live: true);
                }
            }
            catch (Exception error) when (error is System.TimeoutException or IOException or UnauthorizedAccessException or
                ObjectDisposedException or OperationCanceledException or InvalidDataException) { }
            finally { current = null; }
            if (stop.IsCancellationRequested) return;
            Post(null, live: false);
            stop.Token.WaitHandle.WaitOne(2000);
        }
    }

    private void Post(ServiceSnapshot? snapshot, bool live) => context.Post(_ =>
    {
        if (stop.IsCancellationRequested) return;
        if (!live && !Live) return; // still down: nothing new to show
        Latest = snapshot; Live = live;
        Changed?.Invoke();
    }, null);

    public void Dispose()
    {
        stop.Cancel();
        current?.Dispose(); // ends a blocked read
        thread.Join(2000);
    }
}
