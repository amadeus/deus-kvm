using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using DeusKVM.Companion.Core;
using Microsoft.Win32.SafeHandles;

namespace DeusKVM.Companion;

/// <summary>
/// `--clipboard-worker`: the signed-in console user's clipboard helper, started by the service.
/// The clipboard, file paste into Explorer (OLE delay-rendered streams) and images must run as
/// that user, so this is the one part of the companion that is not SYSTEM. Everything else (input,
/// edge return, cursor, displays) is the session agent's. It exits when the service's pipe closes.
/// </summary>
internal sealed class ClipboardWorker : ApplicationContext
{
    private readonly NamedPipeClientStream pipe;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Channel<DesktopMessage> output = Channel.CreateBounded<DesktopMessage>(128);
    // The host replaces a helper that has said nothing for 15 s; the clipboard can be quiet for hours.
    private readonly System.Windows.Forms.Timer heartbeat = new() { Interval = 5000 };
    private readonly int parent;
    private DesktopClipboard? clipboard;
    private bool started;

    public ClipboardWorker(string name, int parent)
    {
        this.parent = parent;
        pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        heartbeat.Tick += (_, _) => Send(new DesktopMessage("alive"));
        Application.Idle += Start;
    }

    private async void Start(object? sender, EventArgs args)
    {
        if (started) return;
        started = true;
        Application.Idle -= Start;
        try
        {
            await pipe.ConnectAsync(10000, shutdown.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != parent)
                throw new InvalidDataException("Unexpected clipboard pipe server");
            clipboard = new DesktopClipboard(Send);
            heartbeat.Start();
            // Pipe EOF ends this process independently of the message loop.
            await Task.Run(() => DesktopPipe.Pump(pipe, output.Reader, message =>
            {
                if (message.Kind.StartsWith("clipboard-", StringComparison.Ordinal)) clipboard?.Post(message);
            }, shutdown.Token)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException) { }
        finally { Environment.Exit(0); }
    }

    private void Send(DesktopMessage message)
    {
        if (!output.Writer.TryWrite(message)) pipe.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.Idle -= Start;
            clipboard?.Dispose();
            heartbeat.Dispose();
            shutdown.Cancel(); pipe.Dispose(); shutdown.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
