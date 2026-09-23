namespace DeusKVM.Companion.Core;

// Mac ownership outlives an individual console worker. A worker/display change
// invalidates configuration, while only Mac EXIT or link reset ends ownership.
public sealed class CompanionHandoff(Action<bool>? captureChanged = null)
{
    public byte? Active { get; private set; }
    public byte? Edge { get; private set; }
    public EdgeConfiguration? Configuration { get; private set; }

    public void Enter(byte id, byte edge)
    {
        var wasCapturing = Active is not null;
        Active = id; Edge = edge;
        if (!wasCapturing) captureChanged?.Invoke(true);
    }
    public bool Accept(byte id) => Active == id;
    public void Exit()
    {
        var wasCapturing = Active is not null;
        Active = null; Edge = null;
        if (wasCapturing) captureChanged?.Invoke(false);
    }
    public void Reset() { Exit(); DesktopChanged(); }
    public void DesktopChanged() => Configuration = null;

    public bool Configure(EdgeConfiguration next)
    {
        if (Configuration == next) return false;
        Configuration = next;
        return true;
    }

    public DesktopMessage? Resume(bool workerConnected) =>
        workerConnected && Active is { } id && Edge is { } edge && Configuration?.Edge == edge
            ? new DesktopMessage("resume", SwitchId: id, Edge: edge)
            : null;
}
