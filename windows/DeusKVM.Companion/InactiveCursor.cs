using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DeusKVM.Companion;

/// <summary>
/// The session agent's cursor hiding while the Mac is in control: on Winlogon, which holds the
/// login and lock screens and UAC prompts (a surface on <see cref="WinlogonCursor"/>'s thread),
/// and on the user's Default desktop only while no user helper does it (the clipboard helper
/// hides it there, as the user: see ClipboardWorker). Leaving a desktop reveals its cursor.
/// </summary>
internal sealed class CursorHider : IDisposable
{
    private readonly InactiveCursor user = new(0);
    private readonly WinlogonCursor secure = new();
    public bool Hidden => user.Hidden || secure.Hidden;

    /// <param name="park">Where the hidden cursor waits (the screen's center), or null to leave it in place.</param>
    /// <param name="userHides">The user's own helper hides it on the Default desktop.</param>
    public void Hide((int X, int Y)? park = null, bool userHides = false)
    {
        if (DesktopNative.DesktopState().Desktop == 1) secure.Hide(park);
        else if (!userHides) user.Hide(park);
    }

    public void Reveal() { user.Reveal(); secure.Reveal(); }

    /// <summary>The input desktop is now <paramref name="desktop"/>: the other one's cursor is revealed.</summary>
    public void Follow(byte desktop)
    {
        if (desktop != 0) user.Reveal();
        if (desktop != 1) secure.Reveal();
    }

    public void Dispose() { user.Dispose(); secure.Dispose(); }
}

/// <summary>
/// An <see cref="InactiveCursor"/> on the Winlogon desktop. A window belongs to its thread's
/// desktop, so this thread attaches to Winlogon before creating it (SYSTEM may) and runs its own
/// message loop; requests are posted to it.
/// </summary>
internal sealed class WinlogonCursor : IDisposable
{
    private const uint WakeMessage = 0x8001, QuitMessage = 0x0012; // WM_APP + 1, WM_QUIT
    private readonly ConcurrentQueue<(bool Hide, (int X, int Y)? Park)> requests = new();
    private readonly ManualResetEventSlim ready = new();
    private readonly Thread thread;
    private volatile uint threadId;
    private volatile bool hidden;
    public bool Hidden => hidden;

    public WinlogonCursor()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "DeusKVM Winlogon cursor" };
        thread.Start();
        ready.Wait(2000);
    }

    public void Hide((int X, int Y)? park) => Post(true, park);
    public void Reveal() { if (hidden) Post(false, null); }

    private void Post(bool hide, (int X, int Y)? park)
    {
        requests.Enqueue((hide, park));
        if (threadId != 0) PostThreadMessage(threadId, WakeMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        InactiveCursor? cursor = null;
        try
        {
            // Attach before any window exists on this thread (SetThreadDesktop fails afterward).
            var desktop = OpenDesktop("Winlogon", 0, false, 0x02000000); // MAXIMUM_ALLOWED
            if (desktop == IntPtr.Zero || !SetThreadDesktop(desktop)) return;
            cursor = new InactiveCursor(1);
            threadId = GetCurrentThreadId();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return; }
        finally { ready.Set(); }
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.Window == IntPtr.Zero && message.Message == WakeMessage)
            {
                while (requests.TryDequeue(out var request)) { if (request.Hide) cursor.Hide(request.Park); else cursor.Reveal(); }
            }
            else { TranslateMessage(ref message); DispatchMessage(ref message); }
            hidden = cursor.Hidden; // a click (its hook) reveals it too
        }
        cursor.Dispose();
    }

    public void Dispose()
    {
        if (threadId != 0) PostThreadMessage(threadId, QuitMessage, IntPtr.Zero, IntPtr.Zero);
        thread.Join(1000);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr Window; public uint Message; public IntPtr WParam, LParam; public uint Time; public DesktopNative.Point Point; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenDesktop(string name, uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg message);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg message);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
}

// A temporary, nonactivating surface under the pointer owns the invisible cursor, on one desktop
// (0 Default, 1 Winlogon: the thread that creates it must be on that desktop). No system cursor
// scheme changes or shared ShowCursor counters survive it.
internal sealed class InactiveCursor : NativeWindow, IDisposable
{
    private readonly MouseHook mouseHook;
    private readonly byte desktop;
    private IntPtr hook;
    public bool Hidden { get; private set; }

    public InactiveCursor(byte desktop)
    {
        this.desktop = desktop;
        mouseHook = OnMouse;
        CreateHandle(new CreateParams
        {
            Caption = "DeusKVM inactive cursor",
            Style = unchecked((int)0x80000000), // WS_POPUP
            ExStyle = 0x08080088, // NOACTIVATE | LAYERED | TOOLWINDOW | TOPMOST
            Width = 1,
            Height = 1
        });
        // Nonzero alpha keeps the one-pixel surface hit-testable without a visible window.
        SetLayeredWindowAttributes(Handle, 0, 1, 2);
    }

    public void Hide((int X, int Y)? park = null)
    {
        if (Hidden || DesktopNative.DesktopState().Desktop != desktop || !DesktopNative.GetCursorPos(out var point)) return;
        if (park is { } at) point = new DesktopNative.Point { X = at.X, Y = at.Y };
        // Reveal before a click is routed so the hider cannot eat the first click.
        // Raw Input supplies device-specific movement; this hook never consumes input.
        hook = SetWindowsHookEx(14, mouseHook, GetModuleHandle(null), 0); // WH_MOUSE_LL
        if (hook == IntPtr.Zero) return;
        Hidden = true;
        if (!SetWindowPos(Handle, new IntPtr(-1), point.X, point.Y, 1, 1, 0x50)) // NOACTIVATE | SHOWWINDOW
        { Reveal(); return; }
        // Parked away from where the Mac left it (often the taskbar, at the edge), the cursor ends
        // any hover or tooltip there; it lands on this surface, so nothing beneath sees it arrive.
        // A real (injected) move, not SetCursorPos, so a tooltip still pending is cancelled too.
        if (park is not null && !DesktopNative.MoveCursorTo(point.X, point.Y)) DesktopNative.SetCursorPos(point.X, point.Y);
        SetCursor(IntPtr.Zero);
    }

    public void Reveal()
    {
        if (!Hidden) return;
        Hidden = false;
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x97); // HIDEWINDOW | NOACTIVATE | NOZORDER | NOMOVE | NOSIZE
        if (!DesktopNative.GetCursorPos(out var point)) return;
        // Ask the underlying app for its current cursor (I-beam, resize, etc.).
        // The fallback also restores visibility if that app cannot respond.
        SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(32512))); // IDC_ARROW
        var target = WindowFromPoint(point);
        if (target != IntPtr.Zero)
            SendNotifyMessage(target, 0x20, target, new IntPtr(1)); // WM_SETCURSOR, HTCLIENT
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && Hidden && message.ToInt64() is 0x201 or 0x204 or 0x207 or 0x20B or 0x20A or 0x20E)
            Reveal();
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x20 && Hidden) // WM_SETCURSOR
        {
            SetCursor(IntPtr.Zero);
            message.Result = new IntPtr(1);
            return;
        }
        if (message.Msg == 0x21) // WM_MOUSEACTIVATE: never take keyboard focus
        {
            message.Result = new IntPtr(3); // MA_NOACTIVATE (do not discard the click)
            return;
        }
        base.WndProc(ref message);
    }

    public void Dispose() { Reveal(); DestroyHandle(); }

    private delegate IntPtr MouseHook(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int id, MouseHook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(DesktopNative.Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendNotifyMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
