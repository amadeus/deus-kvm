namespace DeusKVM.Companion.Core;

// Resolve new handles immediately. Retry negative results only at device refresh,
// so a transient PnP lookup failure cannot disable return until the next reconnect.
public sealed class MouseIdentityCache
{
    private readonly Dictionary<IntPtr, bool> matches = [];
    public bool HasMatch => matches.Values.Any(value => value);
    public bool Resolve(IntPtr handle, Func<IntPtr, bool> probe)
    {
        if (!matches.TryGetValue(handle, out var result)) matches[handle] = result = probe(handle);
        return result;
    }
    public void Refresh(IReadOnlySet<IntPtr> present, Func<IntPtr, bool> probe)
    {
        foreach (var stale in matches.Keys.Where(key => !present.Contains(key)).ToArray()) matches.Remove(stale);
        var retryMisses = !HasMatch;
        foreach (var handle in present)
            if (!matches.TryGetValue(handle, out var matched) || (retryMisses && !matched)) matches[handle] = probe(handle);
    }
    public void Clear() => matches.Clear();
}
