using System.Runtime.InteropServices;
using System.Text;
using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

// Called only on the clipboard STA. No clipboard contents enter logs or files.
internal static class ClipboardNative
{
    private const uint UnicodeText = 13, FileDrop = 15;
    private static readonly uint Own = RegisterClipboardFormat("DeusKVM.Clipboard");
    private static readonly uint[] Private = ClipboardPrivacy.ExcludedFormats.Select(RegisterClipboardFormat).ToArray();
    private static readonly uint[] Flags = ClipboardPrivacy.PermissionFormats.Select(RegisterClipboardFormat).ToArray();
    private static readonly UnicodeEncoding Utf16 = new(false, false, true);
    public static bool Read(IntPtr window, out uint revision, out byte[]? text, out ClipboardFileSource? file)
    {
        revision = 0; text = null; file = null;
        if (!OpenClipboard(window)) return false;
        try
        {
            revision = GetClipboardSequenceNumber();
            if (Private.Any(IsClipboardFormatAvailable) || Flags.Any(IsPrivateFlag)) return true;
            if (IsClipboardFormatAvailable(FileDrop))
            {
                var drop = GetClipboardData(FileDrop);
                if (drop != IntPtr.Zero && DragQueryFile(drop, uint.MaxValue, null, 0) == 1)
                {
                    var length = DragQueryFile(drop, 0, null, 0);
                    if (length is > 0 and < 32768)
                    {
                        var path = new StringBuilder((int)length + 1);
                        if (DragQueryFile(drop, 0, path, (uint)path.Capacity) == length) file = ClipboardFileSource.Capture(path.ToString());
                    }
                }
                return revision == GetClipboardSequenceNumber();
            }
            if (!IsClipboardFormatAvailable(UnicodeText)) return true;
            var bytes = ReadFormat(UnicodeText, ClipboardTransfer.MaximumBytes * 2 + 2);
            if (bytes is null || bytes.Length % 2 != 0) return true;
            var end = 0;
            while (end + 1 < bytes.Length && (bytes[end] != 0 || bytes[end + 1] != 0)) end += 2;
            if (end + 1 >= bytes.Length) return true;
            try { text = ClipboardTransfer.Text(Utf16.GetString(bytes, 0, end)); }
            catch (DecoderFallbackException) { }
            return revision == GetClipboardSequenceNumber();
        }
        finally { CloseClipboard(); }
    }
    private static bool IsPrivateFlag(uint format)
    {
        if (!IsClipboardFormatAvailable(format)) return false;
        var value = ReadFormat(format, 16);
        return ClipboardPrivacy.BlocksSharing(value is null || value.Length < 4 ? null : BitConverter.ToUInt32(value, 0));
    }
    private static byte[]? ReadFormat(uint format, int limit)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;
        var size = GlobalSize(handle).ToUInt64();
        if (size == 0 || size > (ulong)limit) return null;
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try { var bytes = new byte[(int)size]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return bytes; }
        finally { GlobalUnlock(handle); }
    }
    public static bool Write(IntPtr window, byte[] text, uint expected, Func<bool> canAccess, out uint revision)
    {
        revision = GetClipboardSequenceNumber();
        var decoded = ClipboardTransfer.Decode(text);
        if (decoded is null || !OpenClipboard(window)) return false;
        try
        {
            if (GetClipboardSequenceNumber() != expected) return false;
            var content = Utf16.GetBytes(decoded.Replace("\n", "\r\n") + "\0");
            var block = Allocate(content); var marker = Allocate([1]); var cloud = Allocate([0, 0, 0, 0]);
            try
            {
                if (block == IntPtr.Zero || marker == IntPtr.Zero || cloud == IntPtr.Zero || !canAccess() || !EmptyClipboard()) return false;
                if (SetClipboardData(UnicodeText, block) == IntPtr.Zero) return false;
                block = IntPtr.Zero;
                if (SetClipboardData(Own, marker) != IntPtr.Zero) marker = IntPtr.Zero;
                if (SetClipboardData(Flags[1], cloud) != IntPtr.Zero) cloud = IntPtr.Zero;
                revision = GetClipboardSequenceNumber();
                return true;
            }
            finally
            {
                if (block != IntPtr.Zero) GlobalFree(block);
                if (marker != IntPtr.Zero) GlobalFree(marker);
                if (cloud != IntPtr.Zero) GlobalFree(cloud);
            }
        }
        finally { CloseClipboard(); }
    }
    private static IntPtr Allocate(byte[] data)
    {
        var handle = GlobalAlloc(2, (UIntPtr)data.Length);
        if (handle == IntPtr.Zero) return handle;
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) { GlobalFree(handle); return IntPtr.Zero; }
        try { Marshal.Copy(data, 0, pointer, data.Length); }
        finally { GlobalUnlock(handle); }
        return handle;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "DragQueryFileW")]
    private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? path, uint length);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
