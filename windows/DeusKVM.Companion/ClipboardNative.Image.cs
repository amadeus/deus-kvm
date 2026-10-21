using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

internal static partial class ClipboardNative
{
    private const uint BitmapFormat = 2;
    private static readonly uint Png = RegisterClipboardFormat("PNG");

    private static byte[]? ReadImage()
    {
        try
        {
            if (IsClipboardFormatAvailable(Png) && ReadFormat(Png, ClipboardImageContent.MaximumBytes) is { } png &&
                ClipboardImageContent.HasSafeDimensions(png))
            {
                using var input = new MemoryStream(png, false);
                using var decoded = Image.FromStream(input, false, true);
                return png;
            }
            // Windows synthesizes CF_BITMAP for DIB/DIBV5-only producers. Clone the
            // borrowed HBITMAP; never delete the clipboard's handle.
            var handle = GetClipboardData(BitmapFormat);
            if (handle == IntPtr.Zero || GetObject(handle, Marshal.SizeOf<NativeBitmap>(), out var info) == 0 ||
                info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > ClipboardImageContent.MaximumPixels) return null;
            using var bitmap = Image.FromHbitmap(handle);
            using var output = new MemoryStream();
            bitmap.Save(output, ImageFormat.Png);
            return output.Length <= ClipboardImageContent.MaximumBytes ? output.ToArray() : null;
        }
        catch (Exception error) when (error is ArgumentException or ExternalException or OutOfMemoryException) { return null; }
    }

    public static byte[]? ImageDib(byte[] png)
    {
        if (!ClipboardImageContent.HasSafeDimensions(png)) return null;
        try
        {
            using var input = new MemoryStream(png, false);
            using var image = Image.FromStream(input, false, true);
            // PNG preserves alpha for Chromium/Electron. A conventional opaque DIB
            // also supports apps that only understand the Windows bitmap formats.
            using var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White); graphics.DrawImageUnscaled(image, 0, 0);
            }
            using var output = new MemoryStream();
            bitmap.Save(output, ImageFormat.Bmp);
            var bmp = output.ToArray();
            var dib = new byte[bmp.Length - 14];
            Buffer.BlockCopy(bmp, 14, dib, 0, dib.Length);
            return dib;
        }
        catch (Exception error) when (error is ArgumentException or ExternalException or OutOfMemoryException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr handle, int size, out NativeBitmap bitmap);
}
