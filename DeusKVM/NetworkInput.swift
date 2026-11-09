import Foundation

/// Pointer input the tap routed to the local-network stream instead of the Bluetooth mouse.
enum NetworkInputEvent: Sendable, Equatable {
    case motion(Int32, Int32)
    case button(MouseButtons, Bool)
}

/// What the PC needs to connect to the Mac's input listener: the same shape as the file
/// network offer, sent over the Bluetooth companion link as `inputOffer`.
struct InputNetworkOffer: Codable, Sendable, Equatable {
    let hosts: [String]
    let port: UInt16
    let key: String
}

/// What the stream can do for the tap right now.
enum NetworkCarrier: Sendable, Equatable {
    /// No connection: Bluetooth as before.
    case down
    /// Connected and the PC injects: pointer events go over the network.
    case up
    /// Connected but the PC cannot inject (a UAC prompt or the lock screen holds the secure
    /// desktop): everything goes over the Bluetooth mouse, which Windows takes anywhere.
    case unavailable
}

/// The PC's acknowledgement: everything up to `sequence` arrived at `received` on its clock,
/// and whether it could inject at that moment.
struct InputAck: Sendable, Equatable {
    let sequence: UInt32
    let received: UInt32
    let blocked: Bool
}

/// The wire records of the input stream (NETWORK_POINTER_PLAN.md). Little-endian; each Mac to
/// PC record is sealed with AES-GCM and prefixed by its sealed length in one byte; the PC answers
/// with a sealed 12-byte acknowledgement (cumulative) per read.
enum InputRecord {
    static let motion: UInt8 = 1
    static let button: UInt8 = 2
    static let ping: UInt8 = 3
    static let ackLength = 12
    static let ackBlockedFlag: UInt32 = 1
    /// A sealed acknowledgement: the record plus the 16-byte tag.
    static let sealedAckLength = ackLength + 16

    static func motion(sequence: UInt32, captured: UInt32, dx: Int32, dy: Int32) -> Data {
        var data = Data([motion])
        data += u32(sequence) + u32(captured)
        data += i16(dx) + i16(dy)
        return data
    }

    static func button(sequence: UInt32, captured: UInt32, button: MouseButtons, down: Bool) -> Data {
        var data = Data([Self.button])
        data += u32(sequence) + u32(captured)
        data += Data([buttonCode(button), down ? 1 : 0])
        return data
    }

    static func ping(sequence: UInt32, captured: UInt32) -> Data {
        Data([ping]) + u32(sequence) + u32(captured)
    }

    static func ack(_ data: Data) -> InputAck? {
        guard data.count == ackLength else { return nil }
        return InputAck(sequence: read32(data, 0), received: read32(data, 4), blocked: read32(data, 8) & ackBlockedFlag != 0)
    }

    /// 1 left, 2 right, 3 middle; the PC maps them to its button flags.
    static func buttonCode(_ button: MouseButtons) -> UInt8 {
        if button.contains(.left) { return 1 }
        if button.contains(.right) { return 2 }
        return 3
    }

    /// Uptime in 100 µs ticks, wrapping at 32 bits (about 119 hours).
    static func clock(uptime: TimeInterval) -> UInt32 {
        UInt32(truncatingIfNeeded: Int64((uptime * 10000).rounded(.down)))
    }

    private static func u32(_ value: UInt32) -> Data {
        Data([UInt8(value & 0xFF), UInt8((value >> 8) & 0xFF), UInt8((value >> 16) & 0xFF), UInt8(value >> 24)])
    }

    private static func i16(_ value: Int32) -> Data {
        let clamped = UInt16(bitPattern: Int16(clamping: value))
        return Data([UInt8(clamped & 0xFF), UInt8(clamped >> 8)])
    }

    private static func read32(_ data: Data, _ offset: Int) -> UInt32 {
        let base = data.startIndex + offset
        return UInt32(data[base]) | UInt32(data[base + 1]) << 8 | UInt32(data[base + 2]) << 16 | UInt32(data[base + 3]) << 24
    }
}

/// One connection's flow control and counters. Motion is sent as it comes while the PC keeps
/// up; once the oldest unacknowledged record is older than `maxLag` (the PC is behind), motion
/// accumulates into one pending delta that leaves with the next acknowledgement. Buttons
/// always go out at once, after any pending motion, so nothing a button depends on is
/// reordered. Without an acknowledgement for `stallAfter` the stream is stalled and the caller
/// closes it. The PC's receive clock in each acknowledgement, against the capture clock the
/// record carried, gives the one-way jitter of delivery (clock offsets cancel).
struct InputStreamState: Sendable, Equatable {
    static let maxLag: TimeInterval = 0.04
    static let maxInflight = 256
    static let stallAfter: TimeInterval = 1
    static let pingAfter: TimeInterval = 1
    /// Motion gaps longer than this are a lifted finger, not delivery jitter.
    static let strokeGap: TimeInterval = 0.1

    private(set) var lastSent: UInt32 = 0
    private(set) var lastAcked: UInt32 = 0
    private var pendingX: Int32 = 0
    private var pendingY: Int32 = 0
    private var hasPending = false
    private var sentAt: [UInt32: TimeInterval] = [:]
    private var lastAckAt: TimeInterval?
    private var lastSendAt: TimeInterval?
    private var lastMotionAt: TimeInterval?
    private(set) var frames = 0
    private(set) var buttons = 0
    private(set) var coalesced = 0
    private(set) var stalls = 0
    private var rttSum = 0.0
    private var rttCount = 0
    private var rttMax = 0.0
    private var gapMax = 0.0
    private var deliveryMin: Int32?
    private var deliveryMax: Int32?

    var inflight: Int {
        Int(lastSent &- lastAcked)
    }

    /// The PC has not kept up: hold motion until it acknowledges.
    func behind(now: TimeInterval) -> Bool {
        inflight >= Self.maxInflight || sentAt.values.min().map { now - $0 > Self.maxLag } ?? false
    }

    /// The record to send now, or nothing if it was folded into the pending delta.
    mutating func motion(dx: Int32, dy: Int32, now: TimeInterval) -> Data? {
        if let last = lastMotionAt, now - last <= Self.strokeGap { gapMax = max(gapMax, (now - last) * 1000) }
        lastMotionAt = now
        guard !hasPending, !behind(now: now) else {
            pendingX &+= dx; pendingY &+= dy; hasPending = true; coalesced += 1
            return nil
        }
        frames += 1
        return next(now: now) { InputRecord.motion(sequence: $0, captured: $1, dx: dx, dy: dy) }
    }

    /// Any pending motion first, then the button; never deferred.
    mutating func button(_ button: MouseButtons, down: Bool, now: TimeInterval) -> [Data] {
        var records: [Data] = []
        if let pending = flushPending(now: now) { records.append(pending) }
        buttons += 1
        records.append(next(now: now) { InputRecord.button(sequence: $0, captured: $1, button: button, down: down) })
        return records
    }

    /// The PC acknowledged everything up to `sequence`, received at `received` on its clock:
    /// the pending delta goes out if the PC has caught up.
    mutating func acked(_ sequence: UInt32, received: UInt32 = 0, now: TimeInterval) -> Data? {
        let ahead = sequence &- lastAcked
        guard ahead > 0, ahead <= lastSent &- lastAcked else { return nil }
        lastAcked = sequence
        lastAckAt = now
        if let sent = sentAt.removeValue(forKey: sequence) {
            let rtt = (now - sent) * 1000
            rttSum += rtt; rttCount += 1; rttMax = max(rttMax, rtt)
            // Clock offset plus one-way latency, in 100 µs; its spread is the delivery jitter.
            let delivery = Int32(bitPattern: received &- InputRecord.clock(uptime: sent))
            deliveryMin = min(deliveryMin ?? delivery, delivery)
            deliveryMax = max(deliveryMax ?? delivery, delivery)
        }
        sentAt = sentAt.filter { $0.key &- sequence <= UInt32(Self.maxInflight) }
        guard hasPending, !behind(now: now) else { return nil }
        return flushPending(now: now)
    }

    /// True once records have waited longer than `stallAfter` without an acknowledgement.
    func stalled(now: TimeInterval) -> Bool {
        guard inflight > 0, let sent = sentAt.values.min() else { return false }
        return now - sent > Self.stallAfter
    }

    /// A ping to keep the stream measured while the pointer rests.
    mutating func ping(now: TimeInterval) -> Data? {
        guard inflight == 0, now - (lastSendAt ?? -.infinity) >= Self.pingAfter else { return nil }
        return next(now: now) { InputRecord.ping(sequence: $0, captured: $1) }
    }

    mutating func noteStall() {
        stalls += 1
    }

    var summary: String {
        let rttAverage = rttCount == 0 ? 0 : rttSum / Double(rttCount)
        let jitter = deliveryMin.flatMap { low in deliveryMax.map { Double($0 - low) / 10 } } ?? 0
        return "frames=\(frames) buttons=\(buttons) coalesced=\(coalesced) stalls=\(stalls) " +
            "rttAvgMs=\(String(format: "%.1f", rttAverage)) rttMaxMs=\(String(format: "%.1f", rttMax)) " +
            "jitterMs=\(String(format: "%.1f", jitter)) gapMaxMs=\(String(format: "%.1f", gapMax)) inflight=\(inflight)"
    }

    private mutating func flushPending(now: TimeInterval) -> Data? {
        guard hasPending else { return nil }
        let (dx, dy) = (pendingX, pendingY)
        pendingX = 0; pendingY = 0; hasPending = false
        frames += 1
        return next(now: now) { InputRecord.motion(sequence: $0, captured: $1, dx: dx, dy: dy) }
    }

    private mutating func next(now: TimeInterval, _ build: (UInt32, UInt32) -> Data) -> Data {
        lastSent &+= 1
        sentAt[lastSent] = now
        lastSendAt = now
        return build(lastSent, InputRecord.clock(uptime: now))
    }
}
