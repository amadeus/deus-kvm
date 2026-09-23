using System.Runtime.InteropServices;
using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

// Owns only buttons injected by this worker. The pipe reader can release them
// even if the Forms message loop is stalled during shutdown.
internal sealed class DirectPointer
{
    private readonly object gate = new();
    private readonly DirectPointerState state = new();
    private byte buttons;
    private bool shutdown;
    private long lastActivity;
    public bool Active { get { lock (gate) return state.Active is not null; } }
    public void Begin(byte id) { lock (gate) { End(); if (!shutdown && buttons == 0) { state.Begin(id); lastActivity = Environment.TickCount64; } } }
    public void CheckLiveness()
    {
        lock (gate) {
            if (state.Active is not null && Environment.TickCount64 - lastActivity > 3000) End();
            else if (state.Active is null && buttons != 0) End();
        }
    }
    public void Shutdown() { lock (gate) { shutdown = true; End(); } }
    public void End()
    {
        lock (gate)
        {
            state.End();
            // Retain failed releases for a later retry on the Default desktop.
            for (var bit = 0; bit < 3; bit++)
            {
                var mask = (byte)(1 << bit);
                if ((buttons & mask) != 0 && Inject([Button(bit, false)]) == 1) buttons &= (byte)~mask;
            }
        }
    }

    public bool Apply(PointerSample sample, out int dx, out int dy)
    {
        lock (gate)
        {
            dx = dy = 0;
            if (shutdown || !DesktopNative.IsDefaultDesktop() || !DesktopNative.GetCursorPos(out var current) ||
                !state.Move(sample, out dx, out dy)) return false;
            lastActivity = Environment.TickCount64;
            var bounds = SystemInformation.VirtualScreen;
            var x = (int)Math.Clamp((long)current.X + dx, bounds.Left, (long)bounds.Right - 1);
            var y = (int)Math.Clamp((long)current.Y + dy, bounds.Top, (long)bounds.Bottom - 1);
            List<Input> events = [];
            List<byte> after = [];
            if (dx != 0 || dy != 0)
            {
                events.Add(new Input { Mouse = new MouseInput {
                    X = DirectPointerState.Normalize(x, bounds.Left, bounds.Width),
                    Y = DirectPointerState.Normalize(y, bounds.Top, bounds.Height), Flags = 0xC001 } });
                after.Add(buttons);
            }
            var next = buttons;
            for (var bit = 0; bit < 3; bit++)
            {
                var mask = (byte)(1 << bit);
                if ((sample.Buttons & mask) == (next & mask)) continue;
                var down = (sample.Buttons & mask) != 0;
                events.Add(Button(bit, down));
                next = down ? (byte)(next | mask) : (byte)(next & ~mask);
                after.Add(next);
            }
            if (sample.Wheel != 0) { events.Add(Wheel(sample.Wheel, false)); after.Add(next); }
            if (sample.Pan != 0) { events.Add(Wheel(sample.Pan, true)); after.Add(next); }
            if (events.Count == 0) return true;
            var sent = Inject(events.ToArray());
            if (sent > 0 && sent <= after.Count) buttons = after[(int)sent - 1];
            if (sent == events.Count) return true;
            End();
            return false;
        }
    }
    private static Input Button(int bit, bool down) => new() {
        Mouse = new MouseInput { Flags = (down ? 2u : 4u) << (bit * 2) }
    };
    private static Input Wheel(sbyte ticks, bool horizontal) => new() {
        Mouse = new MouseInput { Data = unchecked((uint)(ticks * 120)), Flags = horizontal ? 0x1000u : 0x800u }
    };
    private static uint Inject(Input[] events) => SendInput((uint)events.Length, events, Marshal.SizeOf<Input>());

    // MOUSEINPUT is the largest INPUT union member on both x64 and x86.
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
