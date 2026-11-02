using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class ClipboardWakeTests
{
    [Fact]
    public void IdleClipboardNeedsNoRetryTimer() =>
        Assert.False(ClipboardWake.NeedsRetry(true, true, false, false, false, false, false));

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, true)]
    public void PendingWorkRetriesWhileTheClipboardIsUsable(bool unread, bool text, bool file, bool image, bool yield)
    {
        Assert.True(ClipboardWake.NeedsRetry(true, true, unread, text, file, image, yield));
        Assert.False(ClipboardWake.NeedsRetry(false, true, unread, text, file, image, yield), "sharing disabled");
        Assert.False(ClipboardWake.NeedsRetry(true, false, unread, text, file, image, yield), "secure desktop or not permitted");
    }
}
