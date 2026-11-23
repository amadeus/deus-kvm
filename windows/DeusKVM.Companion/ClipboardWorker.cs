using System.ComponentModel;
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
/// edge return, displays) is the session agent's, except hiding the cursor on the user's desktop
/// after the Mac takes control back: a hider window owned by SYSTEM keeps the taskbar from hearing
/// that the mouse left it (its tooltips stay open), so that surface is this user process's. It
/// exits when the service's pipe closes.
/// </summary>
internal sealed class ClipboardWorker : ApplicationContext
{
    private readonly NamedPipeClientStream pipe;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Channel<DesktopMessage> output = Channel.CreateBounded<DesktopMessage>(128);
    // The host replaces a helper that has said nothing for 15 s; the clipboard can be quiet for hours.
    private readonly System.Windows.Forms.Timer heartbeat = new() { Interval = 5000 };
    private readonly int parent;
    private readonly InactiveCursor cursor = new(0);
    private readonly RawWindow window;
    private SynchronizationContext? context;
    private DesktopClipboard? clipboard;
    private bool started;

    public ClipboardWorker(string name, int parent)
    {
        this.parent = parent;
        pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        window = new RawWindow(this);
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
            var context = this.context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            clipboard = new DesktopClipboard(Send);
            // The PC's own mouse reveals the cursor (the Mac's steps are injected with InjectTag).
            if (!DesktopNative.RegisterRawInputDevices([
                new() { Page = 1, Usage = 2, Flags = 0x0100, Target = window.Handle } // RIDEV_INPUTSINK
            ], 1, (uint)Marshal.SizeOf<DesktopNative.RawDevice>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            heartbeat.Start();
            // Pipe EOF ends this process independently of the message loop.
            await Task.Run(() => DesktopPipe.Pump(pipe, output.Reader, message =>
            {
                if (message.Kind.StartsWith("clipboard-", StringComparison.Ordinal)) clipboard?.Post(message);
                else if (message.Kind == "cursor-hide") context.Post(_ => cursor.Hide(), null);
                else if (message.Kind == "cursor-reveal") context.Post(_ => cursor.Reveal(), null);
            }, shutdown.Token)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException or Win32Exception or System.Text.Json.JsonException) { }
        finally { Environment.Exit(0); }
    }

    private void Send(DesktopMessage message)
    {
        if (!output.Writer.TryWrite(message)) pipe.Dispose();
    }

    // Raw mouse input on the window thread: anything not injected by the agent reveals the cursor.
    private void RawInput(IntPtr handle)
    {
        if (!cursor.Hidden) return;
        var headerSize = (uint)(8 + IntPtr.Size * 2);
        uint size = 0;
        if (DesktopNative.GetRawInputData(handle, 0x10000003, IntPtr.Zero, ref size, headerSize) == uint.MaxValue ||
            size < headerSize + 24 || size > 4096) return;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (DesktopNative.GetRawInputData(handle, 0x10000003, buffer, ref size, headerSize) != size ||
                Marshal.ReadInt32(buffer) != 0) return;
            var mouse = buffer + (int)headerSize;
            if (Marshal.ReadInt32(mouse, 20) == unchecked((int)DesktopNative.InjectTag)) return;
            if (Marshal.ReadInt32(mouse, 12) != 0 || Marshal.ReadInt32(mouse, 16) != 0 || Marshal.ReadInt16(mouse, 4) != 0) cursor.Reveal();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.Idle -= Start;
            cursor.Dispose();
            window.DestroyHandle();
            clipboard?.Dispose();
            heartbeat.Dispose();
            shutdown.Cancel(); pipe.Dispose(); shutdown.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class RawWindow : NativeWindow
    {
        private readonly ClipboardWorker owner;
        public RawWindow(ClipboardWorker owner)
        {
            this.owner = owner;
            CreateHandle(new CreateParams { Caption = "DeusKVM clipboard input", Parent = new IntPtr(-3) });
        }
        protected override void WndProc(ref System.Windows.Forms.Message message)
        {
            if (message.Msg == 0xFF) owner.RawInput(message.LParam);
            base.WndProc(ref message);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
