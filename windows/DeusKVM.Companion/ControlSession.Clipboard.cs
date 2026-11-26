using System.Text.Json;
using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

internal sealed partial class ControlSession
{
    private ClipboardTransfer? clipboardTransfer;
    private bool filesPeer, fileReceivePeer, imagesPeer;
    private ClipboardTransfer Clipboard => clipboardTransfer ??= new ClipboardTransfer(SendClipboard, ApplyClipboard);
    private bool clipboardDefaultDesktop, clipboardPeer, clipboardDesktopAvailable, clipboardOffered, clipboardEnabled, clipboardPrimed;
    private uint clipboardEpoch = (BitConverter.ToUInt32(RuntimeCompat.RandomBytes(4), 0) % (int.MaxValue - 1)) + 1, clipboardRevision;
    private static double ClipboardNow => RuntimeCompat.TickCount64 / 1000d;

    private void UpdateClipboardSession(bool reset = false)
    {
        var wanted = controlling && active && ready && clipboardPeer && clipboardDefaultDesktop && clipboardDesktopAvailable && clipboardHost.Connected == true;
        if (!reset && clipboardOffered == wanted) return;
        clipboardEpoch++; clipboardOffered = wanted; clipboardEnabled = clipboardPrimed = false;
        Clipboard.Reset(clipboardEpoch);
        clipboardHost.Send(new DesktopMessage("clipboard-start", Epoch: clipboardEpoch, Ok: false));
        AdvertiseClipboard();
        if (controlling)
        {
            clipboardHost.Send(new DesktopMessage("clipboard-select", Epoch: clipboardEpoch));
            RequestClipboardSnapshot();
        }
    }

    private void RequestClipboardSnapshot()
    {
        if (!controlling || !ready || handoff.Active is null) return;
        var request = new ClipboardSnapshotRequest(clipboardEpoch, checked((ulong)ownership));
        Send(Protocol.Message.ClipboardSnapshot, request.Encode());
    }

    private void ReceiveClipboardSnapshot(byte[] payload)
    {
        if (!controlling || !ready || handoff.Active is null) return;
        var snapshot = ClipboardSnapshot.Parse(payload);
        if (snapshot.Epoch != clipboardEpoch || snapshot.Generation != (ulong)ownership) return;
        clipboardHost.Send(new DesktopMessage("clipboard-snapshot", Epoch: clipboardEpoch, Clipboard: payload));
    }
    private void AdvertiseClipboard()
    {
        if (!ready || !clipboardPeer) return;
        var state = new byte[5];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(state, clipboardEpoch);
        state[4] = clipboardOffered ? (byte)1 : (byte)0;
        Send(Protocol.Message.ClipState, state);
    }
    private void ReceiveClipboard(Protocol.Message type, byte[] payload)
    {
        if (!clipboardPeer) throw new InvalidDataException("Clipboard capability not negotiated");
        if (type == Protocol.Message.ClipState)
        {
            if (payload.Length != 5 || payload[4] > 1) throw new InvalidDataException("Invalid clipboard state");
            if (ClipboardTransfer.Read(payload, 0) != clipboardEpoch) return;
            var enabled = clipboardOffered && payload[4] == 1;
            if (enabled == clipboardEnabled) return;
            if (!enabled) { UpdateClipboardSession(true); return; }
            clipboardEnabled = true; clipboardPrimed = false;
            Clipboard.Reset(clipboardEpoch);
            Clipboard.SetActive(handoff.Active is not null, ClipboardNow);
            clipboardHost.Send(new DesktopMessage("clipboard-start", Epoch: clipboardEpoch, Ok: true));
        }
        else if (clipboardEnabled)
        {
            // Typed controller snapshots supply all Mac-to-Windows data. Legacy requests/data
            // remain only for the opposite direction when this Mac returns locally.
            if (type is Protocol.Message.ClipGrab or Protocol.Message.ClipData) return;
            if (!clipboardPrimed) return;
            Clipboard.Receive(type, payload, ClipboardNow);
        }
    }
    private void ClipboardDesktopEvent(DesktopMessage message)
    {
        if (message.Kind == "clipboard-availability")
        {
            clipboardDesktopAvailable = message.Ok;
            UpdateClipboardSession();
            return;
        }
        if (!clipboardEnabled || message.Epoch != clipboardEpoch) return;
        if (message.Kind == "clipboard-copy")
        {
            clipboardRevision = message.Revision;
            Clipboard.Observe(message.Clipboard, message.Ok);
            clipboardPrimed = true;

        }
        else if (message.Kind == "clipboard-file-copy" && fileReceivePeer && message.Ok && message.Revision == clipboardRevision && message.Clipboard is { } fileBytes)
        {
            var local = FileClipboardOffer.Parse(fileBytes);
            if (local.IsImage && !imagesPeer) return;
            SendClipboard(Protocol.Message.FileOffer, JsonSerializer.SerializeToUtf8Bytes(local with { ClipboardSequence = Clipboard.Sequence }, FileClipboardOffer.WireJson));
        }
        else if (message.Kind == "clipboard-yield" && clipboardPrimed) Clipboard.Yield();
    }
    private void ReceiveFile(Protocol.Message type, byte[] payload)
    {
        if (!clipboardEnabled) return;
        if (type == Protocol.Message.FileAccept)
        {
            if (!fileReceivePeer) return;
            var request = FileClipboardOffer.Parse(payload);
            if (request.Epoch == clipboardEpoch && request.Sequence == clipboardRevision && request.Network?.Valid == true)
                clipboardHost.Send(new DesktopMessage("clipboard-file-send", Epoch: clipboardEpoch, Clipboard: payload));
            return;
        }
    }

    private void ClipboardSwitch(bool windowsActive)
    {
        Clipboard.SetActive(windowsActive, ClipboardNow);
        if (!windowsActive && controlling)
        {
            clipboardHost.Send(new DesktopMessage("clipboard-local", Epoch: clipboardEpoch));
            if (clipboardEnabled) clipboardHost.Send(new DesktopMessage("clipboard-yield", Epoch: clipboardEpoch));
        }
    }
    private void SendClipboard(Protocol.Message type, byte[] data)
    {
        if (!clipboardEnabled || data.Length > (type == Protocol.Message.FileOffer ? 4096 : ClipboardTransfer.BlockBytes + 12)) return;
        if (type is Protocol.Message.ClipData or Protocol.Message.FileOffer) Enqueue(type, 1, data);
        else Send(type, data);
    }
    private void ApplyClipboard(byte[] data)
    {
        // Incoming text is installed only through the controller-bound snapshot path.
    }
}
