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
