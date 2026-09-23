using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using DeusKVM.Companion.Core;
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace DeusKVM.Companion;

[ComVisible(true), Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IFileAsyncOperation
{
    void SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool value);
    void GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool value);
    void StartOperation(IntPtr reserved);
    void InOperation([MarshalAs(UnmanagedType.Bool)] out bool value);
    void EndOperation(int result, IntPtr reserved, uint effects);
}

// Live OLE data object: never flush it, which would eagerly render the contents.
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class VirtualFileClipboard : IDataObject, IFileAsyncOperation
{
    private static readonly short Descriptor = Format("FileGroupDescriptorW"), Contents = Format("FileContents");
    private static readonly short Effect = Format("Preferred DropEffect"), Own = Format("DeusKVM.Clipboard");
    private static readonly short History = Format("CanIncludeInClipboardHistory"), Cloud = Format("CanUploadToCloudClipboard");
    private readonly FileClipboardSession session;
    private readonly FileClipboardOperation operation;
    private bool asyncMode = true;
    private IntPtr ownerWindow;
    public VirtualFileClipboard(FileClipboardSession session, Func<bool> permitted)
    {
        this.session = session;
        operation = new FileClipboardOperation(permitted,
            () => Volatile.Read(ref ownerWindow) != IntPtr.Zero && GetClipboardOwner() == Volatile.Read(ref ownerWindow),
            session.Dispose, FilePasteDiagnostics.Write);
    }
    // OLE identity checks stay on the clipboard STA. Stream callbacks may run
    // on another COM thread, where we check the published HWND instead.
    internal bool IsCurrent => OleIsCurrentClipboard(this) == 0;
    internal bool Install()
    {
        var result = OleSetClipboard(this);
        if (result < 0) return false;
        Volatile.Write(ref ownerWindow, GetClipboardOwner());
        FilePasteDiagnostics.Write("offer-installed");
        return true;
    }
    internal bool Revoke()
    {
        session.Dispose();
        return OleIsCurrentClipboard(this) == 0 && OleSetClipboard(null) >= 0;
    }
    public void SetAsyncMode(bool value) => asyncMode = value;
    public void GetAsyncMode(out bool value) => value = asyncMode;
    public void StartOperation(IntPtr reserved) => operation.Start();
    public void InOperation(out bool value) => value = operation.Operating;
    public void EndOperation(int result, IntPtr reserved, uint effects) => operation.End(result);
    private static FORMATETC Entry(short id, TYMED medium, int index = -1) => new() { cfFormat = id, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = medium };
    private static FORMATETC[] Formats => [Entry(Descriptor, TYMED.TYMED_HGLOBAL), Entry(Contents, TYMED.TYMED_ISTREAM, 0),
        Entry(Effect, TYMED.TYMED_HGLOBAL), Entry(Own, TYMED.TYMED_HGLOBAL), Entry(History, TYMED.TYMED_HGLOBAL), Entry(Cloud, TYMED.TYMED_HGLOBAL)];
    public int QueryGetData(ref FORMATETC format)
    {
        var requested = format;
        return Formats.Any(f => f.cfFormat == requested.cfFormat && (f.tymed & requested.tymed) != 0 &&
            requested.dwAspect == DVASPECT.DVASPECT_CONTENT && (requested.lindex == f.lindex || requested.cfFormat == Contents && requested.lindex == -1))
            ? 0 : unchecked((int)0x80040064);
    }
    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        operation.Check();
        Marshal.ThrowExceptionForHR(QueryGetData(ref format));
        if (format.cfFormat == Contents)
        {
            FilePasteDiagnostics.Write("contents-stream-requested");
            medium = new STGMEDIUM { tymed = TYMED.TYMED_ISTREAM,
                unionmember = Marshal.GetComInterfaceForObject(new RemoteFileStream(session, operation.CheckRead), typeof(IStream)) };
            return;
        }
        byte[] bytes;
        if (format.cfFormat == Descriptor)
        {
            bytes = new byte[4 + 592]; // FILEGROUPDESCRIPTORW with one FILEDESCRIPTORW.
            BitConverter.GetBytes(1).CopyTo(bytes, 0);
            BitConverter.GetBytes(0x80004044u).CopyTo(bytes, 4); // Unicode, progress, size, attributes.
            BitConverter.GetBytes(0x80u).CopyTo(bytes, 4 + 36); // FILE_ATTRIBUTE_NORMAL.
            BitConverter.GetBytes((uint)session.Offer.Size).CopyTo(bytes, 4 + 68);
            Encoding.Unicode.GetBytes(session.Offer.Name + "\0").CopyTo(bytes, 4 + 72);
        }
        else bytes = BitConverter.GetBytes(format.cfFormat == Effect || format.cfFormat == Own ? 1u : 0u);
        var handle = GlobalAlloc(0x42, (UIntPtr)bytes.Length);
        if (handle == IntPtr.Zero) throw new OutOfMemoryException();
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) { GlobalFree(handle); throw new OutOfMemoryException(); }
        try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
        finally { GlobalUnlock(handle); }
        medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle };
    }
    public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => direction == DATADIR.DATADIR_GET ? new FileFormatEnumerator(Formats) : throw new COMException("Not supported", unchecked((int)0x80004001));
    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException("Not supported", unchecked((int)0x80004001));
    public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = input; output.ptd = IntPtr.Zero; return 0x00040130; }
    public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) { if (release) ReleaseStgMedium(ref medium); }
    public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
    public void DUnadvise(int connection) { }
    public int EnumDAdvise(out IEnumSTATDATA? data) { data = null; return unchecked((int)0x80040003); }
    private static short Format(string name) => unchecked((short)RegisterClipboardFormat(name));
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("ole32.dll")] private static extern int OleSetClipboard(IDataObject? data);
    [DllImport("ole32.dll")] private static extern int OleIsCurrentClipboard(IDataObject data);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr handle);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class FileFormatEnumerator(FORMATETC[] formats) : IEnumFORMATETC
{
    private int position;
    public int Next(int count, FORMATETC[] values, int[]? fetched)
    {
        var n = Math.Min(count, formats.Length - position);
        Array.Copy(formats, position, values, 0, n); position += n;
        if (fetched is { Length: > 0 }) fetched[0] = n;
        return n == count ? 0 : 1;
    }
    public int Skip(int count) { var n = Math.Min(count, formats.Length - position); position += n; return n == count ? 0 : 1; }
    public int Reset() { position = 0; return 0; }
    public void Clone(out IEnumFORMATETC copy) => copy = new FileFormatEnumerator(formats) { position = position };
}

