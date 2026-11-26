using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using DeusKVM.Companion.Core;
using Microsoft.Win32.SafeHandles;

namespace DeusKVM.Companion;

/// <summary>Which helper a <see cref="DesktopHost"/> keeps running in the console session.</summary>
internal enum DesktopRole
{
    /// <summary>
    /// The session agent: SYSTEM in the console session, whenever there is one (login screen,
    /// signed in, locked). Input, edge return, cursor placement and displays.
    /// </summary>
    Agent,
    /// <summary>The clipboard helper: the signed-in console user, only while someone is signed in.</summary>
    Clipboard
}

/// <summary>
/// Service-side supervisor for one helper process in the console session, talking JSON lines
/// (<see cref="DesktopPipe"/>) over a private pipe. It starts the helper on demand, and replaces
/// it when the console session (or, for the clipboard, its user) changes, when it exits, or when
/// it has been silent for 15 s (both helpers send something at least every few seconds).
/// </summary>
internal sealed class DesktopHost(DesktopRole role, Action<DesktopMessage> receive) : IDisposable
{
    private SessionProcess? process;
    private NamedPipeServerStream? pipe;
    private CancellationTokenSource? lifetime;
    private Channel<DesktopMessage>? outgoing;
    private uint sessionId = uint.MaxValue;
    private string? userSid;
    private long lastMessage;
    private long generation;
    public long Generation => Interlocked.Read(ref generation);
    private string launchStep = "reading console session";
    public bool Connected { get; private set; }
    public string Detail { get; private set; } = role == DesktopRole.Agent ? "Waiting for the console session" : "Waiting for a signed-in user";
    private string Name => role == DesktopRole.Agent ? "Session agent" : "Clipboard helper";

    public void Refresh()
    {
        try { RefreshConsole(); }
        catch (Exception error)
        {
            Stop();
            var code = error is Win32Exception native ? $"Win32 {native.NativeErrorCode}" : $"HRESULT 0x{error.HResult:X8}";
            Detail = $"{Name} startup failed at {launchStep}: {error.Message} ({code})";
        }
    }

    private void RefreshConsole()
    {
        launchStep = "reading console session";
        var console = ConsoleSession.Read();
        if (console.Id == uint.MaxValue || (role == DesktopRole.Clipboard && console.State != "User session present"))
        {
            Stop();
            Detail = role == DesktopRole.Agent ? "Waiting for the console session" : "Waiting for a signed-in user";
            return;
        }
        if (role == DesktopRole.Agent)
        {
            if (Running(console.Id, null)) return;
            Launch(console.Id, null, null);
            return;
        }
        launchStep = "getting console user token";
        if (!WTSQueryUserToken(console.Id, out var token))
        {
            var error = Marshal.GetLastWin32Error();
            token.Dispose();
            throw new Win32Exception(error, $"WTSQueryUserToken(session {console.Id}): {new Win32Exception(error).Message}");
        }
        using (token)
        {
            launchStep = "reading console user identity";
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            var sid = identity.User ?? throw new InvalidOperationException("Console token has no user SID");
            if (Running(console.Id, sid.Value)) return;
            Launch(console.Id, sid, token);
        }
    }

    private bool Running(uint session, string? sid) =>
        process is not null && !process.HasExited && sessionId == session && userSid == sid &&
        RuntimeCompat.TickCount64 - lastMessage < 15000;

    private void Launch(uint session, SecurityIdentifier? user, SafeAccessTokenHandle? token)
    {
        Stop();
        sessionId = session;
        userSid = user?.Value;
        var name = (role == DesktopRole.Agent ? "DeusKVM.Agent." : "DeusKVM.Clipboard.") + Guid.NewGuid().ToString("N");
        launchStep = "creating pipe";
        // SYSTEM only for the agent (it is SYSTEM); SYSTEM and the console user for the clipboard.
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        if (user is not null) security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 16384, 16384, security);
        try
        {
            launchStep = "creating console process";
            process = role == DesktopRole.Agent
                ? SessionProcess.LaunchAsSystem(session, "--session-agent", name)
                : SessionProcess.LaunchAsUser(token!, "--clipboard-worker", name);
            lifetime = new CancellationTokenSource();
            outgoing = Channel.CreateBounded<DesktopMessage>(32);
            lastMessage = RuntimeCompat.TickCount64;
            Detail = $"Connecting {Name.ToLowerInvariant()}";
            _ = RunAsync(pipe, process.Id, outgoing, lifetime.Token, Generation);
            launchStep = "resuming console process";
            process.Resume();
        }
        catch { Stop(); throw; }
    }

    public void Send(DesktopMessage message)
    {
        if (Connected && outgoing?.Writer.TryWrite(message) != true) Stop();
    }

    private async Task RunAsync(NamedPipeServerStream connection, int pid, Channel<DesktopMessage> queue, CancellationToken stop, long runGeneration)
    {
        try
        {
            await connection.WaitForConnectionAsync(stop).WaitAsync(TimeSpan.FromSeconds(10), stop);
            if (!GetNamedPipeClientProcessId(connection.SafePipeHandle, out var peer) || peer != pid)
                throw new InvalidDataException("Unexpected helper pipe client");
            if (stop.IsCancellationRequested || runGeneration != Generation) return;
            Connected = true;
            Detail = $"{Name} connected";
            receive(new DesktopMessage("connected", HostGeneration: runGeneration));
            var writer = WriteAsync();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var message = await DesktopPipe.Read(connection, stop);
                    lastMessage = RuntimeCompat.TickCount64;
                    if (message.Kind == "alive") continue;
                    if (message.Detail is not null) Detail = message.Detail;
                    if (runGeneration == Generation && !stop.IsCancellationRequested) receive(message with { HostGeneration = runGeneration });
                }
            }
            finally { queue.Writer.TryComplete(); await writer; }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException or JsonException)
        {
            if (!stop.IsCancellationRequested && runGeneration == Generation)
            {
                Detail = $"{Name} disconnected: " + error.Message;
                Stop();
            }
        }

        async Task WriteAsync()
        {
            try { await foreach (var message in queue.Reader.ReadAllAsync(stop)) await DesktopPipe.Write(connection, message, stop); }
            catch { connection.Dispose(); }
        }
    }

    private void Stop()
    {
        var nextGeneration = Interlocked.Increment(ref generation);
        var wasConnected = Connected;
        Connected = false;
        lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
        outgoing?.Writer.TryComplete(); outgoing = null;
        pipe?.Dispose(); pipe = null;
        process?.Dispose(); process = null;
        if (wasConnected) receive(new DesktopMessage("disconnected", HostGeneration: nextGeneration));
    }

    public void Dispose() => Stop();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint session, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
