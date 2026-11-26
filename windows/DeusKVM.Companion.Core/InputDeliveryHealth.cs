namespace DeusKVM.Companion.Core;

/// <summary>A deferred batch retains any injection failure until the next health probe reports it.</summary>
public sealed class InputDeliveryHealth
{
    public bool Healthy { get; private set; } = true;
    public void Observe(bool succeeded) => Healthy &= succeeded;
    public bool Probe()
    {
        var result = Healthy;
        Healthy = true;
        return result;
    }
}
