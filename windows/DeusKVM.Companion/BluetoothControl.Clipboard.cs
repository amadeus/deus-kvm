using System.Text.Json;
using DeusKVM.Companion.Core;

namespace DeusKVM.Companion;

internal sealed partial class BluetoothControl
{
    private ClipboardTransfer? clipboardTransfer;
    private bool filesPeer, fileReceivePeer;
    private uint? peerClipboardSequence, peerClipboardRevision, deliveredFileSequence;
    private byte[]? pendingFileOffer;
    private ClipboardTransfer Clipboard => clipboardTransfer ??= new ClipboardTransfer(SendClipboard, ApplyClipboard);
    private bool clipboardDefaultDesktop, clipboardPeer, clipboardDesktopAvailable, clipboardOffered, clipboardEnabled, clipboardPrimed;
    private uint clipboardEpoch = (uint)Random.Shared.Next(1, int.MaxValue), clipboardRevision;
    private byte[]? pendingClipboardOffer;
    private static double ClipboardNow => Environment.TickCount64 / 1000d;

    private void UpdateClipboardSession(bool reset = false)
    {
        var wanted = active && ready && clipboardPeer && clipboardDefaultDesktop && clipboardDesktopAvailable && desktop?.Connected == true;
        if (!reset && clipboardOffered == wanted) return;
        clipboardEpoch++; clipboardOffered = wanted; clipboardEnabled = clipboardPrimed = false;
        pendingClipboardOffer = null; pendingFileOffer = null; peerClipboardSequence = peerClipboardRevision = deliveredFileSequence = null; Clipboard.Reset(clipboardEpoch);
        desktop?.Send(new DesktopMessage("clipboard-start", Epoch: clipboardEpoch, Ok: false));
        AdvertiseClipboard();
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
            desktop?.Send(new DesktopMessage("clipboard-start", Epoch: clipboardEpoch, Ok: true));
        }
        else if (clipboardEnabled)
        {
            if (type == Protocol.Message.ClipGrab && payload.Length == 12 && ClipboardTransfer.Read(payload, 0) == clipboardEpoch &&
                peerClipboardSequence != ClipboardTransfer.Read(payload, 4))
            {
                peerClipboardSequence = ClipboardTransfer.Read(payload, 4);
                peerClipboardRevision = clipboardPrimed ? clipboardRevision : null;
                deliveredFileSequence = null;
                desktop?.Send(new DesktopMessage("clipboard-file-clear", Epoch: clipboardEpoch));
            }
            if (!clipboardPrimed)
            {
                if (type == Protocol.Message.ClipGrab && payload.Length == 12) pendingClipboardOffer = payload;
                return;
            }
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
            if (!clipboardPrimed && peerClipboardSequence is not null) peerClipboardRevision = message.Revision;
            clipboardRevision = message.Revision;
            Clipboard.Observe(message.Clipboard, message.Ok);
            clipboardPrimed = true;
            if (pendingClipboardOffer is { } offer)
            {
                pendingClipboardOffer = null;
                try { Clipboard.Receive(Protocol.Message.ClipGrab, offer, ClipboardNow); }
                catch (InvalidDataException) { Fail("Invalid clipboard offer"); }
            }
            if (pendingFileOffer is { } file) { pendingFileOffer = null; ReceiveFile(Protocol.Message.FileOffer, file); }
        }
        else if (message.Kind == "clipboard-file-copy" && fileReceivePeer && message.Ok && message.Revision == clipboardRevision && message.Clipboard is { } fileBytes)
        {
            var local = FileClipboardOffer.Parse(fileBytes);
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
                desktop?.Send(new DesktopMessage("clipboard-file-send", Epoch: clipboardEpoch, Clipboard: payload));
            return;
        }
        if (type == Protocol.Message.FileOffer)
        {
            FileClipboardOffer offer;
            try { offer = FileClipboardOffer.Parse(payload); }
            catch (InvalidDataException) { return; } // Unsupported names/sizes must not disconnect input.
            if (offer.Epoch != clipboardEpoch || offer.ClipboardSequence != peerClipboardSequence) return;
            if (!clipboardPrimed) { pendingFileOffer = payload; return; }
            if (deliveredFileSequence == offer.Sequence) return;
            deliveredFileSequence = offer.Sequence;
            desktop?.Send(new DesktopMessage("clipboard-file-offer", Epoch: clipboardEpoch,
                Revision: peerClipboardRevision ?? clipboardRevision, Clipboard: payload));
        }

    }

    private void ClipboardSwitch(bool windowsActive)
    {
        Clipboard.SetActive(windowsActive, ClipboardNow);
        if (!windowsActive && clipboardEnabled)
            desktop?.Send(new DesktopMessage("clipboard-yield", Epoch: clipboardEpoch));
    }
    private void SendClipboard(Protocol.Message type, byte[] data)
    {
        if (!clipboardEnabled || data.Length > (type == Protocol.Message.FileOffer ? 4096 : ClipboardTransfer.BlockBytes + 12)) return;
        if (type is Protocol.Message.ClipData or Protocol.Message.FileOffer) Enqueue(type, 1, data);
        else Send(type, data);
    }
    private void ApplyClipboard(byte[] data)
    {
        if (clipboardEnabled && clipboardPrimed)
            desktop?.Send(new DesktopMessage("clipboard-apply", Epoch: clipboardEpoch, Revision: clipboardRevision, Clipboard: data));
    }
}
