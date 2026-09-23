using System.Runtime.InteropServices;

namespace DeusKVM.Companion.Core;

// Async extraction is an optional optimization, not authorization to read.
// Both synchronous and asynchronous consumers fetch only on an actual stream read.
public sealed class FileClipboardOperation(Func<bool> permitted, Func<bool> ownsClipboard, Action cancel, Action<string> diagnostic)
{
    private volatile bool operating, revoked;
    private int readStarted, denied;
    public bool Operating => operating;
    public void Start() { Check(); operating = true; diagnostic("async-start"); }
    public void End(int result)
    {
        operating = false;
        diagnostic($"async-end hr=0x{result:X8}");
        if (result < 0) { revoked = true; cancel(); }
    }
    public void Check()
    {
        if (revoked || !permitted()) Deny("session-unavailable");
    }
    public void CheckRead()
    {
        Check();
        if (!ownsClipboard()) Deny("clipboard-replaced");
        if (Interlocked.Exchange(ref readStarted, 1) == 0)
            diagnostic(operating ? "stream-read async" : "stream-read synchronous");
    }
    private void Deny(string reason)
    {
        if (Interlocked.Exchange(ref denied, 1) == 0) diagnostic("read-denied " + reason);
        throw new COMException("File offer is no longer available; copy it again on the Mac.", unchecked((int)0x80030005));
    }
}
