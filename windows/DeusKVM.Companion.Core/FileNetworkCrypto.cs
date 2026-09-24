using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DeusKVM.Companion.Core;

public sealed record FileNetworkOffer(string[] Hosts, int Port, string Key)
{
    public bool Valid => Hosts is { Length: > 0 and <= 4 } && Hosts.All(LocalAddress) && Port is > 0 and <= 65535 && ValidKey();
    private bool ValidKey()
    {
        if (Key is not { Length: 44 }) return false;
        try { return Convert.FromBase64String(Key).Length == 32; }
        catch (FormatException) { return false; }
    }
    public static bool LocalAddress(string host)
    {
        if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254;
    }
}

public sealed class FileNetworkCrypto : IDisposable
{
    public const int MaximumBlock = 256 * 1024;
#if NETFRAMEWORK
    private readonly WindowsGcm aes;
#else
    private readonly AesGcm aes;
#endif
    private ulong counter;
    public FileNetworkCrypto(byte[] secret, byte[] client, byte[] server, string direction)
    {
        if (secret.Length != 32 || client.Length != 32 || server.Length != 32 || direction is not ("client" or "server"))
            throw new InvalidDataException("Invalid network key context");
        var key = RuntimeCompat.DeriveFileKey(secret, client.Concat(server).ToArray(),
            Encoding.UTF8.GetBytes("DeusKVM file v1 " + direction));
        try
        {
#if NETFRAMEWORK
            aes = new WindowsGcm(key);
#else
            aes = new AesGcm(key, 16);
#endif
        }
        finally { RuntimeCompat.ZeroMemory(key); }
    }
    private byte[] Nonce()
    {
        if (counter == ulong.MaxValue) throw new CryptographicException("Record sequence exhausted");
        var nonce = new byte[12]; BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter++); return nonce;
    }
    public byte[] Seal(byte[] data)
    {
        var result = new byte[data.Length + 16];
        aes.Encrypt(Nonce(), data, result.AsSpan(0, data.Length), result.AsSpan(data.Length)); return result;
    }
    public byte[] Open(byte[] data)
    {
        if (data.Length < 16) throw new InvalidDataException("Invalid encrypted record");
        var result = new byte[data.Length - 16];
        aes.Decrypt(Nonce(), data.AsSpan(0, result.Length), data.AsSpan(result.Length), result); return result;
    }
    public void Dispose() => aes.Dispose();
}
