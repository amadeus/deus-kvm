namespace DeusKVM.Companion.Core;

// Mac ownership outlives an individual console worker. A worker/display change
// invalidates configuration, while only Mac EXIT or link reset ends ownership.
public sealed class CompanionHandoff
{
    public byte? Active { get; private set; }
    public byte? Edge { get; private set; }
    public EdgeConfiguration? Configuration { get; private set; }

    public void Enter(byte id, byte edge) { Active = id; Edge = edge; }
    public bool Accept(byte id) => Active == id;
    public void Exit() { Active = null; Edge = null; }
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
