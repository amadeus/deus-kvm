using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class MacPriorityTests
{
    [Fact]
    public void FirstConnectionKeepsPriorityUntilDisconnectAndRejoinsAtBack()
    {
        var policy = new MacPriority(); policy.CompleteEnumeration();
        policy.Observe("a", "Mac", "01", true);
        var a = policy.Next!;
        Assert.True(policy.Activate(a));
        policy.Observe("b", "Mac", "02", true);
        policy.Observe("a", "Renamed", "01", true);
        Assert.Equal(a.Connection, policy.Next!.Connection);
        policy.Remove("a");
        var b = policy.Next!;
        Assert.Equal("b", b.Id); Assert.True(policy.Activate(b));
        policy.Observe("a", "Mac", "01", true);
        Assert.Equal(b, policy.Next);
        Assert.False(policy.Activate(a));
        policy.Remove("b");
        Assert.Equal("a", policy.Next!.Id);
        Assert.NotEqual(a.Connection, policy.Next.Connection);
    }

    [Theory]
    [InlineData(null, "a")]
    [InlineData("b", "b")]
    [InlineData("offline", "a")]
    public void StartupWaitsForEnumerationThenUsesPreferenceAndStableIdentity(string? preference, string expected)
    {
        var policy = new MacPriority(preference);
        policy.Observe("b", "Same name", "02", true);
        policy.Observe("a", "Same name", "01", true);
        Assert.Null(policy.Next);
        policy.CompleteEnumeration();
        Assert.Equal(expected, policy.Next!.Id);
        policy.CompleteEnumeration();
        Assert.Equal(expected, policy.Next.Id);
    }

    [Fact]
    public void OldPreferenceDoesNotPreemptALaterObservedConnection()
    {
        var policy = new MacPriority("a"); policy.CompleteEnumeration();
        policy.Observe("b", "B", "02", true);
        policy.Observe("a", "A", "01", true);
        Assert.Equal("b", policy.Next!.Id);
    }

    [Fact]
    public void UnsupportedDeviceIsSkippedOnlyForItsCurrentConnection()
    {
        var policy = new MacPriority(); policy.CompleteEnumeration();
        policy.Observe("speaker", "Speaker", "01", true);
        policy.Observe("mac", "Mac", "02", true);
        var speaker = policy.Next!;
        policy.Reject(speaker);
        Assert.False(policy.Activate(speaker));
        Assert.Equal("mac", policy.Next!.Id);
        policy.Remove("speaker");
        policy.Observe("speaker", "Speaker", "01", true);
        policy.Reject(speaker); // stale verification must not reject a new connection
        Assert.Equal(2, policy.Candidates.Length);
    }

    [Fact]
    public void DisconnectBeforeVerificationCannotActivateOrRejectNewConnection()
    {
        var policy = new MacPriority(); policy.CompleteEnumeration();
        policy.Observe("a", "A", "01", true);
        var old = policy.Next!;
        policy.Observe("a", "A", "01", false);
        Assert.Null(policy.Next);
        policy.Observe("a", "A", "01", true);
        Assert.False(policy.IsCurrent(old));
        Assert.False(policy.Activate(old));
        policy.Reject(old);
        Assert.True(policy.Activate(policy.Next!));
    }

    [Fact]
    public void SlowEarlierCandidateCannotBeOvertakenByLaterVerification()
    {
        var policy = new MacPriority(); policy.CompleteEnumeration();
        policy.Observe("a", "A", "01", true);
        policy.Observe("b", "B", "02", true);
        Assert.False(policy.Activate(policy.Candidates[1]));
        Assert.True(policy.Activate(policy.Next!));
    }

    [Fact]
    public void RetryingAnEarlierUnverifiedDeviceCannotStealAnActiveConnection()
    {
        var policy = new MacPriority(); policy.CompleteEnumeration();
        policy.Observe("a", "Unknown device", "01", true);
        var a = policy.Next!;
        policy.Observe("b", "Mac", "02", true);
        policy.Reject(a);
        Assert.True(policy.Activate(policy.Next!));
        policy.Reconsider(a);
        Assert.Equal("b", policy.Next!.Id);
        Assert.False(policy.Activate(a));
        policy.Remove("b");
        Assert.Equal(a.Connection, policy.Next!.Connection);
    }

    [Fact]
    public void InitialRemovalAndStaleReconsiderCannotResurrectAConnection()
    {
        var policy = new MacPriority("a");
        policy.Observe("a", "A", "01", true);
        var a = Assert.Single(policy.Connections);
        policy.Remove("a");
        policy.Reconsider(a);
        policy.CompleteEnumeration();
        Assert.Null(policy.Next);
        Assert.Empty(policy.Connections);
    }

    [Fact]
    public void MigrationAndAddingMacsPreserveStartupPreferenceAndPairingIdentity()
    {
        var a = new CompanionSettings("a", "A") { PairingDeviceId = "classic-a" };
        var settings = AutomaticMacSettings.Migrate(a).Remember(new("b", "B"));
        Assert.Equal("a", settings.LastActiveId);
        Assert.Equal(2, settings.Macs.Length);
        settings = settings.Remember(new("a", "Renamed"), active: true);
        Assert.Equal("classic-a", settings.Macs.Single(item => item.DeviceId == "a").PairingDeviceId);
        settings = settings.Remember(new("b", "B"), active: true);
        settings.Validate();
        Assert.Equal("b", settings.LastActiveId);
        Assert.Equal(2, settings.Macs.Length);
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<AutomaticMacSettings>(
            System.Text.Json.JsonSerializer.Serialize(settings))!;
        roundTrip.Validate();
        Assert.Equal(settings.LastActiveId, roundTrip.LastActiveId);
        Assert.Equal(settings.Macs, roundTrip.Macs);
        Assert.Empty(AutomaticMacSettings.Migrate(null).Macs);
    }
}
