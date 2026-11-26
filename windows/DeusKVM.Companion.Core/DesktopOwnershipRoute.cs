namespace DeusKVM.Companion.Core;

/// <summary>
/// Service-side lifetime fence for the shared desktop agent. Host incarnations survive queued
/// callbacks; ownership generations are seeded into each new agent. Clipboard readiness also
/// belongs to one endpoint epoch, including when the same Mac remains the controller.
/// </summary>
public sealed class DesktopOwnershipRoute
{
    private uint? clipboardEpoch;
    public long? Owner { get; private set; }
    public long Generation { get; private set; }
    public long AgentGeneration { get; private set; }

    public bool RestartAgent(long hostGeneration)
    {
        if (hostGeneration <= AgentGeneration) return false;
        AgentGeneration = hostGeneration;
        Owner = null; clipboardEpoch = null;
        Generation = checked(Generation + 1);
        return true;
    }

    public bool TrySelect(long source, long stream, long generation, long hostGeneration, Func<long, long, bool> available)
    {
        if (AgentGeneration == 0 || hostGeneration != AgentGeneration || generation <= Generation || source < 0 ||
            source != 0 && !available(source, stream)) return false;
        Owner = source == 0 ? null : source;
        Generation = generation;
        clipboardEpoch = null;
        return true;
    }

    public bool ExpectClipboard(long source, long generation, uint epoch)
    {
        if (Owner != source || generation != Generation) return false;
        clipboardEpoch = epoch;
        return true;
    }

    public bool MatchesClipboard(long source, long generation, uint epoch) =>
        Owner == source && Generation == generation && clipboardEpoch == epoch;
}
