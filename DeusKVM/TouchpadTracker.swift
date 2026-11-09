import Foundation

/// One contact as the MultitouchSupport framework reports it: the identifier it keeps for the
/// finger's lifetime, its state, and its position normalized to 0...1 with the origin at the
/// bottom left of the pad.
struct TouchpadRawContact: Sendable, Equatable {
    var identifier: Int32
    var state: Int32
    var x: Float
    var y: Float

    /// States 3 and 4 are a finger on the surface (the probe saw both while touching); 1 and 2
    /// are arriving or hovering, 5 to 7 lifting or gone.
    var isTouching: Bool {
        state == 3 || state == 4
    }
}

/// Turns raw frames into the contacts the touchpad collection reports and remembers which
/// identifiers are down, so every lift is reported exactly once at the finger's last known
/// position and nothing stays pressed on Windows.
struct TouchpadTracker: Sendable, Equatable {
    /// The framework's y axis points up and a Windows digitizer's points down. The first
    /// thing to check on hardware: inverted vertical motion means this is wrong.
    static let flipsY = true

    private var down: [UInt8: TouchpadContact] = [:]
    /// Identifiers in the order they landed; reports keep this order.
    private var order: [UInt8] = []

    var isTouching: Bool {
        !order.isEmpty
    }

    /// The contacts to report for one raw frame: each finger still down at its new position,
    /// one tip-off report for each finger that lifted or vanished, then fingers that landed
    /// while fewer than `TouchpadReport.maximumContacts` are tracked. Empty when nothing is
    /// down and nothing landed. With `hidingLoneFinger` a single finger counts as lifted: the
    /// network pointer path carries it, and Windows only sees two or more (its gestures).
    mutating func track(_ frame: [TouchpadRawContact], hidingLoneFinger: Bool = false) -> [TouchpadContact] {
        var touching: [UInt8: TouchpadRawContact] = [:]
        var arrivals: [UInt8] = []
        for contact in frame where contact.isTouching {
            let id = Self.identifier(contact.identifier)
            if touching.updateValue(contact, forKey: id) == nil { arrivals.append(id) }
        }
        if hidingLoneFinger, touching.count <= 1 { touching.removeAll(); arrivals.removeAll() }
        var reports: [TouchpadContact] = []
        for id in order {
            guard var known = down[id] else { continue }
            if let contact = touching[id] {
                (known.x, known.y) = Self.position(contact)
                down[id] = known
            } else {
                known.tip = false
                down[id] = nil
            }
            reports.append(known)
        }
        order.removeAll { down[$0] == nil }
        for id in arrivals where down[id] == nil && order.count < TouchpadReport.maximumContacts {
            guard let contact = touching[id] else { continue }
            let (x, y) = Self.position(contact)
            let landed = TouchpadContact(id: id, x: x, y: y)
            down[id] = landed
            order.append(id)
            reports.append(landed)
        }
        return reports
    }

    /// Tip-off reports for every finger still down, for the end of a session.
    mutating func lift() -> [TouchpadContact] {
        let lifted = order.compactMap { id -> TouchpadContact? in
            guard var contact = down[id] else { return nil }
            contact.tip = false
            return contact
        }
        down.removeAll()
        order.removeAll()
        return lifted
    }

    /// Contact Identifier is 0...127 in the descriptor.
    static func identifier(_ raw: Int32) -> UInt8 {
        UInt8(truncatingIfNeeded: raw & 0x7F)
    }

    static func position(_ contact: TouchpadRawContact) -> (UInt16, UInt16) {
        (logical(contact.x), logical(flipsY ? 1 - contact.y : contact.y))
    }

    /// 0...1 onto the descriptor's 0...4095, clamped; a non-finite value lands at 0.
    static func logical(_ value: Float) -> UInt16 {
        guard value.isFinite else { return 0 }
        return UInt16((min(max(Double(value), 0), 1) * Double(TouchpadReport.logicalMaximum)).rounded())
    }
}

/// What the input tap needs to know about the pad when a pointer event arrives.
enum TouchpadPresence: Sendable, Equatable {
    /// No finger for longer than the grace period: the event comes from another device.
    case none
    /// Every finger lifted within the grace period: macOS may still be emitting its tail.
    case recent
    /// Fingers on the surface, with their count.
    case touching(Int)
}

/// Presence derived from raw frames, with a grace period after the last lift that absorbs the
/// pointer events macOS still emits once a finger has left (tap-to-click among them).
struct TouchpadActivity: Sendable, Equatable {
    static let grace: TimeInterval = 0.15

    private(set) var fingers = 0
    private(set) var liftedAt: TimeInterval?

    mutating func update(touching next: Int, now: TimeInterval) {
        if fingers > 0, next == 0 { liftedAt = now }
        fingers = next
    }

    func presence(now: TimeInterval) -> TouchpadPresence {
        if fingers > 0 { return .touching(fingers) }
        if let liftedAt, now - liftedAt < Self.grace { return .recent }
        return .none
    }
}

/// One session of live frames on the Bluetooth queue: the pad's button and the contacts last
/// reported touching (so a button change can go out as a frame of its own) and the counts
/// for the session's diagnostics line.
struct TouchpadStream: Sendable, Equatable {
    var streaming = false
    var button = false
    var contacts: [TouchpadContact] = []
    var frames = 0
    var idle = 0
    var reports = 0
    var deferred = 0
    var drops = 0
    var lifts = 0
    var buttons = 0

    /// Absolute positions tolerate a lost frame when the link is backed up; a lift or a button
    /// change does not, since Windows would keep the finger or button down.
    static func droppable(_ frame: TouchpadFrame, button: Bool) -> Bool {
        frame.button == button && frame.contacts.allSatisfy(\.tip)
    }

    var summary: String {
        "frames=\(frames) idle=\(idle) reports=\(reports) deferred=\(deferred) drops=\(drops) lifts=\(lifts) buttons=\(buttons)"
    }
}
