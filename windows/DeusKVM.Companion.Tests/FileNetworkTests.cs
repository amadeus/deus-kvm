using System.Security.Cryptography;
using System.Text.Json;
using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class FileNetworkTests
{
    private sealed record Vector(string Secret, string Client, string Server, string Direction, string[] Plain, string[] Sealed);
    [Fact]
    public void SharedSwiftVectorsAuthenticateAndRejectReplay()
    {
        var vectors = JsonSerializer.Deserialize<Vector[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "file-network-fixtures.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        foreach (var v in vectors)
        {
            using var sender = new FileNetworkCrypto(Convert.FromHexString(v.Secret), Convert.FromHexString(v.Client), Convert.FromHexString(v.Server), v.Direction);
            using var receiver = new FileNetworkCrypto(Convert.FromHexString(v.Secret), Convert.FromHexString(v.Client), Convert.FromHexString(v.Server), v.Direction);
            for (var i = 0; i < v.Plain.Length; i++)
            {
                Assert.Equal(Convert.FromHexString(v.Sealed[i]), sender.Seal(Convert.FromHexString(v.Plain[i])));
                Assert.Equal(Convert.FromHexString(v.Plain[i]), receiver.Open(Convert.FromHexString(v.Sealed[i])));
            }
            Assert.ThrowsAny<CryptographicException>(() => receiver.Open(Convert.FromHexString(v.Sealed[0])));
        }
    }
    [Fact]
    public void WrongKeyTamperingAndDifferentConnectionNoncesRejectRecords()
    {
        var key = new byte[32]; var client = new byte[32]; var server = new byte[32];
        using var encoder = new FileNetworkCrypto(key, client, server, "client");
        var data = encoder.Seal([1, 2, 3]);
        using var reflected = new FileNetworkCrypto(key, client, server, "server");
        Assert.ThrowsAny<CryptographicException>(() => reflected.Open(data));
        client[0] = 1;
        using var other = new FileNetworkCrypto(key, client, server, "client");
        Assert.ThrowsAny<CryptographicException>(() => other.Open(data));
        client[0] = 0; data[0] ^= 1;
        using var receiver = new FileNetworkCrypto(key, client, server, "client");
        Assert.ThrowsAny<CryptographicException>(() => receiver.Open(data));
    }
    [Theory]
    [InlineData("192.168.1.2", true)]
    [InlineData("10.0.0.2", true)]
    [InlineData("172.16.0.2", true)]
    [InlineData("172.32.0.2", false)]
    [InlineData("169.254.1.2", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("example.com", false)]
    public void AdvertisementsOnlyDialLocalIPv4(string host, bool expected) => Assert.Equal(expected, FileNetworkOffer.LocalAddress(host));

    [Fact]
    public void MetadataDoesNotStartNetworkAndInvalidEndpointsRetainBluetooth()
    {
        var offer = new FileClipboardOffer(1, 2, "file", 3, Network: new(["192.168.1.2"], 12345, Convert.ToBase64String(new byte[32])));
        using var session = new FileClipboardSession(offer, _ => Assert.Fail("No requests during metadata inspection"));
        var stream = new RemoteFileStream(session, () => { });
        stream.Stat(out var stat, 0); Assert.Equal(3, stat.cbSize);
        var invalid = offer with { Network = offer.Network! with { Hosts = ["8.8.8.8"] } };
        Assert.Null(FileClipboardOffer.Parse(JsonSerializer.SerializeToUtf8Bytes(invalid)).Network);
    }
}
