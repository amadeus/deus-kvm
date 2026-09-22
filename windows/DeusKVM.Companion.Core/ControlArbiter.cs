namespace DeusKVM.Companion.Core;

// Windows owns the single grant. A request cannot bypass the previous owner's
// acknowledgement that local capture and held input have been released.
public sealed class ControlArbiter
{
    public long? Owner { get; private set; }
    public long? Requested { get; private set; }
    public long? Releasing { get; private set; }
    public uint ReleaseEpoch { get; private set; }

    public void Request(long connection) => Requested = Owner == connection && Releasing is null ? null : connection;
    public void CancelRequest() => Requested = null;

    public void BeginRelease(uint epoch)
    {
        if (Owner is not { } owner || Releasing is not null) return;
        Releasing = owner; ReleaseEpoch = epoch;
    }

    public bool Acknowledge(long connection, uint epoch)
    {
        if (Releasing != connection || ReleaseEpoch != epoch) return false;
        Owner = Releasing = null;
        return true;
    }

    public void Disconnected(long connection)
    {
        if (Owner == connection) Owner = null;
        if (Releasing == connection) Releasing = null;
        if (Requested == connection) Requested = null;
    }

    public bool Grant(long connection)
    {
        if (Owner is not null || Releasing is not null || Requested is { } requested && requested != connection) return false;
        Owner = connection; Requested = null;
        return true;
    }
}
