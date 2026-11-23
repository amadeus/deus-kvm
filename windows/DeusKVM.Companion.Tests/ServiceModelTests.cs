using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class ServiceModelTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 5)]
    [InlineData(3, 10)]
    [InlineData(4, 20)]
    [InlineData(100, 30)]
    public void ReconnectBackoffIsBounded(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), ServicePolicy.RetryDelay(failures));

    [Fact]
    public void StaleStatusCannotLookLikeAWorkingConnection()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(ServicePolicy.IsFresh(now.AddSeconds(-10), now));
        Assert.False(ServicePolicy.IsFresh(now.AddMinutes(-1), now));
        Assert.False(ServicePolicy.IsFresh(now.AddHours(1), now));
    }

    [Theory]
    [InlineData("", "Mac")]
    [InlineData("device\nother", "Mac")]
    [InlineData("endpoint", "")]
    public void InvalidDeviceSelectionIsRejected(string id, string name) =>
        Assert.Throws<ArgumentException>(() => new CompanionSettings(id, name).Validate());

    [Fact]
    public void ConfigurationRoundTripPreservesIdAndUnicodeName()
    {
        var folder = Path.Combine(Path.GetTempPath(), "deuskvm-test-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "settings.json");
            var value = new CompanionSettings("fa988199-ba3a-c4cb-8620-946dd087f60c", "Amadeus’s Mac");
            value.Validate();
            JsonFiles.Write(path, value);
            Assert.Equal(value, JsonFiles.Read<CompanionSettings>(path));
            var updated = value with { DeviceName = "Renamed Mac" };
            JsonFiles.Write(path, updated);
            Assert.Equal(updated, JsonFiles.Read<CompanionSettings>(path));
            Assert.False(File.Exists(path + ".new"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
    [Fact]
    public void AnOlderFileWithItsExtraFieldStillParses()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<CompanionSettings>(
            "{\"DeviceId\":\"old-endpoint\",\"DeviceName\":\"Mac\",\"PairingDeviceId\":\"classic\"}")!;
        settings.Validate();
        Assert.Equal("old-endpoint", settings.DeviceId);
    }
}
