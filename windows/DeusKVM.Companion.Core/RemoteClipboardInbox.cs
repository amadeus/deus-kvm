namespace DeusKVM.Companion.Core;

// The pipe selects a lease before the clipboard STA processes its lifecycle command.
// Keep that lease's latest snapshot until the STA has processed the same selection.
public sealed class RemoteClipboardInbox<T> where T : class
{
    private readonly object gate = new();
    private RemoteClipboardLease? selected;
    private T? pending;

    public void Select(RemoteClipboardLease lease)
    {
        lock (gate) { selected = lease; pending = null; }
    }

    public void Publish(RemoteClipboardLease lease, T value)
    {
        lock (gate)
            if (ReferenceEquals(selected, lease) && lease.Valid) pending = value;
    }

    public T? Take(RemoteClipboardLease? lease)
    {
        lock (gate)
        {
            if (lease is null || !ReferenceEquals(selected, lease) || !lease.Valid) return null;
            var value = pending; pending = null;
            return value;
        }
    }

    public void Clear()
    {
        lock (gate) pending = null;
    }
}
