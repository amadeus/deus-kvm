namespace DeusKVM.Companion.Core;

public static class Protocol
{
    // MaximumPayload is the largest message; each travels whole in one ControlLink frame.
    public const int Version = 1, MaximumPayload = 131072;
    public enum Message : byte
    {
        Hello = 1, Ping = 2, Pong = 3, Screens = 4, Config = 5,
        Enter = 0x11, EnterAck = 0x12, Leave = 0x13, State = 0x14, Exit = 0x15, Resume = 0x16, EnterCenter = 0x17,
        Availability = 0x18, Selection = 0x19,
        ClipGrab = 0x20, ClipGet = 0x21, ClipData = 0x22, ClipState = 0x23, FileOffer = 0x24, FileGet = 0x25, FileData = 0x26, FileAccept = 0x27,
        // Problem report: the Mac asks (control stream, u32 token); Windows answers on the bulk
        // stream with the token and UTF-8 diagnostic text. Offered via HELLO `report: 1`.
        ReportRequest = 0x30, Report = 0x31,
        // The Mac's local-network pointer listener (bulk stream, JSON like a file offer's
        // network part). Offered via HELLO `inputNetwork: 1`; 2 also takes keyboard, media-key and
        // scroll records on the stream.
        InputOffer = 0x32, ClipboardSnapshot = 0x52, Nack = 0x7F
    }
    public sealed record Packet(byte Stream, byte Type, byte[] Payload);
}
