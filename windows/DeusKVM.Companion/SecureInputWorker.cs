using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using DeusKVM.Companion.Core;
using Microsoft.Win32.SafeHandles;

namespace DeusKVM.Companion;

/// <summary>
/// `--secure-worker`: runs as SYSTEM in the console session, spawned by the service. Injection
/// only. It receives relative motion and button records from the service over a SYSTEM-only
/// pipe and injects them onto whichever desktop currently receives input, so pointer control
/// keeps working on the secure desktop (UAC prompt, lock screen) where the user worker cannot
/// inject. It reports back whether it can inject; the service relays that to the Mac, which
/// falls back to Bluetooth when it cannot. Headless: no window, no message loop.
/// </summary>
internal sealed class SecureInputWorker
{
    private readonly string pipeName;
    private readonly int parent;
    private readonly SecureInjector injector = new();
    private byte lastStatus = 2; // neither ready(1) nor blocked(0) yet

    public SecureInputWorker(string pipeName, int parent)
    {
        this.pipeName = pipeName;
        this.parent = parent;
    }

    public int Run()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(10000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server) || server != parent)
                return 2; // not the service we were told to talk to
            var buffer = new byte[8192];
            var filled = 0;
            while (true)
            {
                var read = pipe.Read(buffer, filled, buffer.Length - filled);
                if (read <= 0) break;
                filled += read;
                var frames = SecureFrame.Parse(buffer, filled, out var consumed);
                foreach (var frame in frames) Apply(frame, pipe);
                if (consumed > 0) { Buffer.BlockCopy(buffer, consumed, buffer, 0, filled - consumed); filled -= consumed; }
                if (filled == buffer.Length) return 3; // a frame larger than the buffer: malformed
            }
            return 0;
        }
        catch (Exception error) when (error is IOException or TimeoutException or ObjectDisposedException or Win32Exception)
        {
            return 1;
        }
        finally { injector.Dispose(); }
    }

    // One batch of records. Each is injected on the current input desktop; the resulting status
    // (could we inject?) is reported to the service only when it changes.
    private void Apply(byte[] body, PipeStream pipe)
    {
        var ok = true;
        var offset = 0;
        while (offset < body.Length)
        {
            var length = SecureInputRecord.Length(body[offset]);
            if (length == 0 || offset + length > body.Length) break;
            if (SecureInputRecord.TryParse(body.AsSpan(offset, length), out var type, out var dx, out var dy, out var button, out var down))
                ok &= type == SecureInputRecord.Motion ? injector.Move(dx, dy) : injector.Button(button, down);
            offset += length;
        }
        var status = (byte)(ok ? 1 : 0);
        if (status == lastStatus) return;
        lastStatus = status;
        try { var frame = SecureFrame.Wrap([status]); pipe.Write(frame, 0, frame.Length); }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}

/// <summary>
/// Injects relative mouse input on the desktop currently receiving input, re-attaching the
/// calling thread only when that desktop changes. A SYSTEM process in the console session can
/// attach to the Winlogon (secure) desktop, which the user's process cannot. Buttons it presses
/// are released when the desktop changes or on dispose, so none is left held on a desktop the
/// worker can no longer reach.
/// </summary>
internal sealed class SecureInjector : IDisposable
{
    private IntPtr desktop = IntPtr.Zero;
    private string current = "";
    private uint held; // MOUSEEVENTF up flags owed on the attached desktop

    public bool Move(int dx, int dy) => Attach() && DesktopNative.InjectMouse(dx, dy, DesktopNative.MouseMove);

    public bool Button(byte button, bool down)
    {
        if (!Attach()) return false;
        var (downFlag, upFlag) = DesktopNative.ButtonFlags(button);
        if (down) held |= upFlag; else held &= ~upFlag;
        return DesktopNative.InjectMouse(0, 0, down ? downFlag : upFlag);
    }

    // Opens the input desktop and, when it is a different one than the thread is attached to,
    // releases any held buttons on the old desktop and attaches the thread to the new one.
    private bool Attach()
    {
        var input = OpenInputDesktop(0, false, Access);
        if (input == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(256);
            if (!GetUserObjectInformation(input, 2, name, name.Capacity * 2, out _)) return false;
            if (name.ToString() == current && desktop != IntPtr.Zero) return true;
            if (held != 0 && desktop != IntPtr.Zero) { DesktopNative.InjectMouse(0, 0, held); held = 0; }
            if (!SetThreadDesktop(input)) return false;
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);
            desktop = input;
            current = name.ToString();
            input = IntPtr.Zero; // kept as `desktop`
            return true;
        }
        finally { if (input != IntPtr.Zero) CloseDesktop(input); }
    }

    public void Dispose()
    {
        if (held != 0 && desktop != IntPtr.Zero) DesktopNative.InjectMouse(0, 0, held);
        if (desktop != IntPtr.Zero) CloseDesktop(desktop);
        desktop = IntPtr.Zero; held = 0;
    }

    // DESKTOP_WRITEOBJECTS | DESKTOP_READOBJECTS | DESKTOP_JOURNALPLAYBACK | DESKTOP_HOOKCONTROL
    private const uint Access = 0x0080 | 0x0001 | 0x0020 | 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int length, out int needed);
}
