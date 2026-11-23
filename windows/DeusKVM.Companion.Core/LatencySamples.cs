namespace DeusKVM.Companion.Core;

/// <summary>
/// Latency samples in microseconds for one reporting window: exact percentiles over the window
/// (sorted at report time), at most <see cref="Capacity"/> samples kept. Safe from any thread.
/// </summary>
public sealed class LatencySamples
{
    public const int Capacity = 16384;
    private readonly object gate = new();
    private readonly int[] values = new int[Capacity];
    private int count;

    public int Count { get { lock (gate) return count; } }

    public void Add(long micros)
    {
        lock (gate) if (count < Capacity) values[count++] = (int)Math.Max(0, Math.Min(micros, int.MaxValue));
    }

    /// <summary>The window's summary; the window starts over.</summary>
    public LatencySummary Take()
    {
        int[] sorted;
        lock (gate)
        {
            sorted = new int[count];
            Array.Copy(values, sorted, count);
            count = 0;
        }
        if (sorted.Length == 0) return default;
        Array.Sort(sorted);
        int At(int percent) => sorted[(sorted.Length - 1) * percent / 100];
        return new LatencySummary(sorted.Length, At(50), At(90), At(99), sorted[sorted.Length - 1]);
    }

    public static long Micros(long ticks) => ticks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
}

public readonly record struct LatencySummary(int Count, int P50, int P90, int P99, int Max)
{
    public override string ToString() => Count == 0 ? "n=0" : $"n={Count} p50Us={P50} p90Us={P90} p99Us={P99} maxUs={Max}";
}
