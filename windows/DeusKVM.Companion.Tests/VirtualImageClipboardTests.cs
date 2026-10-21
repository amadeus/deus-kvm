#if NETFRAMEWORK
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

// Exercises the production OLE data object on Windows without installing it on
// the user's clipboard. Portable stream/lifetime checks live in ClipboardImageTests.
public sealed class VirtualImageClipboardTests
{
    [Fact]
    public void OneDataObjectOffersBothImageFormatsAndExplorerFileFormats()
    {
        using var bitmap = new Bitmap(2, 1, PixelFormat.Format32bppArgb);
        bitmap.SetPixel(0, 0, Color.FromArgb(128, 255, 0, 0));
        bitmap.SetPixel(1, 0, Color.Blue);
        using var encoded = new MemoryStream(); bitmap.Save(encoded, ImageFormat.Png);
        var png = encoded.ToArray();
        var dib = ClipboardNative.ImageDib(png);
        Assert.NotNull(dib);
        Assert.Equal(2, BitConverter.ToInt32(dib!, 4));
        Assert.Equal(24, BitConverter.ToInt16(dib!, 14));
        using var session = ClipboardImageContent.CreateFileSession(new(1, 2, "Screenshot.png", png.Length, ContentType: "image/png"), png);
        var clipboard = new VirtualFileClipboard(session, () => true, png, dib!);
        var formats = Enumerate(clipboard);
        foreach (var id in new[] { Format("PNG"), (short)8, Format("FileGroupDescriptorW"), Format("FileContents") })
            Assert.Contains(formats, entry => entry.cfFormat == id);
        foreach (var entry in formats)
        {
            var query = entry;
            Assert.Equal(0, clipboard.QueryGetData(ref query));
        }
        var descriptor = formats.Single(entry => entry.cfFormat == Format("FileGroupDescriptorW"));
        clipboard.GetData(ref descriptor, out var medium);
        try
        {
            var pointer = GlobalLock(medium.unionmember);
            Assert.NotEqual(IntPtr.Zero, pointer);
            try
            {
                var bytes = new byte[596]; Marshal.Copy(pointer, bytes, 0, bytes.Length);
                Assert.Equal(1, BitConverter.ToInt32(bytes, 0));
                Assert.Equal(png.Length, BitConverter.ToInt32(bytes, 72));
                Assert.Equal("Screenshot.png", Encoding.Unicode.GetString(bytes, 76, 520).TrimEnd('\0'));
            }
            finally { GlobalUnlock(medium.unionmember); }
        }
        finally { ReleaseStgMedium(ref medium); }
        var contents = formats.Single(entry => entry.cfFormat == Format("FileContents"));
        Assert.Equal(TYMED.TYMED_ISTREAM, contents.tymed);
        Assert.Equal(0, contents.lindex);
        contents.lindex = 1;
        Assert.NotEqual(0, clipboard.QueryGetData(ref contents));
        // The image bytes themselves require clipboard ownership, just like file reads.
        var image = formats.Single(entry => entry.cfFormat == Format("PNG"));
        Assert.Throws<COMException>(() => clipboard.GetData(ref image, out _));
    }

    [Fact]
    public void OrdinaryFileObjectDoesNotAdvertiseAnImage()
    {
        using var session = new FileClipboardSession(new(1, 2, "file.txt", 1), new TestFileReader(_ => [1]));
        var formats = Enumerate(new VirtualFileClipboard(session, () => true));
        Assert.DoesNotContain(formats, entry => entry.cfFormat == Format("PNG") || entry.cfFormat == 8);
        Assert.Contains(formats, entry => entry.cfFormat == Format("FileContents"));
    }

    private static List<FORMATETC> Enumerate(VirtualFileClipboard clipboard)
    {
        var enumerator = clipboard.EnumFormatEtc(DATADIR.DATADIR_GET);
        var result = new List<FORMATETC>(); var entry = new FORMATETC[1];
        while (enumerator.Next(1, entry, null!) == 0) result.Add(entry[0]);
        return result;
    }
    private static short Format(string name) => unchecked((short)RegisterClipboardFormat(name));
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}
#endif
