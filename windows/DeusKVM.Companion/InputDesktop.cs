using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace DeusKVM.Companion;

/// <summary>
/// Follows the desktop that currently receives input (the user's Default desktop, or Winlogon
/// for the login screen, lock screen and UAC prompts). SendInput, GetCursorPos and SetCursorPos
/// act only on the calling thread's desktop, so each thread that uses them attaches itself here
/// first; the session agent runs as SYSTEM in the console session, which may attach to every one
/// of them. SetThreadDesktop fails on a thread that owns a window (or COM's hidden STA window), so
/// only windowless threads may call <see cref="Attach"/>: the stream's receive thread, the key
/// repeat thread and <see cref="InputDesktopThread"/>.
/// </summary>
internal static class InputDesktop
{
    [ThreadStatic] private static IntPtr attached;
    [ThreadStatic] private static string? attachedName;

    /// <summary>The desktop the calling thread is attached to, after the last <see cref="Attach"/>.</summary>
    public static string? Current => attachedName;

    /// <summary>
    /// Attaches the calling thread to the input desktop if it changed. Returns false when the
    /// input desktop cannot be opened or attached (the last Win32 error says why).
    /// </summary>
    public static bool Attach()
    {
        var input = OpenInputDesktop(0, false, Access);
        if (input == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(256);
            if (!GetUserObjectInformation(input, 2, name, name.Capacity * 2, out _)) return false;
            if (attached != IntPtr.Zero && name.ToString() == attachedName) return true;
            if (!SetThreadDesktop(input)) return false;
            if (attached != IntPtr.Zero) CloseDesktop(attached);
            attached = input;
            attachedName = name.ToString();
            input = IntPtr.Zero; // kept as `attached`
            return true;
        }
        finally { if (input != IntPtr.Zero) CloseDesktop(input); }
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

/// <summary>
/// A windowless MTA thread for work that must run on the input desktop but is requested from the
/// agent's window thread (which owns windows and so cannot follow the input desktop): cursor
/// placement on entry, releasing held input, the cursor line of a problem report.
/// </summary>
internal sealed class InputDesktopThread : IDisposable
{
    private readonly BlockingCollection<Action> work = new(64);
    private readonly Thread thread;

    public InputDesktopThread()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "DeusKVM input desktop" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    /// <summary>Runs <paramref name="action"/> on this thread, attached to the input desktop.</summary>
    public void Post(Action action)
    {
        if (!work.IsAddingCompleted) work.TryAdd(action);
    }

    private void Run()
    {
        try
        {
            foreach (var action in work.GetConsumingEnumerable())
            {
                InputDesktop.Attach();
                try { action(); }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        work.CompleteAdding();
        thread.Join(1000);
    }
}
