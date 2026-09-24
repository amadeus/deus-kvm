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

// Service-owned worker in the console user's Default desktop. The tray is not involved.
internal sealed class DesktopHost(Action<DesktopMessage> receive) : IDisposable
{
    private DesktopProcess? process;
    private NamedPipeServerStream? pipe;
    private CancellationTokenSource? lifetime;
    private Channel<DesktopMessage>? outgoing;
    private uint sessionId = uint.MaxValue;
    private string? userSid;
    private long lastMessage;
    private string launchStep = "reading console session";
    public bool Connected { get; private set; }
    public string Detail { get; private set; } = "Waiting for signed-in console desktop";

    public void Refresh(string address)
    {
        try { RefreshConsole(address); }
        catch (Exception error)
        {
            Stop();
            var code = error is Win32Exception native ? $"Win32 {native.NativeErrorCode}" : $"HRESULT 0x{error.HResult:X8}";
            Detail = $"Desktop worker startup failed at {launchStep}: {error.Message} ({code})";
        }
    }

    private void RefreshConsole(string address)
    {
        launchStep = "reading console session";
        var console = ConsoleSession.Read();
        if (console.Id == uint.MaxValue || console.State == "Signed out")
        {
            Stop();
            Detail = "Waiting for signed-in console desktop";
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
            if (process is not null && !process.HasExited && sessionId == console.Id && userSid == sid.Value &&
                RuntimeCompat.TickCount64 - lastMessage < 15000) return;
            Stop();
            sessionId = console.Id;
            userSid = sid.Value;
            var name = "DeusKVM.Desktop." + Guid.NewGuid().ToString("N");
            launchStep = "creating desktop pipe";
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 16384, 16384, security);
            try
            {
                launchStep = "creating console process";
                process = DesktopProcess.Launch(token, name, address);
                lifetime = new CancellationTokenSource();
                outgoing = Channel.CreateBounded<DesktopMessage>(32);
                lastMessage = RuntimeCompat.TickCount64;
                Detail = "Connecting console desktop worker";
                _ = RunAsync(pipe, process.Id, outgoing, lifetime.Token);
                launchStep = "resuming console process";
                process.Resume();
            }
            catch { Stop(); throw; }
        }
    }

    public void Send(DesktopMessage message)
    {
        if (Connected && outgoing?.Writer.TryWrite(message) != true) Stop();
    }

    private async Task RunAsync(NamedPipeServerStream connection, int pid, Channel<DesktopMessage> queue, CancellationToken stop)
    {
        try
        {
            await connection.WaitForConnectionAsync(stop).WaitAsync(TimeSpan.FromSeconds(10), stop);
            if (!GetNamedPipeClientProcessId(connection.SafePipeHandle, out var peer) || peer != pid)
                throw new InvalidDataException("Unexpected desktop pipe client");
            Connected = true;
            Detail = "Console desktop connected";
            receive(new DesktopMessage("connected"));
            var writer = WriteAsync();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var message = await DesktopPipe.Read(connection, stop);
                    lastMessage = RuntimeCompat.TickCount64;
                    if (message.Detail is not null) Detail = message.Detail;
                    receive(message);
                }
            }
            finally { queue.Writer.TryComplete(); await writer; }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException or JsonException)
        {
            if (!stop.IsCancellationRequested)
            {
                Detail = "Desktop worker disconnected: " + error.Message;
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
        var wasConnected = Connected;
        Connected = false;
        lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
        outgoing?.Writer.TryComplete(); outgoing = null;
        pipe?.Dispose(); pipe = null;
        process?.Dispose(); process = null;
        if (wasConnected) receive(new DesktopMessage("disconnected"));
    }

    public void Dispose() => Stop();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint session, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
