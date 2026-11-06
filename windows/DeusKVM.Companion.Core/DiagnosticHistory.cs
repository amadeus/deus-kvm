namespace DeusKVM.Companion.Core;

// Recent edge and desktop decisions, kept only in memory for a problem report requested
// from the Mac. Bounded by entry count and entry length.
public sealed class DiagnosticHistory(int capacity = 100, int maximumLength = 240)
{
    private readonly Queue<string> entries = new();
    private readonly Func<DateTime> now = () => DateTime.UtcNow;

    public DiagnosticHistory(int capacity, int maximumLength, Func<DateTime> now) : this(capacity, maximumLength) => this.now = now;

    public int Count => entries.Count;

    public void Add(string entry)
    {
        var text = entry.Length > maximumLength ? entry.Substring(0, maximumLength) : entry;
        entries.Enqueue($"{now():HH:mm:ss.fff}Z {text}");
        while (entries.Count > Math.Max(1, capacity)) entries.Dequeue();
    }

    // Oldest first.
    public IReadOnlyList<string> Lines() => entries.ToArray();
}
