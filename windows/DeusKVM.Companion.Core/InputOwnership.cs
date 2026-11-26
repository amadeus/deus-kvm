namespace DeusKVM.Companion.Core;

public enum InputDisposition { Ignore, Passive, Forward, Reset }

/// <summary>
/// A decision made before injection. A changed owner requires releasing all previously injected
/// input first. RestoreModifiers contains only held modifiers: ordinary held keys and buttons
/// must never be synthesized on acquisition (a lock key or a drag would be repeated).
/// </summary>
public readonly record struct InputAdmission(InputDisposition Disposition, InputEvent Record,
    bool OwnershipChanged, long Generation, byte RestoreModifiers = 0);

/// <summary>
/// Arbitration for one Windows desktop. Callers serialize this with injection. Source ids identify
/// control connections; stream ids fence callbacks from replaced input connections. Each source
/// independently enters/exits PC mode. Only an entry or changed user input chooses the owner.
/// </summary>
public sealed class InputOwnership(long initialGeneration = 0)
{
    private sealed class Source(long stream)
    {
        public long Stream { get; } = stream;
        public byte? Switch { get; set; }
        public ulong Capture { get; set; }
        public byte Modifiers { get; set; }
        public HashSet<byte> Keys { get; } = [];
        public HashSet<byte> Suppressed { get; } = [];
        public HashSet<byte> Buttons { get; } = [];
        public ushort Consumer { get; set; }
        public void Clear()
        {
            Modifiers = 0; Keys.Clear(); Suppressed.Clear(); Buttons.Clear(); Consumer = 0;
        }
    }

    private readonly Dictionary<long, Source> sources = [];
    public long? Owner { get; private set; }
    public long Generation { get; private set; } = initialGeneration;
    public byte? OwnerSwitch => Owner is { } owner && sources.TryGetValue(owner, out var source) ? source.Switch : null;

    public void Renew() => Generation = checked(Generation + 1);

    public void Connect(long source, long stream)
    {
        if (sources.TryGetValue(source, out var previous) && previous.Stream == stream) return;
        if (Owner == source) Select(null);
        sources[source] = new Source(stream);
    }

    public bool IsCurrent(long source, long stream) => sources.TryGetValue(source, out var state) && state.Stream == stream;
    public bool IsRemote(long source, long stream) => Current(source, stream)?.Switch is not null;

    public bool Enter(long source, long stream, byte switchId, ulong capture)
    {
        if (capture == 0 || Current(source, stream) is not { } state) return false;
        var fresh = state.Capture != capture;
        if (fresh) state.Clear();
        state.Switch = switchId; state.Capture = capture;
        if (Owner == source && fresh) Generation = checked(Generation + 1);
        Acquire(source, state, []);
        return true;
    }

    /// <summary>Restores remote metadata after helper startup; background recovery never claims control.</summary>
    public bool Resume(long source, long stream, byte switchId, ulong capture)
    {
        if (capture == 0 || Current(source, stream) is not { } state) return false;
        if (state.Capture != 0 && state.Capture != capture) return false;
        state.Switch = switchId; state.Capture = capture;
        return true;
    }

    public bool Exit(long source, long stream, byte switchId, ulong capture)
    {
        if (Current(source, stream) is not { } state || state.Switch != switchId || state.Capture != capture) return false;
        state.Switch = null; state.Capture = 0; state.Clear();
        // Keep the last controller as clipboard partner after returning locally.
        return true;
    }

    public bool Disconnect(long source, long stream)
    {
        if (!IsCurrent(source, stream)) return false;
        sources.Remove(source);
        if (Owner == source) Select(null);
        return true;
    }

    public InputAdmission Observe(long source, long stream, InputEvent record)
    {
        var before = Generation;
        if (Current(source, stream) is not { } state) return Decision(InputDisposition.Ignore);
        if (record.Type == InputRecord.Ping) return Decision(InputDisposition.Passive);
        if (state.Switch is null || record.Capture != state.Capture) return Decision(InputDisposition.Ignore);
        if (record.Type == InputRecord.Reset)
        {
            state.Clear();
            return Decision(Owner == source ? InputDisposition.Reset : InputDisposition.Passive);
        }

        var pressed = new HashSet<byte>();
        bool changed;
        if (record.IsKeyboard)
        {
            var keys = new HashSet<byte>(record.Keys ?? []);
            changed = state.Modifiers != record.Modifiers || !state.Keys.SetEquals(keys);
            pressed.UnionWith(keys.Except(state.Keys));
            state.Suppressed.IntersectWith(keys);
            state.Keys.Clear(); state.Keys.UnionWith(keys); state.Modifiers = record.Modifiers;
        }
        else if (record.IsConsumer)
        {
            changed = state.Consumer != record.Usage;
            state.Consumer = record.Usage;
        }
        else if (record.IsButton) changed = record.Down ? state.Buttons.Add(record.Button) : state.Buttons.Remove(record.Button);
        else if (record.IsMotion) changed = record.Dx != 0 || record.Dy != 0;
        else if (record.IsScroll) changed = record.Wheel != 0 || record.Pan != 0;
        else return Decision(InputDisposition.Ignore);

        if (!changed) return Decision(InputDisposition.Passive);
        var acquired = Owner != source;
        if (acquired) Acquire(source, state, pressed);
        if (record.IsKeyboard) record = record with { Keys = state.Keys.Except(state.Suppressed).OrderBy(key => key).ToArray() };
        return Decision(InputDisposition.Forward, acquired ? state.Modifiers : (byte)0);

        InputAdmission Decision(InputDisposition disposition, byte modifiers = 0) =>
            new(disposition, record, before != Generation, Generation, modifiers);
    }

    private Source? Current(long source, long stream) =>
        sources.TryGetValue(source, out var state) && state.Stream == stream ? state : null;

    private void Acquire(long source, Source state, IEnumerable<byte> pressed)
    {
        if (Owner == source) return;
        state.Suppressed.Clear(); state.Suppressed.UnionWith(state.Keys.Except(pressed));
        Select(source);
    }

    private void Select(long? source)
    {
        if (Owner == source) return;
        Owner = source;
        Generation = checked(Generation + 1);
    }
}
