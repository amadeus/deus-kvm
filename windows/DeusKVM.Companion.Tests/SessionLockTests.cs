using System.Runtime.InteropServices;
using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class SessionLockTests
{
    [Theory]
    [InlineData(0, SessionLockState.Locked)]
    [InlineData(1, SessionLockState.Unlocked)]
    [InlineData(-1, SessionLockState.Unknown)]
    [InlineData(2, SessionLockState.Unknown)]
    public void ReadsNativeWtsInfoExLayout(int flags, SessionLockState expected)
    {
        WithNativeSnapshot(flags, buffer => Assert.Equal(expected, SessionLock.Parse(buffer, 232, 7)));
    }

    [Fact]
    public void RejectsIncompleteWrongLevelAndOtherSession()
    {
        WithNativeSnapshot(0, buffer =>
        {
            Assert.Equal(SessionLockState.Unknown, SessionLock.Parse(buffer, 231, 7));
            Assert.Equal(SessionLockState.Unknown, SessionLock.Parse(buffer, 232, 8));
            Marshal.WriteInt32(buffer, 0, 2);
            Assert.Equal(SessionLockState.Unknown, SessionLock.Parse(buffer, 232, 7));
        });
        Assert.Equal(SessionLockState.Unknown, SessionLock.Parse(IntPtr.Zero, 232, 7));
    }

    private static void WithNativeSnapshot(int flags, Action<IntPtr> check)
    {
        // WTSINFOEXW: DWORD Level followed by 8-byte-aligned WTSINFOEX_LEVEL1_W.
        // The native level-one record is 224 bytes; its first fields are session, state, flags.
        var buffer = Marshal.AllocHGlobal(232);
        try
        {
            Marshal.Copy(new byte[232], 0, buffer, 232);
            Marshal.WriteInt32(buffer, 0, 1);
            Marshal.WriteInt32(buffer, 8, 7);
            Marshal.WriteInt32(buffer, 12, 0);
            Marshal.WriteInt32(buffer, 16, flags);
            check(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
