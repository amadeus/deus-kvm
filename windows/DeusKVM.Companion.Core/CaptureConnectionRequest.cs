namespace DeusKVM.Companion.Core;

// Keep one optional connection preference for a remote capture, never per input
// report. An unavailable/failed preference must not interrupt ordinary HID input.
public sealed class CaptureConnectionRequest(Func<IDisposable?> acquire, Action<Exception> failed) : IDisposable
{
    private IDisposable? request;
    private bool capturing, disposed;

    public void SetCapturing(bool value)
    {
        if (disposed || capturing == value) return;
        capturing = value;
        if (!value) { Release(); return; }
        try { request = acquire(); }
        catch (Exception error) { failed(error); }
    }

    private void Release()
    {
        var previous = request;
        request = null;
        try { previous?.Dispose(); }
        catch (Exception error) { failed(error); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        capturing = false;
        Release();
    }
}
