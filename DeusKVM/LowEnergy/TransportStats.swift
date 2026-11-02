import Foundation

/// Aggregate counts from the Bluetooth queue, sampled on the main thread for capture health.
/// Counts and durations only; reset by each `takeInterval`.
final class TransportStats: @unchecked Sendable {
    private let lock = NSLock()
    private var interval = TransportInterval()
    private var pending = 0
    private var companionQueued = 0
    private var companionBlocked = false
    /// CoreBluetooth refused an update and has not yet reported ready again.
    private var stallOrigin: TimeInterval?
    private var stallCounted: TimeInterval = 0

    func input(delay: TimeInterval) {
        lock.withLock {
            interval.events += 1
            interval.maxDispatchMs = max(interval.maxDispatchMs, delay * 1000)
        }
    }

    func notification(rejected wasRejected: Bool = false, merged wasMerged: Bool, pending count: Int) {
        lock.withLock {
            if wasRejected { interval.hidRejected += 1 }
            if wasMerged { interval.hidMerged += 1 }
            setPendingLocked(count)
        }
    }

    func sent() {
        lock.withLock { interval.hidSent += 1 }
    }

    func setPending(_ count: Int) {
        lock.withLock { setPendingLocked(count) }
    }

    private func setPendingLocked(_ count: Int) {
        pending = count
        interval.hidPendingMax = max(interval.hidPendingMax, count)
    }

    func companion(queued: Int, blocked: Bool) {
        lock.withLock { companionQueued = queued; companionBlocked = blocked }
    }

    func companionSent(bytes: Int) {
        lock.withLock { interval.framesOut += 1; interval.bytesOut += bytes }
    }

    func companionReceived(frames: Int, bytes: Int) {
        lock.withLock { interval.framesIn += frames; interval.bytesIn += bytes }
    }

    func transmitBlocked(at now: TimeInterval) {
        lock.withLock {
            guard stallOrigin == nil else { return }
            stallOrigin = now; stallCounted = now
        }
    }

    func transmitReady(at now: TimeInterval) {
        lock.withLock {
            guard let origin = stallOrigin else { return }
            interval.stallMs += max(0, now - stallCounted) * 1000
            interval.stallMaxMs = max(interval.stallMaxMs, (now - origin) * 1000)
            stallOrigin = nil
        }
    }

    /// Counters since the previous call. An ongoing stall contributes its time so far.
    func takeInterval(now: TimeInterval) -> TransportInterval {
        lock.withLock {
            var result = interval
            if let origin = stallOrigin {
                result.stallMs += max(0, now - stallCounted) * 1000
                result.stallMaxMs = max(result.stallMaxMs, (now - origin) * 1000)
                stallCounted = now
            }
            result.hidPending = pending
            result.companionQueued = companionQueued
            result.companionBlocked = companionBlocked
            interval = TransportInterval()
            return result
        }
    }

    var companionState: (queued: Int, blocked: Bool) {
        lock.withLock { (companionQueued, companionBlocked) }
    }

    /// The current stall's age, for a problem report.
    func stallAge(now: TimeInterval) -> TimeInterval? {
        lock.withLock { stallOrigin.map { now - $0 } }
    }
}
