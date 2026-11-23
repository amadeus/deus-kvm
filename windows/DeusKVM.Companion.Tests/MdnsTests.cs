using System.Net;
using System.Text;
using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class MdnsTests
{
    private const string Instance = "Amadeus’s Mac Studio._deuskvm._tcp.local";
    private const string Host = "Mac-Studio.local";

    // A response as mDNSResponder sends one: header, then records (the PTR name compressed).
    private static byte[] Response(params byte[][] records)
    {
        var bytes = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, (byte)records.Length, 0, 0, 0, 0 };
        foreach (var record in records) bytes.AddRange(record);
        return bytes.ToArray();
    }

    private static byte[] Name(string name)
    {
        using var stream = new MemoryStream();
        Mdns.WriteName(stream, name);
        return stream.ToArray();
    }

    private static byte[] Record(byte[] name, ushort type, uint ttl, byte[] data)
    {
        var bytes = new List<byte>(name) { (byte)(type >> 8), (byte)type, 0x80, 1, (byte)(ttl >> 24), (byte)(ttl >> 16), (byte)(ttl >> 8), (byte)ttl,
            (byte)(data.Length >> 8), (byte)data.Length };
        bytes.AddRange(data);
        return bytes.ToArray();
    }

    private static byte[] Ptr(uint ttl = 4500) => Record(Name(Mdns.ServiceName), Mdns.TypePtr, ttl, Name(Instance));
    private static byte[] Srv(uint ttl = 120) => Record(Name(Instance), Mdns.TypeSrv, ttl, [0, 0, 0, 0, 0xD0, 0x78, .. Name(Host)]);
    private static byte[] Txt(string id) =>
        Record(Name(Instance), Mdns.TypeTxt, 4500, [3, .. "v=1"u8.ToArray(), (byte)(3 + id.Length), .. Encoding.UTF8.GetBytes("id=" + id)]);
    private static byte[] A(byte last, uint ttl = 120) => Record(Name(Host), Mdns.TypeA, ttl, [192, 168, 1, last]);

    [Fact]
    public void QueriesAskForUnicastAnswers()
    {
        var query = Mdns.Query((Mdns.ServiceName, Mdns.TypePtr));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, query.Take(12));
        Assert.Equal(new byte[] { 0, 12, 0x80, 1 }, query.Skip(query.Length - 4)); // PTR, IN with the QU bit
        var offset = 12;
        Assert.Equal(Mdns.ServiceName, Mdns.ReadName(query, ref offset));
    }

    [Fact]
    public void AFullAnswerDescribesTheMac()
    {
        var id = "fc0e14f6-8853-7123-8229-c03b61bdac22";
        var browser = new MdnsBrowser(Mdns.ServiceName);
        var now = DateTime.UtcNow;
        browser.Receive(Response(Ptr(), Srv(), Txt(id), A(50)), now);
        var service = Assert.Single(browser.Services(now));
        Assert.Equal("Amadeus’s Mac Studio", service.Instance);
        Assert.Equal(53368, service.Port);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), Assert.Single(service.Addresses));
        Assert.Equal(new Guid(id), service.Id);
        Assert.Empty(browser.FollowUps(now));
    }

    [Fact]
    public void MissingRecordsAreAskedForAndCompleteTheService()
    {
        var browser = new MdnsBrowser(Mdns.ServiceName);
        var now = DateTime.UtcNow;
        browser.Receive(Response(Ptr()), now);
        Assert.Empty(browser.Services(now));
        Assert.Equal(2, browser.FollowUps(now).Count); // SRV and TXT for the instance
        browser.Receive(Response(Srv()), now);
        var followUp = Assert.Single(browser.FollowUps(now.AddSeconds(1)).Skip(1)); // TXT, then the host's A
        var offset = 12;
        Assert.Equal(Host, Mdns.ReadName(followUp, ref offset));
        browser.Receive(Response(A(50), A(51)), now);
        Assert.Equal(2, Assert.Single(browser.Services(now)).Addresses.Length);
    }

    [Fact]
    public void GoodbyesAndExpiryRemoveTheService()
    {
        var browser = new MdnsBrowser(Mdns.ServiceName);
        var now = DateTime.UtcNow;
        browser.Receive(Response(Ptr(), Srv(), A(50)), now);
        Assert.Single(browser.Services(now));
        Assert.Empty(browser.Services(now.AddSeconds(121))); // SRV and A expired
        browser.Receive(Response(Ptr(), Srv(), A(50)), now);
        browser.Receive(Response(Ptr(ttl: 0)), now);
        Assert.Empty(browser.Services(now));
    }

    [Fact]
    public void MalformedPacketsAreIgnored()
    {
        Assert.Empty(Mdns.Records([1, 2, 3]));
        Assert.Empty(Mdns.Records(Mdns.Query((Mdns.ServiceName, Mdns.TypePtr)))); // a query, not a response
        var looping = Response(Record([0xC0, 12], Mdns.TypePtr, 10, [0xC0, 12])); // a pointer to itself
        Assert.Empty(Mdns.Records(looping));
    }
}
