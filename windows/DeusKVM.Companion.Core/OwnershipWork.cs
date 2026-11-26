namespace DeusKVM.Companion.Core;

/// <summary>
/// Orders input behind a controller's clipboard preparation. The caller holds the same lock for
/// this queue, ownership decisions and injection. Replacing a generation discards every pending
/// action, including entry placement; late readiness and native-thread callbacks are harmless.
/// </summary>
public sealed class OwnershipWork(int capacity = 256)
{
    private readonly Queue<Action> waiting = new();
    public long Source { get; private set; }
    public long Generation { get; private set; }
    public bool Ready { get; private set; }
    public int Count => waiting.Count;

    public void Begin(long source, long generation)
    {
        Source = source; Generation = generation; Ready = false; waiting.Clear();
    }
    public bool Current(long source, long generation) => Source == source && Generation == generation;
    public bool Submit(long source, long generation, Action action)
    {
        if (!Current(source, generation)) return false;
        if (Ready) { action(); return true; }
        if (waiting.Count >= capacity) return false;
        waiting.Enqueue(action);
        return true;
    }
    public bool Complete(long source, long generation)
    {
        if (!Current(source, generation)) return false;
        Ready = true;
        while (waiting.Count > 0 && Current(source, generation) && Ready) waiting.Dequeue()();
        return Current(source, generation) && Ready;
    }
    public void DiscardPending() => waiting.Clear();

    public void Cancel() { waiting.Clear(); Ready = false; Source = 0; Generation = checked(Generation + 1); }
}
