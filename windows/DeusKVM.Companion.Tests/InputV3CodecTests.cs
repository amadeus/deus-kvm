using System.Text;
using System.Text.Json;
using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class InputV3CodecTests
{
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "multi-mac-fixtures.json")));
    private static ulong Number(JsonElement fixture, string name) => Convert.ToUInt64(fixture.GetProperty(name).GetString(), 16);
    private static byte[] Bytes(JsonElement fixture, string name) => TestBytes.FromHex(fixture.GetProperty(name).GetString()!);

    [Fact]
    public void EveryMacRecordPreservesCaptureAndPayloadAcrossLanguages()
    {
        using var fixture = Fixture();
        var root = fixture.RootElement;
        foreach (var item in root.GetProperty("records").EnumerateArray())
        {
            Assert.True(InputRecord.TryParseV3(Bytes(item, "hex"), out var record), item.GetProperty("name").GetString());
            Assert.Equal(item.GetProperty("type").GetByte(), record.Type);
            Assert.Equal(root.GetProperty("sequence").GetUInt32(), record.Sequence);
            Assert.Equal(root.GetProperty("captured").GetUInt32(), record.Captured);
            Assert.Equal(record.Type == InputRecord.Ping ? 0ul : Number(root, "capture"), record.Capture);
            switch (record.Type)
            {
                case InputRecord.Motion: Assert.Equal(-2, record.Dx); Assert.Equal(300, record.Dy); break;
                case InputRecord.Button: Assert.Equal(2, record.Button); Assert.True(record.Down); break;
                case InputRecord.Keyboard: Assert.Equal(0x82, record.Modifiers); Assert.Equal(new byte[] { 4, 0x4C }, record.Keys); break;
                case InputRecord.Consumer: Assert.Equal(0xE9, record.Usage); break;
                case InputRecord.Scroll: Assert.Equal(-3, record.Wheel); Assert.Equal(1, record.Pan); break;
            }
        }
    }

    [Fact]
    public void UnscopedTruncatedExtendedAndZeroCaptureInputCannotReachInjection()
    {
        using var fixture = Fixture();
        foreach (var item in fixture.RootElement.GetProperty("records").EnumerateArray())
        {
            var bytes = Bytes(item, "hex");
            for (var length = 0; length < bytes.Length; length++)
                Assert.False(InputRecord.TryParseV3(bytes.AsSpan(0, length), out _));
            Assert.False(InputRecord.TryParseV3([.. bytes, 0], out _));
            var invalidCapture = bytes.ToArray();
            if (item.GetProperty("type").GetByte() == InputRecord.Ping) invalidCapture[9] = 1;
            else Array.Clear(invalidCapture, 9, 8);
            Assert.False(InputRecord.TryParseV3(invalidCapture, out _));
        }
    }

    [Fact]
    public void WindowsAcknowledgementIncludesCurrentOwnershipAndSuppressesOldLeave()
    {
        using var fixture = Fixture();
        var root = fixture.RootElement;
        var leave = InputRecord.LeaveFlags(0xAB, 3, 0xBEEF);
        Assert.Equal(Bytes(root, "acknowledgement"),
            InputRecord.ScopedAck(16, 32, true, leave, Number(root, "capture"), Number(root, "ownership"), true, true));
        var revoked = InputRecord.ScopedAck(16, 32, false, leave, Number(root, "capture"), Number(root, "ownership"), false);
        Assert.Equal(0u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(revoked.AsSpan(8)));
        Assert.Equal(44, InputRecord.ScopedSealedAckLength);
    }

    [Fact]
    public void ControlEntryExitResumeAndAcknowledgementShareTheCaptureIdentity()
    {
        using var fixture = Fixture();
        var root = fixture.RootElement; var control = root.GetProperty("control"); var capture = Number(root, "capture");
        Assert.True(CaptureProtocol.TryReadEnter(Protocol.Message.Enter, Bytes(control, "enter"), out var edge));
        Assert.Equal(new CaptureEntry(0xAB, 3, 0xBEEF, false, capture), edge);
        Assert.True(CaptureProtocol.TryReadEnter(Protocol.Message.EnterCenter, Bytes(control, "center"), out var center));
        Assert.Equal(new CaptureEntry(0xAB, 3, 0, true, capture), center);
        Assert.True(CaptureProtocol.TryReadExit(Bytes(control, "exit"), out var id, out var exiting));
        Assert.Equal((byte)0xAB, id); Assert.Equal(capture, exiting);
        Assert.True(CaptureProtocol.TryReadResume(Bytes(control, "resume"), out id, out var side, out var resuming));
        Assert.Equal((byte)0xAB, id); Assert.Equal((byte)3, side); Assert.Equal(capture, resuming);
        Assert.Equal(Bytes(control, "acknowledge"), CaptureProtocol.Acknowledge(0xAB, true, -2, 300, 0, capture));
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptureProtocol.Acknowledge(1, true, 0, 0, 0, 0));
    }

    [Fact]
    public void ControlRecordsRejectLegacyLengthsZeroCaptureAndInvalidEdges()
    {
        using var fixture = Fixture();
        var control = fixture.RootElement.GetProperty("control");
        foreach (var (name, type) in new[] { ("enter", Protocol.Message.Enter), ("center", Protocol.Message.EnterCenter) })
        {
            var bytes = Bytes(control, name);
            for (var size = 0; size < bytes.Length; size++) Assert.False(CaptureProtocol.TryReadEnter(type, bytes.AsSpan(0, size), out _));
            Assert.False(CaptureProtocol.TryReadEnter(type, [.. bytes, 0], out _));
            bytes[1] = 4; Assert.False(CaptureProtocol.TryReadEnter(type, bytes, out _)); bytes[1] = 3;
            Array.Clear(bytes, bytes.Length - 8, 8); Assert.False(CaptureProtocol.TryReadEnter(type, bytes, out _));
        }
        Assert.False(CaptureProtocol.TryReadEnter(Protocol.Message.Resume, Bytes(control, "resume"), out _));
        Assert.False(CaptureProtocol.TryReadExit(new byte[9], out _, out _));
        Assert.False(CaptureProtocol.TryReadResume(new byte[10], out _, out _, out _));
        Assert.False(CaptureProtocol.TryReadExit([0xAB], out _, out _));
        Assert.False(CaptureProtocol.TryReadResume([0xAB, 3], out _, out _, out _));
    }

    [Fact]
    public void ClipboardSnapshotInteroperatesWithoutRoundingOwnership()
    {
        using var fixture = Fixture();
        var root = fixture.RootElement;
        var request = ClipboardSnapshotRequest.Parse(Encoding.UTF8.GetBytes(root.GetProperty("clipboardRequest").GetRawText()));
        var snapshot = ClipboardSnapshot.Parse(Encoding.UTF8.GetBytes(root.GetProperty("clipboardSnapshot").GetRawText()));
        Assert.Equal(Number(root, "ownership"), request.Generation);
        Assert.Equal(request.Generation, snapshot.Generation);
        Assert.Equal("hello\n🚀", ClipboardTransfer.Decode(snapshot.Text!));
    }

    [Fact]
    public async Task MaximumClipboardTextSurvivesEncryptedControlAndHelperFraming()
    {
        var snapshot = new ClipboardSnapshot(7, 9, 12, "text", Enumerable.Repeat((byte)'x', ClipboardTransfer.MaximumBytes).ToArray());
        var bytes = snapshot.Encode();
        using var sender = new FileNetworkCrypto(new byte[32], new byte[32], new byte[32], "server", "control");
        using var receiver = new FileNetworkCrypto(new byte[32], new byte[32], new byte[32], "server", "control");
        var wire = ControlLink.Frame(sender, 1, (byte)Protocol.Message.ClipboardSnapshot, bytes);
        Assert.Equal(wire.Length - 4, ControlLink.SealedLength(wire));
        var packet = ControlLink.Open(receiver, wire.AsSpan(4).ToArray());
        Assert.Equal(bytes, packet.Payload);
        using var pipe = new MemoryStream();
        var message = new DesktopMessage("clipboard-snapshot", Source: 1, Ownership: 9, Epoch: 7, Clipboard: packet.Payload);
        await DesktopPipe.Write(pipe, message, CancellationToken.None);
        pipe.Position = 0;
        var read = await DesktopPipe.Read(pipe, CancellationToken.None);
        Assert.Equal(snapshot.Text, ClipboardSnapshot.Parse(read.Clipboard!).Text);
    }
}
