namespace DeusKVM.Companion.Core;

public readonly record struct InputFeedback(ulong Capture, ulong Ownership, bool Owns, uint Leave, long LeaveSerial);

/// <summary>One atomic acknowledgement scope: an old leave can never acquire a newer owner's identity.</summary>
public sealed class InputStreamFeedback
{
    private readonly object gate = new();
    private ulong capture, ownership;
    private bool owns;
    private uint leave;
    private long until, serial;
    public void SetOwner(ulong nextCapture, ulong nextOwnership, bool nextOwns)
    {
        lock (gate)
        {
            if (capture == nextCapture && ownership == nextOwnership && owns == nextOwns) return;
            capture = nextCapture; ownership = nextOwnership; owns = nextOwns;
            leave = 0; until = 0;
        }
    }
    public void Leave(uint flags, long deadline)
    {
        lock (gate)
        {
            if (!owns || capture == 0) return;
            leave = flags; until = deadline; serial = checked(serial + 1);
        }
    }
    public InputFeedback Read(long now)
    {
        lock (gate)
            return new(capture, ownership, owns, owns && now < until ? leave : 0, owns && now < until ? serial : 0);
    }
}
