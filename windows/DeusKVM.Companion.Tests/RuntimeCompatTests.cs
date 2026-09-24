using System.Net;
using System.Net.Sockets;
using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class RuntimeCompatTests
{
    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("C:\\folder\\", "\"C:\\folder\\\\\"")]
    public void WindowsArgumentsPreserveSpacesQuotesAndTrailingSlashes(string argument, string expected) =>
        Assert.Equal(expected, RuntimeCompat.QuoteArgument(argument));

    [Fact]
    public void HkdfMatchesFirstBlockOfRfc5869CaseOne()
    {
        var key = RuntimeCompat.DeriveFileKey(Enumerable.Repeat((byte)0x0b, 22).ToArray(),
            TestBytes.FromHex("000102030405060708090a0b0c"), TestBytes.FromHex("f0f1f2f3f4f5f6f7f8f9"));
        Assert.Equal(TestBytes.FromHex("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf"), key);
    }
    [Fact]
    public async Task ExactReadsHandlePartialReadsAndDetectTruncation()
    {
        using var stream = new ShortStream(new byte[] { 1, 2, 3, 4, 5 });
        var bytes = new byte[5];
        await RuntimeCompat.ReadExactlyAsync(stream, bytes, CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, bytes);
        await Assert.ThrowsAsync<EndOfStreamException>(() => RuntimeCompat.ReadExactlyAsync(stream, new byte[1], CancellationToken.None));
    }
    [Fact]
    public async Task CancelingAStalledSocketReadUnblocksWithoutPolling()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            using var client = new TcpClient();
            var accept = listener.AcceptTcpClientAsync();
            await RuntimeCompat.ConnectAsync(client, IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, CancellationToken.None);
            using var server = await accept;
            using var stop = new CancellationTokenSource();
            var read = RuntimeCompat.ReadExactlyAsync(client.GetStream(), new byte[4], stop.Token);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RuntimeCompat.WaitAsync(read, TimeSpan.FromSeconds(2)));
        }
        finally { listener.Stop(); }
    }
    [Fact]
    public async Task TaskDeadlineDistinguishesCancellationTimeoutAndFailure()
    {
        var pending = new TaskCompletionSource<bool>();
        await Assert.ThrowsAsync<TimeoutException>(() => RuntimeCompat.WaitAsync(pending.Task, TimeSpan.Zero));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RuntimeCompat.WaitAsync(pending.Task, TimeSpan.FromSeconds(1), stop.Token));
        await Assert.ThrowsAsync<IOException>(() => RuntimeCompat.WaitAsync(Task.FromException(new IOException()), TimeSpan.FromSeconds(1)));
    }
    private sealed class ShortStream(byte[] data) : MemoryStream(data)
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken stop) =>
            base.ReadAsync(buffer, offset, Math.Min(count, 2), stop);
    }
}

internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (Environment.OSVersion.Platform != PlatformID.Win32NT) Skip = "Windows CNG runtime required"; }
}

public sealed class WindowsGcmTests
{
    [WindowsFact]
    public void EmptyAes256RecordMatchesKnownTagAndRejectsTampering()
    {
        using var aes = new WindowsGcm(new byte[32]);
        var tag = new byte[16]; aes.Encrypt(new byte[12], [], [], tag);
        Assert.Equal(TestBytes.FromHex("530f8afbc74536b9a963b4f1c4cb738b"), tag);
        aes.Decrypt(new byte[12], [], tag, []);
        tag[0] ^= 1;
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => aes.Decrypt(new byte[12], [], tag, []));
    }
    [WindowsFact]
    public void FullNetworkBlockRoundTripsAndFailedAuthenticationClearsDestination()
    {
        using var aes = new WindowsGcm(new byte[32]);
        var plain = Enumerable.Range(0, FileNetworkCrypto.MaximumBlock).Select(i => (byte)i).ToArray();
        var cipher = new byte[plain.Length]; var tag = new byte[16]; var output = new byte[plain.Length];
        aes.Encrypt(new byte[12], plain, cipher, tag);
        aes.Decrypt(new byte[12], cipher, tag, output); Assert.Equal(plain, output);
        tag[0] ^= 1;
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => aes.Decrypt(new byte[12], cipher, tag, output));
        Assert.All(output, b => Assert.Equal(0, b));
    }
}
