using DeusKVM.Companion.Core;
using Xunit;
namespace DeusKVM.Companion.Tests;

public sealed class InputDeliveryHealthTests
{
    [Fact]
    public void LaterSuccessDoesNotEraseDeferredFailureAndProbeAllowsRecovery()
    {
        var health = new InputDeliveryHealth();
        health.Observe(false); health.Observe(true);
        Assert.False(health.Healthy); Assert.False(health.Probe()); Assert.True(health.Probe());
        health.Observe(false); Assert.False(health.Probe());
    }
}
