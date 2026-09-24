using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using DeusKVM.Companion.Core;
using Microsoft.Win32.SafeHandles;

namespace DeusKVM.Companion;

internal sealed class DesktopWorker : ApplicationContext
{
    private readonly NamedPipeClientStream pipe;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Channel<DesktopMessage> output = Channel.CreateBounded<DesktopMessage>(32);
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly RawWindow window;
    private DesktopClipboard? clipboard;
    private readonly InactiveCursor cursor = new();
    private readonly string address;
    private readonly int parent;
    private readonly HandoffSession handoff = new();
    private readonly DesktopRecoveryPoll recoveryPoll = new();
    private readonly MouseIdentityCache devices = new();
    private int selectedMotion, otherMotion, heldMotion, clippedMotion, leaveRequests;
    private long lastEdgeReport;
    private MonitorInfo[] monitors = [];
    private EdgeConfiguration? config;
    private byte blind = 4;
    private bool started;

    public DesktopWorker(string name, int parent, string address)
    {
        this.parent = parent;
        this.address = address;
        pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        window = new RawWindow(this);
        timer.Tick += (_, _) => Refresh();
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
                throw new InvalidDataException("Unexpected desktop pipe server");
            var context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            clipboard = new DesktopClipboard(Send);
            var connection = Task.Run(async () =>
            {
                try
                {
                    await DesktopPipe.Pump(pipe, output.Reader,
                        message => { if (message.Kind.StartsWith("clipboard-", StringComparison.Ordinal)) clipboard?.Post(message);
                            else context.Post(_ => Handle(message), null); }, shutdown.Token).ConfigureAwait(false);
                }
                finally { Environment.Exit(0); }
            });
            if (!DesktopNative.RegisterRawInputDevices([
                new() { Page = 1, Usage = 2, Flags = 0x2100, Target = window.Handle }
            ], 1, (uint)Marshal.SizeOf<DesktopNative.RawDevice>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Refresh();
            timer.Start();
            // Pipe EOF must terminate this process independently of the desktop
            // message loop: it replaces cross-session job inheritance.
            await connection.ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or Win32Exception or System.Text.Json.JsonException) { }
        finally { Environment.Exit(0); }
    }

    private void Send(DesktopMessage message)
    {
        if (!output.Writer.TryWrite(message)) pipe.Dispose();
    }

    private void Refresh()
    {
        var state = DesktopNative.DesktopState();
        if (state.Desktop != 0)
        {
            UpdateDesktopState(state);
            return; // Secure-desktop enumeration must not invalidate the saved display/session.
        }
        var next = Screen.AllScreens.Select(screen => new MonitorInfo(screen.DeviceName,
            screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height, DesktopNative.Dpi(screen.Bounds.X, screen.Bounds.Y), screen.Primary)).ToArray();
        if (!next.SequenceEqual(monitors))
        {
            cursor.Reveal();
            handoff.Exit(); monitors = next;
            Send(new DesktopMessage("screens", Monitors: monitors));
        }
        uint count = 0;
        var size = (uint)Marshal.SizeOf<DesktopNative.DeviceEntry>();
        if (DesktopNative.GetRawInputDeviceList(null, ref count, size) != uint.MaxValue && count <= 1024)
        {
            var list = new DesktopNative.DeviceEntry[count];
            var read = DesktopNative.GetRawInputDeviceList(list, ref count, size);
            if (read != uint.MaxValue)
            {
                var present = list.Take((int)read).Where(item => item.Type == 0).Select(item => item.Handle).ToHashSet();
                devices.Refresh(present, device => DesktopNative.IsMacMouse(device, address));
            }
        }
        UpdateDesktopState(state);
        ReportEdgeHealth();
    }

    private void UpdateDesktopState((byte Blind, byte Desktop) state)
    {
        var mouse = devices.HasMatch;
        var accessible = state.Desktop == 0;
        blind = !accessible ? state.Blind : !mouse ? (byte)3 : (byte)0;
        handoff.SetAvailable(blind == 0);
        if (blind != 0) cursor.Reveal();
        Send(new DesktopMessage("state", Blind: blind, Desktop: state.Desktop, MousePresent: mouse,
            Detail: !accessible ? "Secure desktop; use the Mac hotkey" : !mouse ?
                "Waiting for the active Mac's HID mouse" : "Windows edge return ready"));
    }

    private void Handle(DesktopMessage message)
    {
        if (message.Kind.StartsWith("clipboard-", StringComparison.Ordinal)) { clipboard?.Post(message); return; }
        switch (message.Kind)
        {
            case "config":
                cursor.Reveal();
                handoff.Exit();
                config = message.Config is { Edge: < 4 } ? message.Config : null;
                break;
            case "reset": cursor.Reveal(); handoff.Exit(); break;
            case "exit":
                if (handoff.Accept(message.SwitchId))
                {
                    handoff.Exit();
                    if (blind == 0) cursor.Hide();
                }
                break;
            case "resume":
                // Reattach to current Mac ownership after login/worker startup.
                // Never replay entry placement over the user's current cursor.
                if (config?.Edge == message.Edge && monitors.Any(item => item.Id == config.Monitor))
                {
                    handoff.Enter(message.SwitchId);
                    cursor.Reveal();
                    UpdateDesktopState(DesktopNative.DesktopState());
                }
                break;
            case "enter":
                cursor.Reveal();
                handoff.Exit();
                var monitor = monitors.FirstOrDefault(item => item.Id == config?.Monitor);
                var ok = false;
                var position = new DesktopNative.Point();
                if (monitor is not null && message.Edge == config?.Edge)
                {
                    // The Mac already owns this handoff even when secure desktop
                    // prevents cursor placement. Resume edge observation on return.
                    handoff.Enter(message.SwitchId);
                    if (DesktopNative.IsDefaultDesktop())
                    {
                        var point = message.Center ? monitor.Center : monitor.Entry(message.Edge, message.Fraction);
                        ok = DesktopNative.SetCursorPos(point.X, point.Y) && DesktopNative.GetCursorPos(out position) &&
                            Math.Abs(position.X - point.X) <= 1 && Math.Abs(position.Y - point.Y) <= 1;
                    }
                }
                Send(new DesktopMessage("ack", SwitchId: message.SwitchId, Ok: ok,
                    X: position.X, Y: position.Y, Blind: ok ? blind : (byte)4));
                break;
        }
    }

    private void RawInput(IntPtr handle)
    {
        // Probe immediately on first movement, but do not enumerate devices and
        // send Bluetooth status on every mouse packet while recovery is pending.
        if (recoveryPoll.ShouldRefresh(blind != 0, handoff.Active is not null, RuntimeCompat.TickCount64)) Refresh();
        if (!cursor.Hidden && (!handoff.CanReturn || config is null)) return;
        var headerSize = (uint)(8 + IntPtr.Size * 2);
        uint size = 0;
        if (DesktopNative.GetRawInputData(handle, 0x10000003, IntPtr.Zero, ref size, headerSize) == uint.MaxValue ||
            size < headerSize + 24 || size > 4096) return;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (DesktopNative.GetRawInputData(handle, 0x10000003, buffer, ref size, headerSize) != size ||
                Marshal.ReadInt32(buffer) != 0) return;
            var device = Marshal.ReadIntPtr(buffer, 8);
            var selected = devices.Resolve(device, candidate => DesktopNative.IsMacMouse(candidate, address));
            var mouse = buffer + (int)headerSize;
            var dx = Marshal.ReadInt32(mouse, 12);
            var dy = Marshal.ReadInt32(mouse, 16);
            var buttons = Marshal.ReadInt16(mouse, 4);
            if (dx != 0 || dy != 0)
            {
                if (selected) selectedMotion++; else otherMotion++;
            }
            if (!selected)
            {
                if (dx != 0 || dy != 0 || buttons != 0) cursor.Reveal();
                return;
            }
            if (handoff.Active is not { } id || config is null || !handoff.CanReturn ||
                (Marshal.ReadInt16(mouse) & 1) != 0 || !DesktopNative.GetCursorPos(out var point)) return;
            if (dx == 0 && dy == 0) return;
            var monitor = monitors.FirstOrDefault(item => item.Id == config.Monitor);
            if (monitor is null) return;
            var held = new[] { 1, 2, 4, 5, 6 }.Any(key => DesktopNative.GetAsyncKeyState(key) < 0);
            var clipped = !DesktopNative.GetClipCursor(out var clip) || clip.Left > monitor.X || clip.Top > monitor.Y ||
                clip.Right < monitor.X + monitor.W || clip.Bottom < monitor.Y + monitor.H;
            if (held) heldMotion++;
            if (clipped) clippedMotion++;
            if (EdgeDetector.Observe(monitor, monitors, config, point.X, point.Y, dx, dy, held, clipped))
            {
                leaveRequests++;
                Send(new DesktopMessage("leave", SwitchId: id, Edge: config.Edge,
                    Fraction: monitor.Fraction(config.Edge, point.X, point.Y)));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private void ReportEdgeHealth()
    {
        var now = RuntimeCompat.TickCount64;
        if (now - lastEdgeReport < 5000) return;
        lastEdgeReport = now;
        if (handoff.Active is not null || selectedMotion + otherMotion != 0)
        {
            var value = $"edge health: active={handoff.Active is not null} blind={blind} configured={config is not null} " +
                $"mouse={devices.HasMatch} selected={selectedMotion} other={otherMotion} held={heldMotion} clipped={clippedMotion} leave={leaveRequests}";
            // File IO never runs on the raw-input thread. At most one aggregate per five seconds.
            ThreadPool.QueueUserWorkItem(_ => EdgeDiagnostics.Write(value));
        }
        selectedMotion = otherMotion = heldMotion = clippedMotion = leaveRequests = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.Idle -= Start;
            clipboard?.Dispose();
            cursor.Dispose();
            shutdown.Cancel(); timer.Dispose(); pipe.Dispose(); window.DestroyHandle(); shutdown.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class RawWindow : NativeWindow
    {
        private readonly DesktopWorker owner;
        public RawWindow(DesktopWorker owner)
        {
            this.owner = owner;
            CreateHandle(new CreateParams { Caption = "DeusKVM desktop input", Parent = new IntPtr(-3) });
        }
        protected override void WndProc(ref System.Windows.Forms.Message message)
        {
            if (message.Msg == 0xFF) owner.RawInput(message.LParam);
            if (message.Msg == 0xFE) owner.devices.Clear(); // device handles can be reused after reconnect
            base.WndProc(ref message);
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
