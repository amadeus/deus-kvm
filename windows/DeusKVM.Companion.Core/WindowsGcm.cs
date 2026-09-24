using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeusKVM.Companion.Core;

// Windows CNG supplies AES-GCM; this is only a native API adapter, not a cipher implementation.
public sealed class WindowsGcm : IDisposable
{
    private readonly AlgorithmHandle algorithm;
    private readonly KeyHandle key;
    public WindowsGcm(byte[] secret)
    {
        if (secret.Length != 32) throw new ArgumentException("AES-256 requires 32 bytes", nameof(secret));
        Check(BCryptOpenAlgorithmProvider(out algorithm, "AES", null, 0));
        try
        {
            var mode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
            Check(BCryptSetProperty(algorithm, "ChainingMode", mode, mode.Length, 0));
            Check(BCryptGenerateSymmetricKey(algorithm, out key, IntPtr.Zero, 0, secret, secret.Length, 0));
        }
        catch { algorithm.Dispose(); throw; }
    }
    public void Encrypt(byte[] nonce, byte[] plain, Span<byte> cipher, Span<byte> tag) => Transform(true, nonce, plain, cipher, tag);
    public void Decrypt(byte[] nonce, ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> tag, byte[] plain)
    {
        var tagBuffer = tag.ToArray();
        try { Transform(false, nonce, cipher, plain, tagBuffer); }
        catch { RuntimeCompat.ZeroMemory(plain); throw; }
    }
    private unsafe void Transform(bool encrypt, byte[] nonce, ReadOnlySpan<byte> input, Span<byte> output, Span<byte> tag)
    {
        if (nonce.Length != 12 || tag.Length != 16 || input.Length != output.Length)
            throw new ArgumentException("Invalid GCM buffer lengths");
        // Non-null buffers also allow authenticated empty records instead of CNG's size-query mode.
        byte empty = 0;
        fixed (byte* noncePointer = nonce)
        fixed (byte* inputPointer = input)
        fixed (byte* outputPointer = output)
        fixed (byte* tagPointer = tag)
        {
            var info = new AuthInfo { Size = Marshal.SizeOf<AuthInfo>(), Version = 1,
                Nonce = (IntPtr)noncePointer, NonceSize = nonce.Length, Tag = (IntPtr)tagPointer, TagSize = tag.Length };
            int written;
            var status = encrypt
                ? BCryptEncrypt(key, inputPointer == null ? &empty : inputPointer, input.Length, ref info, IntPtr.Zero, 0,
                    outputPointer == null ? &empty : outputPointer, output.Length, out written, 0)
                : BCryptDecrypt(key, inputPointer == null ? &empty : inputPointer, input.Length, ref info, IntPtr.Zero, 0,
                    outputPointer == null ? &empty : outputPointer, output.Length, out written, 0);
            Check(status);
            if (written != output.Length) throw new CryptographicException("Unexpected encrypted record size");
        }
    }
    public void Dispose() { key.Dispose(); algorithm.Dispose(); }
    private static void Check(int status) { if (status < 0) throw new CryptographicException($"Windows cryptography failed: 0x{status:X8}"); }

    // Default Windows ABI packing is 8; ULONG is 32 bits even in a 64-bit process.
    [StructLayout(LayoutKind.Sequential)]
    private struct AuthInfo
    {
        public int Size, Version;
        public IntPtr Nonce; public int NonceSize;
        public IntPtr AuthData; public int AuthDataSize;
        public IntPtr Tag; public int TagSize;
        public IntPtr MacContext; public int MacContextSize;
        public int AadSize; public ulong DataSize; public int Flags;
    }
    private sealed class AlgorithmHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public AlgorithmHandle() : base(true) { }
        protected override bool ReleaseHandle() => BCryptCloseAlgorithmProvider(handle, 0) >= 0;
    }
    private sealed class KeyHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public KeyHandle() : base(true) { }
        protected override bool ReleaseHandle() => BCryptDestroyKey(handle) >= 0;
    }
    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] private static extern int BCryptOpenAlgorithmProvider(out AlgorithmHandle algorithm, string id, string? implementation, int flags);
    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] private static extern int BCryptSetProperty(AlgorithmHandle algorithm, string property, byte[] value, int size, int flags);
    [DllImport("bcrypt.dll")] private static extern int BCryptGenerateSymmetricKey(AlgorithmHandle algorithm, out KeyHandle key, IntPtr keyObject, int objectSize, byte[] secret, int secretSize, int flags);
    [DllImport("bcrypt.dll")] private static extern unsafe int BCryptEncrypt(KeyHandle key, byte* input, int inputSize, ref AuthInfo info, IntPtr iv, int ivSize, byte* output, int outputSize, out int written, int flags);
    [DllImport("bcrypt.dll")] private static extern unsafe int BCryptDecrypt(KeyHandle key, byte* input, int inputSize, ref AuthInfo info, IntPtr iv, int ivSize, byte* output, int outputSize, out int written, int flags);
    [DllImport("bcrypt.dll")] private static extern int BCryptDestroyKey(IntPtr key);
    [DllImport("bcrypt.dll")] private static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, int flags);
}
