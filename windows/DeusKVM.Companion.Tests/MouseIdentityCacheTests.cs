using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class MouseIdentityCacheTests
{
    [Fact]
    public void TransientMissRecoversWithoutReconnectOrPerPacketProbing()
    {
        var cache = new MouseIdentityCache();
        var calls = 0;
        var ready = false;
        bool Probe(IntPtr _) { calls++; return ready; }
        Assert.False(cache.Resolve((IntPtr)1, Probe));
        ready = true;
        for (var i = 0; i < 1000; i++) Assert.False(cache.Resolve((IntPtr)1, Probe));
        Assert.Equal(1, calls);
        cache.Refresh(new HashSet<IntPtr> { (IntPtr)1 }, Probe);
        Assert.True(cache.HasMatch);
        Assert.True(cache.Resolve((IntPtr)1, Probe));
        Assert.Equal(2, calls);
        cache.Refresh(new HashSet<IntPtr> { (IntPtr)1 }, Probe);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void RemovalAndDeviceChangeInvalidateCachedMatches()
    {
        var cache = new MouseIdentityCache();
        Assert.True(cache.Resolve((IntPtr)1, _ => true));
        cache.Refresh(new HashSet<IntPtr>(), _ => true);
        Assert.False(cache.HasMatch);
        Assert.False(cache.Resolve((IntPtr)1, _ => false));
        cache.Clear();
        Assert.True(cache.Resolve((IntPtr)1, _ => true));
    }

    [Fact]
    public void OtherMouseNeverBecomesTheSelectedMacByFallback()
    {
        var cache = new MouseIdentityCache();
        var present = new HashSet<IntPtr> { (IntPtr)1, (IntPtr)2 };
        cache.Refresh(present, handle => handle == (IntPtr)2);
        Assert.False(cache.Resolve((IntPtr)1, _ => true));
        Assert.True(cache.Resolve((IntPtr)2, _ => false));
        Assert.True(cache.HasMatch);
    }
}
