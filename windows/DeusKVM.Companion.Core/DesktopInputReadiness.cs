namespace DeusKVM.Companion.Core;

public enum SessionLockState { Unknown, Locked, Unlocked }
public readonly record struct InputDesktopAccess(bool Attached, bool Default, bool Winlogon, SessionLockState Session)
{
    public bool Secure => Attached && (Winlogon || Session == SessionLockState.Locked);
    public bool Clipboard => Attached && Default && Session == SessionLockState.Unlocked;
}
public enum InputReadinessReason { DesktopUnavailable, SessionUnavailable, ClipboardPending, Secure, Clipboard, ReleasePending }

/// <summary>
/// The input agent's readiness barrier. A secure desktop needs no user clipboard; an unlocked
/// Default desktop needs a fresh controller clipboard. Pending entry placement survives helper
/// recovery, but never a new capture, cancellation or synthetic input reset.
/// </summary>
public sealed class DesktopInputReadiness
{
    private readonly OwnershipWork work = new();
    private Action? entry;
    private bool clipboardInstalled;
    public long Source => work.Source;
    public long Generation => work.Generation;
    public int Count => work.Count;
    public bool Ready => work.Ready;
    public bool ReadyWithoutClipboard { get; private set; }
    public InputReadinessReason Reason { get; private set; } = InputReadinessReason.DesktopUnavailable;
    public bool Current(long source, long generation) => work.Current(source, generation);

    public void Begin(long source, long generation, Action? placement = null)
    {
        work.Begin(source, generation); clipboardInstalled = ReadyWithoutClipboard = false;
        Reason = InputReadinessReason.DesktopUnavailable;
        entry = placement;
        if (placement is not null) work.Submit(source, generation, () => { entry = null; placement(); });
    }
    public void Renew(long source, long generation) => Begin(source, generation, Source == source ? entry : null);
    public bool ClipboardInstalled(long source, long generation)
    {
        if (!Current(source, generation)) return false;
        clipboardInstalled = true;
        return true;
    }
    public bool Submit(long source, long generation, Action action) => work.Submit(source, generation, action);

    /// <summary>Called before native input, and when the attached desktop changes.</summary>
    public bool NeedsRenewal(InputDesktopAccess access)
    {
        if (access.Secure) { if (Ready) ReadyWithoutClipboard = true; return false; }
        // A failed native callback can make the queue pending again after a secure grant. Its
        // unlock obligation still exists, even though the entry has not been acknowledged yet.
        return access.Attached && ReadyWithoutClipboard;
    }

    public bool Complete(long source, long generation, InputDesktopAccess access, Func<bool> prepare, Func<bool>? beforeAction = null)
    {
        if (!Current(source, generation)) return false;
        if (Ready) return true;
        Reason = !access.Attached ? InputReadinessReason.DesktopUnavailable
            : access.Secure ? InputReadinessReason.Secure
            : !access.Clipboard ? InputReadinessReason.SessionUnavailable
            : !clipboardInstalled ? InputReadinessReason.ClipboardPending : InputReadinessReason.Clipboard;
        if (Reason is not (InputReadinessReason.Secure or InputReadinessReason.Clipboard)) return false;
        if (!prepare()) { Reason = InputReadinessReason.ReleasePending; return false; }
        ReadyWithoutClipboard = Reason == InputReadinessReason.Secure;
        return work.Complete(source, generation, beforeAction);
    }

    public void DiscardPending() { entry = null; work.DiscardPending(); }
    public void Cancel() { entry = null; clipboardInstalled = ReadyWithoutClipboard = false; work.Cancel(); }
}
