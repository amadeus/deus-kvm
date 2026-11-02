namespace DeusKVM.Companion.Core;

// The desktop worker reports its state every second over the local pipe (which doubles as its
// keep-alive). Only changes cross Bluetooth to the Mac, plus a slow refresh so a Mac that reset
// its copy converges without a message every second.
public sealed class StateReporter(long refreshMilliseconds = 30000)
{
    private byte[]? last;
    private long sentAt;

    public bool ShouldSend(byte[] state, long now)
    {
        if (last is not null && last.AsSpan().SequenceEqual(state) && now - sentAt < refreshMilliseconds) return false;
        last = (byte[])state.Clone();
        sentAt = now;
        return true;
    }

    // A new connection, HELLO or selection change: the next state always goes out.
    public void Reset() => last = null;
}
