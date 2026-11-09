using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class NetworkInputTests
{
    [Fact]
    public void RecordsParseTheMacsLayout()
    {
        Assert.True(InputRecord.TryParse([1, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0xFE, 0xFF, 0x2C, 0x01], out var motion));
        Assert.True(motion.IsMotion);
        Assert.Equal(0x04030201u, motion.Sequence);
        Assert.Equal(0x08070605u, motion.Captured);
        Assert.Equal(-2, motion.Dx);
        Assert.Equal(300, motion.Dy);
        Assert.True(InputRecord.TryParse([2, 7, 0, 0, 0, 9, 0, 0, 0, 2, 1], out var button));
        Assert.True(button.IsButton);
        Assert.Equal(2, button.Button);
        Assert.True(button.Down);
        Assert.True(InputRecord.TryParse([3, 2, 0, 0, 0, 3, 0, 0, 0], out var ping));
        Assert.Equal(InputRecord.Ping, ping.Type);
        Assert.False(ping.IsMotion);
    }

    [Theory]
    [InlineData(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 4, 1 })]
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2 })]
    [InlineData(new byte[] { 9, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 3, 0, 0 })]
    public void MalformedRecordsAreRejected(byte[] bytes)
    {
        Assert.False(InputRecord.TryParse(bytes, out _));
    }

    [Fact]
    public void AcknowledgementsCarrySequenceAndClock()
    {
        Assert.Equal(new byte[] { 0x10, 0, 0, 0, 0x20, 0, 0, 0 }, InputRecord.Ack(0x10, 0x20));
        Assert.Equal(24, InputRecord.SealedAckLength);
        var first = InputRecord.Clock();
        Thread.Sleep(2);
        Assert.True(unchecked(InputRecord.Clock() - first) >= 10, "the clock counts 100 µs ticks");
    }

    [Fact]
    public void ArrivalStatisticsIgnoreLiftsAndSummarise()
    {
        var stats = new InputArrivalStats();
        stats.Observe(1000); stats.Observe(1015); stats.Observe(1032); stats.Observe(2000); stats.Observe(2014);
        stats.Injected = 4; stats.Discarded = 1;
        Assert.Equal(5, stats.Received);
        Assert.Equal(17, stats.GapMaxMs, 3);
        Assert.Equal("received=5 injected=4 discarded=1 gapAvgMs=15.3 gapMaxMs=17.0", stats.Summary);
        stats.Reset();
        Assert.Equal("received=0 injected=0 discarded=0 gapAvgMs=0.0 gapMaxMs=0.0", stats.Summary);
    }

    [Fact]
    public void InputKeysAreSeparateFromFileKeys()
    {
        var key = new byte[32]; var client = new byte[32]; var server = new byte[32];
        using var file = new FileNetworkCrypto(key, client, server, "server");
        using var input = new FileNetworkCrypto(key, client, server, "server", "input");
        using var inputReader = new FileNetworkCrypto(key, client, server, "server", "input");
        var sealedByInput = input.Seal([1, 2, 3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, inputReader.Open(sealedByInput));
        using var fileReader = new FileNetworkCrypto(key, client, server, "server");
        Assert.ThrowsAny<Exception>(() => fileReader.Open(sealedByInput));
    }
}
