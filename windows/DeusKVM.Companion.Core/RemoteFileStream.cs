using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DeusKVM.Companion.Core;

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RemoteFileStream(FileClipboardSession session, Action check) : IStream
{
    private long position;
    private byte[] block = [];
    private long blockOffset = -1;
    public void Read(byte[] buffer, int count, IntPtr read)
    {
        check();
        var total = 0;
        while (total < count && position < session.Offer.Size)
        {
            check();
            if (position < blockOffset || position >= blockOffset + block.Length)
            { blockOffset = position; block = session.Read((uint)position); }
            var index = (int)(position - blockOffset);
            var n = Math.Min(count - total, block.Length - index);
            Array.Copy(block, index, buffer, total, n); total += n; position += n;
        }
        if (read != IntPtr.Zero) Marshal.WriteInt32(read, total);
    }
    public void Seek(long move, int origin, IntPtr result)
    {
        var next = origin switch { 0 => move, 1 => position + move, 2 => session.Offer.Size + move, _ => -1 };
        if (next < 0 || next > session.Offer.Size) throw new IOException("Invalid seek");
        position = next; if (result != IntPtr.Zero) Marshal.WriteInt64(result, position);
    }
    public void Stat(out STATSTG stat, int flags) => stat = new STATSTG { type = 2, cbSize = session.Offer.Size, grfMode = 0, pwcsName = (flags & 1) == 0 ? session.Offer.Name : null! };
    public void Clone(out IStream stream) => stream = new RemoteFileStream(session, check) { position = position };
    public void CopyTo(IStream target, long count, IntPtr read, IntPtr written)
    {
        var buffer = new byte[4096]; long total = 0;
        var number = Marshal.AllocCoTaskMem(4);
        try
        {
            while (total < count && position < session.Offer.Size)
            {
                var size = (int)Math.Min(buffer.Length, Math.Min(count - total, session.Offer.Size - position));
                Read(buffer, size, number); var n = Marshal.ReadInt32(number);
                target.Write(buffer, n, number);
                if (Marshal.ReadInt32(number) != n) throw new IOException("Short destination write");
                total += n;
            }
        }
        finally { Marshal.FreeCoTaskMem(number); if (read != IntPtr.Zero) Marshal.WriteInt64(read, total); if (written != IntPtr.Zero) Marshal.WriteInt64(written, total); }
    }
    public void Write(byte[] buffer, int count, IntPtr written) => throw new UnauthorizedAccessException();
    public void SetSize(long size) => throw new UnauthorizedAccessException();
    public void Commit(int flags) { }
    public void Revert() => throw new NotSupportedException();
    public void LockRegion(long offset, long count, int type) => throw new NotSupportedException();
    public void UnlockRegion(long offset, long count, int type) => throw new NotSupportedException();
}
