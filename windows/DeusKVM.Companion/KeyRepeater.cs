using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeusKVM.Companion;

/// <summary>
/// Repeats the Mac's newest held key, as a keyboard driver would for a real keyboard: Windows does
/// not auto-repeat injected keys. One thread waits for each deadline; the system timer runs at
/// 1 ms only while a key repeats, so a 30 Hz repeat is even instead of snapping to 15.6 ms ticks.
/// <see cref="Start"/> and <see cref="Stop"/> may be called under the caller's lock: the repeat
/// callback runs without this class's lock held.
/// </summary>
internal sealed class KeyRepeater : IDisposable
{
    private readonly Action<KeyStroke, int> repeat;
    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread thread;
    private KeyStroke? key;
    private int serial, periodMs;
    private long due; // Stopwatch ticks
    private bool fine, disposed;

    /// <param name="repeat">Injects one repeat of the key; the int is the serial it was started with.</param>
    public KeyRepeater(Action<KeyStroke, int> repeat)
    {
        this.repeat = repeat;
        thread = new Thread(Run) { IsBackground = true, Name = "DeusKVM key repeat", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    public void Start(KeyStroke stroke, int serial, int delayMs, int periodMs)
    {
        lock (gate)
        {
            key = stroke; this.serial = serial; this.periodMs = Math.Max(1, periodMs);
            due = Stopwatch.GetTimestamp() + delayMs * Stopwatch.Frequency / 1000;
        }
        wake.Set();
    }

    public void Stop()
    {
        lock (gate) key = null;
        wake.Set();
    }

    private void Run()
    {
        while (true)
        {
            KeyStroke stroke; int current;
            int wait;
            lock (gate)
            {
                if (disposed) break;
                if (key is null) { Coarse(); wait = Timeout.Infinite; stroke = default; current = 0; }
                else
                {
                    Fine();
                    var now = Stopwatch.GetTimestamp();
                    wait = (int)Math.Max(0, (due - now) * 1000 / Stopwatch.Frequency);
                    stroke = key.Value; current = serial;
                    if (wait == 0)
                    {
                        var period = periodMs * Stopwatch.Frequency / 1000;
                        // A late wake keeps the cadence; one more than a period late restarts it.
                        due = now - due > period ? now + period : due + period;
                    }
                }
            }
            if (wait != 0) { wake.WaitOne(wait); continue; }
            repeat(stroke, current);
        }
        Coarse();
    }

    private void Fine() { if (!fine) { timeBeginPeriod(1); fine = true; } }
    private void Coarse() { if (fine) { timeEndPeriod(1); fine = false; } }

    public void Dispose()
    {
        lock (gate) { disposed = true; key = null; }
        wake.Set();
        thread.Join(500);
        wake.Dispose();
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
