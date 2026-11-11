using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using DeusKVM.Companion.Core;
using Microsoft.Win32.SafeHandles;

namespace DeusKVM.Companion;

/// <summary>
/// `--secure-worker`: runs as SYSTEM in the console session, spawned by the service. Injection
/// only. It receives every network pointer record (relative motion, buttons) from the service over
/// a SYSTEM-only pipe and injects it on whichever desktop currently receives input: the user's
/// desktop, including behind elevated windows, and the secure desktop (UAC prompt, lock screen),
/// where the user's own process cannot inject. It follows desktop switches itself, holding records
/// it could not inject while the switch is under way (up to 300 ms) and replaying them once it
/// can. It reports whether it can inject, and every ten seconds what the path cost; the service
/// relays both to the desktop worker, which falls back to injecting itself (or the Mac to
/// Bluetooth) while this worker cannot. Headless: no window, no message loop.
/// </summary>
internal sealed class SecureInputWorker
{
    private readonly string pipeName;
    private readonly int parent;
    private readonly SecureInjector injector = new();
    private byte lastStatus = 2; // neither ready(1) nor blocked(0) yet
    // While failing, the failure is repeated at this interval: the desktop worker must never be left
    // believing in a relay that stopped injecting.
    private static readonly long FailReportTicks = Stopwatch.Frequency / 10;
    private long lastFailReport;
    // A pause in records starts a new burst; its first batch always reports, even if the status
    // matches the end of the last one.
    private const long EpisodeGapMs = 500;
    private long lastBatch = long.MinValue / 2;
    // Records not yet injected, oldest first, while injection fails (a desktop switch under way).
    // Kept a little longer than the desktop worker's 250 ms grace: past that it has fallen back
    // (to injecting itself, or the Mac to Bluetooth), and replaying older steps would repeat them.
    private const int OwedLimit = 512;
    private static readonly long OwedTicks = Stopwatch.Frequency * 3 / 10;
    private readonly Queue<byte[]> owed = new();
    private long failingSince; // Stopwatch ticks of the first failure in the current outage, or 0
    // Measurements for the current ten-second window.
    private static readonly long ReportTicks = Stopwatch.Frequency * 10;
    private readonly LatencySamples transit = new(), inject = new(), total = new();
    private long mark, lastReport = Stopwatch.GetTimestamp();
    private int failed, longestFailMs, replayed;

    public SecureInputWorker(string pipeName, int parent)
    {
        this.pipeName = pipeName;
        this.parent = parent;
    }

    // SetThreadDesktop fails on a thread that owns any window, and the [STAThread] main thread
    // has COM's hidden apartment window: the worker could follow the input desktop to the user's
    // desktop it started on, but never to the secure desktop. It runs on a fresh MTA thread instead.
    public int Run()
    {
        var code = 1;
        var thread = new Thread(() => code = Serve()) { Name = "DeusKVM secure injector", Priority = ThreadPriority.AboveNormal };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return code;
    }

    private int Serve()
    {
        try
        {
            // Synchronous handle: reads return on this thread instead of through the thread pool.
            // Reads and the status writes both happen on this thread, so they never contend.
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(10000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server) || server != parent)
                return 2; // not the service we were told to talk to
            var buffer = new byte[8192];
            var filled = 0;
            while (true)
            {
                var read = pipe.Read(buffer, filled, buffer.Length - filled);
                if (read <= 0) break;
                var received = Stopwatch.GetTimestamp();
                filled += read;
                var frames = SecureFrame.Parse(buffer, filled, out var consumed);
                foreach (var frame in frames) Apply(frame, received, pipe);
                if (consumed > 0) { Buffer.BlockCopy(buffer, consumed, buffer, 0, filled - consumed); filled -= consumed; }
                if (filled == buffer.Length) return 3; // a frame larger than the buffer: malformed
                if (received - lastReport >= ReportTicks) Report(pipe, received);
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
    // (could we inject?) is reported to the service when it changes or a new burst starts.
    private void Apply(byte[] body, long received, PipeStream pipe)
    {
        var now = RuntimeCompat.TickCount64;
        if (now - lastBatch > EpisodeGapMs) lastStatus = 2;
        lastBatch = now;
        var ok = true;
        var injected = false;
        var offset = 0;
        while (offset < body.Length)
        {
            var length = SecureInputRecord.Length(body[offset]);
            if (length == 0 || offset + length > body.Length) break;
            var record = body.AsSpan(offset, length);
            offset += length;
            switch (record[0])
            {
                case SecureInputRecord.Mark:
                    var arrived = SecureInputRecord.ReadMark(record);
                    if (arrived == 0) break;
                    transit.Add(LatencySamples.Micros(received - arrived));
                    if (mark == 0) mark = arrived; // the oldest batch not yet injected
                    break;
                case SecureInputRecord.Release:
                    injector.ReleaseAll();
                    break;
                default:
                    if (Deliver(record.ToArray())) injected = true; else ok = false;
                    break;
            }
        }
        if (injected && mark != 0) { total.Add(LatencySamples.Micros(Stopwatch.GetTimestamp() - mark)); mark = 0; }
        var status = (byte)(ok ? 1 : 0);
        var stamp = Stopwatch.GetTimestamp();
        if (status == lastStatus && (ok || stamp - lastFailReport < FailReportTicks)) return;
        lastStatus = status;
        if (!ok) lastFailReport = stamp;
        // A failure carries what failed and its Win32 error: [0, step, error (i32)].
        byte[] report = ok ? [1] : [0, injector.FailedStep, 0, 0, 0, 0];
        if (!ok) System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(report.AsSpan(2), injector.FailedError);
        try { var frame = SecureFrame.Wrap(report); pipe.Write(frame, 0, frame.Length); }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    // Injects a record after any still owed from an outage, in order. False leaves it owed.
    private bool Deliver(byte[] record)
    {
        var now = Stopwatch.GetTimestamp();
        if (failingSince != 0 && now - failingSince > OwedTicks) { failed += owed.Count; owed.Clear(); } // too old to replay
        if (owed.Count >= OwedLimit) { owed.Dequeue(); failed++; }
        owed.Enqueue(record);
        var backlog = owed.Count > 1;
        while (owed.Count > 0)
        {
            var start = Stopwatch.GetTimestamp();
            if (!InjectOne(owed.Peek()))
            {
                if (failingSince == 0) failingSince = start;
                return false;
            }
            inject.Add(LatencySamples.Micros(Stopwatch.GetTimestamp() - start));
            owed.Dequeue();
            if (backlog) replayed++;
        }
        if (failingSince != 0)
        {
            longestFailMs = Math.Max(longestFailMs, (int)((now - failingSince) * 1000 / Stopwatch.Frequency));
            failingSince = 0;
        }
        return true;
    }

    private bool InjectOne(byte[] record) =>
        SecureInputRecord.TryParse(record, out var type, out var dx, out var dy, out var button, out var down) &&
        (type == SecureInputRecord.Motion ? injector.Move(dx, dy) : injector.Button(button, down));

    // The window's measurements, back to the service (and on to the desktop worker's log).
    private void Report(PipeStream pipe, long now)
    {
        lastReport = now;
        var stats = new SecureRelayStats(transit.Take(), inject.Take(), total.Take(), failed, longestFailMs,
            injector.TakeSwitches(), replayed);
        failed = longestFailMs = replayed = 0;
        if (stats.Inject.Count == 0 && stats.Failed == 0 && stats.Switches == 0) return;
        try { var frame = SecureFrame.Wrap(stats.Encode()); pipe.Write(frame, 0, frame.Length); }
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
    private int switches;

    public int TakeSwitches() { var value = switches; switches = 0; return value; }

    // Lets go of every button this worker pressed (the stream or the desktop worker ended).
    public void ReleaseAll()
    {
        if (held != 0 && desktop != IntPtr.Zero) DesktopNative.InjectMouse(0, 0, held);
        held = 0;
    }

    // The step that last failed (1 OpenInputDesktop, 2 GetUserObjectInformation, 3 SetThreadDesktop,
    // 4 SendInput) and its Win32 error.
    public byte FailedStep { get; private set; }
    public int FailedError { get; private set; }

    private bool Fail(byte step)
    {
        FailedStep = step;
        FailedError = Marshal.GetLastWin32Error();
        return false;
    }

    private bool Send(int dx, int dy, uint flags) => DesktopNative.InjectMouse(dx, dy, flags) || Fail(4);

    public bool Move(int dx, int dy) => Attach() && Send(dx, dy, DesktopNative.MouseMove);

    public bool Button(byte button, bool down)
    {
        if (!Attach()) return false;
        var (downFlag, upFlag) = DesktopNative.ButtonFlags(button);
        if (down) held |= upFlag; else held &= ~upFlag;
        return Send(0, 0, down ? downFlag : upFlag);
    }

    // Opens the input desktop and, when it is a different one than the thread is attached to,
    // releases any held buttons on the old desktop and attaches the thread to the new one.
    private bool Attach()
    {
        var input = OpenInputDesktop(0, false, Access);
        if (input == IntPtr.Zero) return Fail(1);
        try
        {
            var name = new StringBuilder(256);
            if (!GetUserObjectInformation(input, 2, name, name.Capacity * 2, out _)) return Fail(2);
            if (name.ToString() == current && desktop != IntPtr.Zero) return true;
            if (held != 0 && desktop != IntPtr.Zero) { DesktopNative.InjectMouse(0, 0, held); held = 0; }
            if (!SetThreadDesktop(input)) return Fail(3);
            if (desktop != IntPtr.Zero) { CloseDesktop(desktop); switches++; }
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
