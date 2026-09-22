namespace DeusKVM.Companion.Core;

// Mouse input can arrive much faster than desktop/device enumeration can run.
// Keep the first recovery probe immediate, then bound retries during an outage.
public sealed class DesktopRecoveryPoll
{
    private long? lastRefresh;

    public bool ShouldRefresh(bool unavailable, bool activeHandoff, long milliseconds)
    {
        if (!unavailable || !activeHandoff)
        {
            lastRefresh = null;
            return false;
        }
        if (lastRefresh is { } previous && milliseconds - previous < 250) return false;
        lastRefresh = milliseconds;
        return true;
    }
}
