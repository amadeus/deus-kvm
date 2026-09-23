using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class CaptureConnectionRequestTests
{
    private sealed class Request(Action release) : IDisposable { public void Dispose() => release(); }

    [Fact]
    public void RepeatedEntryAndResumeKeepOneRequestUntilReturn()
    {
        var acquired = 0; var released = 0;
        using var preference = new CaptureConnectionRequest(() => { acquired++; return new Request(() => released++); }, _ => Assert.Fail());
        var handoff = new CompanionHandoff(preference.SetCapturing);
        handoff.Enter(1, 0); handoff.Enter(1, 0); handoff.Enter(2, 0);
        handoff.Configure(new(0, "primary")); handoff.DesktopChanged();
        Assert.Equal(1, acquired); Assert.Equal(0, released);
        handoff.Exit(); handoff.Exit();
        Assert.Equal(1, released);
        handoff.Enter(3, 0); handoff.Reset();
        Assert.Equal(2, acquired); Assert.Equal(2, released);
    }

    [Fact]
    public void OldOwnerReleasesBeforeNewOwnerAcquires()
    {
        var events = new List<string>();
        using var oldPreference = new CaptureConnectionRequest(() => new Request(() => events.Add("old released")), _ => Assert.Fail());
        using var nextPreference = new CaptureConnectionRequest(() => { events.Add("new acquired"); return new Request(() => { }); }, _ => Assert.Fail());
        var old = new CompanionHandoff(oldPreference.SetCapturing);
        var next = new CompanionHandoff(nextPreference.SetCapturing);
        old.Enter(1, 0);
        old.Reset(); // BluetoothControl.SetActive(false), before the arbiter grants the next Mac.
        next.Enter(2, 0);
        Assert.Equal(new[] { "old released", "new acquired" }, events);
    }

    [Fact]
    public void UnsupportedOrRejectedRequestsDoNotBlockCaptureOrRetryEveryResume()
    {
        var attempts = 0;
        using var preference = new CaptureConnectionRequest(() => { attempts++; return null; }, _ => Assert.Fail());
        var handoff = new CompanionHandoff(preference.SetCapturing);
        handoff.Enter(1, 0); handoff.Enter(1, 0);
        Assert.True(handoff.Accept(1)); Assert.Equal(1, attempts);
        handoff.Exit(); handoff.Enter(2, 0);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void RequestAndReleaseFailuresDoNotBreakHandoff()
    {
        var errors = 0; var attempts = 0;
        using var preference = new CaptureConnectionRequest(() => {
            if (++attempts == 1) throw new IOException("request unavailable");
            return new Request(() => throw new IOException("release failed"));
        }, _ => errors++);
        var handoff = new CompanionHandoff(preference.SetCapturing);
        handoff.Enter(1, 0); Assert.True(handoff.Accept(1));
        handoff.Reset(); handoff.Enter(2, 0); handoff.Reset();
        Assert.Null(handoff.Active); Assert.Equal(2, errors);
    }

    [Fact]
    public void DisconnectAndDisposalReleaseOnceAndCannotRestartDisposedRequest()
    {
        var acquired = 0; var released = 0;
        var preference = new CaptureConnectionRequest(() => { acquired++; return new Request(() => released++); }, _ => Assert.Fail());
        preference.SetCapturing(true); preference.SetCapturing(false); preference.Dispose(); preference.Dispose();
        preference.SetCapturing(true);
        Assert.Equal(1, acquired); Assert.Equal(1, released);
        using var active = new CaptureConnectionRequest(() => new Request(() => released++), _ => Assert.Fail());
        active.SetCapturing(true); active.Dispose();
        Assert.Equal(2, released);
    }
}
