import CoreGraphics
import Foundation

/// Monotonic deadlines; an idle tap has no scheduled wakeup.
enum TapWorkSchedule {
    static func deadline(now: TimeInterval, handoffReady: Bool, probeStarted: TimeInterval?) -> TimeInterval? {
        let handoff = handoffReady ? now + 0.02 : nil
        let probe = probeStarted.map { $0 + 1 }
        if let handoff, let probe { return min(handoff, probe) }
        return handoff ?? probe
    }

    static func needsCursorCorrection(location: CGPoint, parkingPoint: CGPoint) -> Bool {
        location != parkingPoint
    }
}

/// On the Mac, motion events only matter near the switch edge. Each tapped event costs about
/// 100 µs of macOS's tap round trip; a poll of the cursor costs about 45 µs a wake. So away from
/// the edge the tap thread polls instead, each time after the shortest time in which the cursor
/// could reach the edge at `topSpeed`, and within `band` of the edge (where polls would come
/// faster than a trackpad's events) the motion tap runs.
enum PointerProximity {
    /// Points per second; faster than a flick across a 5K display. A cursor that does outrun
    /// it is found at the edge by the next poll and crosses then.
    static let topSpeed = 30000.0
    static let shortestPoll: TimeInterval = 0.012
    static let longestPoll: TimeInterval = 0.1

    static var band: Double {
        topSpeed * shortestPoll
    }

    /// The next poll's delay, or nil where the motion tap should run instead.
    static func pollInterval(distance: Double) -> TimeInterval? {
        guard distance > band else { return nil }
        return min(longestPoll, distance / topSpeed)
    }
}

/// How local pointer motion is followed: by the motion tap, by polling the cursor, or not at all.
enum MotionGate {
    case off, tap, poll
}

/// The event types of InputTap's three taps.
enum TapMask {
    static let main = mask([
        .keyDown, .keyUp, .flagsChanged, .leftMouseDown, .leftMouseUp, .rightMouseDown, .rightMouseUp,
        .otherMouseDown, .otherMouseUp, MediaKeyEvent.eventType
    ])
    static let motion = mask([.mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged])
    static let remote = TrackpadGesture.eventMask | mask([.scrollWheel])

    private static func mask(_ types: [CGEventType]) -> CGEventMask {
        types.reduce(0) { $0 | CGEventMask(1) << CGEventMask($1.rawValue) }
    }
}

struct TapConfiguration: Equatable, Sendable {
    var targetAvailable = false
    var edgeEnabled = false
    var geometry: EdgeGeometry?
    var shortcut = ToggleShortcut()
}

enum TapOutput: Sendable {
    case installed(Int, Bool)
    case keyboardMonitoring(Int, Bool)
    case begin(Int, CGPoint, fromEdge: Bool)
    case end(Int)
    case disabled
    /// A diagnostic line for the coordinator's log; counts only.
    case note(String)
}
