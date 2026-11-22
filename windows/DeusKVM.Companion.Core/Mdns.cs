using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace DeusKVM.Companion.Core;

/// <summary>A Mac found by Bonjour: its instance name, host, IPv4 addresses, port and TXT pairs.</summary>
public sealed record MdnsService(string Instance, string Host, IPAddress[] Addresses, int Port, IReadOnlyDictionary<string, string> Text)
{
    /// <summary>The Mac's control-link id from its TXT record, if it has a valid one.</summary>
    public Guid? Id => Text.TryGetValue("id", out var value) && Guid.TryParse(value, out var id) ? id : null;
}

/// <summary>One resource record of a response; <see cref="Data"/> is the target name (PTR), host (SRV) or empty.</summary>
public sealed record MdnsRecord(string Name, ushort Type, uint Ttl, string Data = "", int Port = 0,
    IPAddress? Address = null, IReadOnlyDictionary<string, string>? Text = null);

/// <summary>
/// Just enough mDNS (RFC 6762/6763) to find the Macs' <c>_deuskvm._tcp</c> service: one-shot
/// queries sent to 224.0.0.251:5353 from an ordinary UDP port, which responders answer by
/// unicast to that port (so no port 5353 bind, which Windows' DNS client owns, and no inbound
/// firewall rule). Responses are parsed for PTR, SRV, TXT and A records, following name
/// compression.
/// </summary>
public static class Mdns
{
    public static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);
    public const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeSrv = 33;
    private const ushort ClassIn = 1, UnicastResponse = 0x8000;

    public static string ServiceName => $"{ControlLink.ServiceType}.{ControlLink.ServiceDomain}";

    public static byte[] Query(params (string Name, ushort Type)[] questions)
    {
        using var output = new MemoryStream();
        output.Write([0, 0, 0, 0, 0, (byte)questions.Length, 0, 0, 0, 0, 0, 0], 0, 12); // id 0, flags 0
        foreach (var (name, type) in questions)
        {
            WriteName(output, name);
            WriteU16(output, type);
            WriteU16(output, ClassIn | UnicastResponse);
        }
        return output.ToArray();
    }

    /// <summary>Every answer, authority and additional record of a response; empty for anything malformed or not a response.</summary>
    public static IReadOnlyList<MdnsRecord> Records(byte[] packet)
    {
        var records = new List<MdnsRecord>();
        try
        {
            if (packet.Length < 12 || (packet[2] & 0x80) == 0) return records;
            var questions = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4));
            var count = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6)) + BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8)) +
                BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10));
            var offset = 12;
            for (var i = 0; i < questions; i++) { ReadName(packet, ref offset); offset += 4; }
            for (var i = 0; i < count && offset + 10 <= packet.Length; i++)
            {
                var name = ReadName(packet, ref offset);
                var type = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset));
                var ttl = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(offset + 4));
                var length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 8));
                var data = offset + 10;
                offset = data + length;
                if (offset > packet.Length) break;
                switch (type)
                {
                    case TypePtr:
                        var target = data;
                        records.Add(new MdnsRecord(name, type, ttl, ReadName(packet, ref target)));
                        break;
                    case TypeSrv when length >= 7:
                        var host = data + 6;
                        records.Add(new MdnsRecord(name, type, ttl, ReadName(packet, ref host), BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(data + 4))));
                        break;
                    case TypeTxt:
                        records.Add(new MdnsRecord(name, type, ttl, Text: ReadText(packet, data, length)));
                        break;
                    case TypeA when length == 4:
                        records.Add(new MdnsRecord(name, type, ttl, Address: new IPAddress(packet.AsSpan(data, 4).ToArray())));
                        break;
                }
            }
        }
        catch (Exception error) when (error is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException) { }
        return records;
    }

    internal static bool Same(string a, string b) => string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    // "Amadeus’s Mac Studio._deuskvm._tcp.local" → "Amadeus’s Mac Studio"
    internal static string Label(string instance, string service) =>
        instance.EndsWith("." + service, StringComparison.OrdinalIgnoreCase) ? instance.Substring(0, instance.Length - service.Length - 1) : instance;

    private static Dictionary<string, string> ReadText(byte[] packet, int offset, int length)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var end = offset + length;
        while (offset < end)
        {
            var size = packet[offset++];
            if (offset + size > end) break;
            var entry = Encoding.UTF8.GetString(packet, offset, size);
            offset += size;
            var equals = entry.IndexOf('=');
            if (equals > 0) pairs[entry.Substring(0, equals)] = entry.Substring(equals + 1);
        }
        return pairs;
    }

    public static string ReadName(byte[] packet, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        for (var hops = 0; hops < 64; hops++)
        {
            var length = packet[position];
            if (length == 0) { position++; if (!jumped) offset = position; return string.Join(".", labels); }
            if ((length & 0xC0) == 0xC0)
            {
                var pointer = (length & 0x3F) << 8 | packet[position + 1];
                if (!jumped) offset = position + 2;
                jumped = true;
                position = pointer;
                continue;
            }
            if ((length & 0xC0) != 0) throw new InvalidDataException("Bad label");
            labels.Add(Encoding.UTF8.GetString(packet, position + 1, length));
            position += 1 + length;
        }
        throw new InvalidDataException("Name loop");
    }

    public static void WriteName(Stream output, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException("Bad label");
            output.WriteByte((byte)bytes.Length);
            output.Write(bytes, 0, bytes.Length);
        }
        output.WriteByte(0);
    }

    private static void WriteU16(Stream output, ushort value) { output.WriteByte((byte)(value >> 8)); output.WriteByte((byte)value); }
}

/// <summary>
/// What responses have said about the service, with each record kept until its TTL runs out
/// (a TTL of 0 is a goodbye). <see cref="FollowUps"/> asks for whatever an instance still lacks
/// (its SRV/TXT, or its host's address), since a responder need not send them all at once.
/// </summary>
public sealed class MdnsBrowser(string service)
{
    private readonly Dictionary<string, (MdnsRecord Record, DateTime Expires)> instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (MdnsRecord Record, DateTime Expires)> srv = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (MdnsRecord Record, DateTime Expires)> txt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<IPAddress, DateTime>> addresses = new(StringComparer.OrdinalIgnoreCase);

    public string Service => service;
    public byte[] Query() => Mdns.Query((service, Mdns.TypePtr));

    public void Receive(byte[] packet, DateTime now)
    {
        foreach (var record in Mdns.Records(packet))
        {
            var expires = now.AddSeconds(Math.Min(record.Ttl, 86400u));
            switch (record.Type)
            {
                case Mdns.TypePtr when Mdns.Same(record.Name, service): Put(instances, record.Data, record, expires); break;
                case Mdns.TypeSrv: Put(srv, record.Name, record, expires); break;
                case Mdns.TypeTxt: Put(txt, record.Name, record, expires); break;
                case Mdns.TypeA when record.Address is { } address:
                    if (!addresses.TryGetValue(record.Name, out var known)) addresses[record.Name] = known = [];
                    if (record.Ttl == 0) known.Remove(address); else known[address] = expires;
                    break;
            }
        }
    }

    private static void Put(Dictionary<string, (MdnsRecord, DateTime)> map, string key, MdnsRecord record, DateTime expires)
    {
        if (record.Ttl == 0) map.Remove(key); else map[key] = (record, expires);
    }

    public IReadOnlyList<MdnsService> Services(DateTime now)
    {
        Expire(now);
        var services = new List<MdnsService>();
        foreach (var instance in instances.Keys)
        {
            if (!srv.TryGetValue(instance, out var target)) continue;
            var host = target.Record.Data;
            var found = addresses.TryGetValue(host, out var known) ? known.Keys.ToArray() : [];
            if (found.Length == 0) continue;
            services.Add(new MdnsService(Mdns.Label(instance, service), host, found, target.Record.Port,
                txt.TryGetValue(instance, out var text) ? text.Record.Text ?? new Dictionary<string, string>() : new Dictionary<string, string>()));
        }
        return services;
    }

    public IReadOnlyList<byte[]> FollowUps(DateTime now)
    {
        Expire(now);
        var questions = new List<(string, ushort)>();
        foreach (var instance in instances.Keys)
        {
            if (!srv.TryGetValue(instance, out var target)) { questions.Add((instance, Mdns.TypeSrv)); questions.Add((instance, Mdns.TypeTxt)); continue; }
            if (!txt.ContainsKey(instance)) questions.Add((instance, Mdns.TypeTxt));
            if (!addresses.TryGetValue(target.Record.Data, out var known) || known.Count == 0) questions.Add((target.Record.Data, Mdns.TypeA));
        }
        return questions.Distinct().Select(question => Mdns.Query(question)).ToArray();
    }

    private void Expire(DateTime now)
    {
        foreach (var map in new[] { instances, srv, txt })
            foreach (var key in map.Where(item => item.Value.Expires <= now).Select(item => item.Key).ToArray()) map.Remove(key);
        foreach (var known in addresses.Values)
            foreach (var address in known.Where(item => item.Value <= now).Select(item => item.Key).ToArray()) known.Remove(address);
    }
}
