import Darwin
import Foundation

/// Time from an input event's creation to the tap forwarding it. A slow tap callback makes
/// the window server queue events behind it; the input queue's delay cannot see that.
enum EventLag {
    private static let timebase: mach_timebase_info_data_t = {
        var info = mach_timebase_info_data_t()
        mach_timebase_info(&info)
        return info
    }()

    static let plausibleLimit: UInt64 = 10_000_000_000

    static func nanoseconds(timestamp: UInt64) -> UInt64? {
        nanoseconds(
            timestamp: timestamp, nowTicks: mach_absolute_time(), nowNanoseconds: clock_gettime_nsec_np(CLOCK_UPTIME_RAW),
            numer: timebase.numer, denom: timebase.denom
        )
    }

    /// Quartz documents event timestamps as nanoseconds, but some systems report mach ticks.
    /// Each reading of the other unit is far outside the plausible range, so take whichever fits.
    static func nanoseconds(timestamp: UInt64, nowTicks: UInt64, nowNanoseconds: UInt64, numer: UInt32, denom: UInt32) -> UInt64? {
        guard timestamp > 0, denom > 0 else { return nil }
        if nowNanoseconds >= timestamp, nowNanoseconds - timestamp <= plausibleLimit { return nowNanoseconds - timestamp }
        guard nowTicks >= timestamp else { return nil }
        let (scaled, overflow) = (nowTicks - timestamp).multipliedReportingOverflow(by: UInt64(numer))
        guard !overflow else { return nil }
        let value = scaled / UInt64(denom)
        return value <= plausibleLimit ? value : nil
    }
}

/// Tap-thread counters for forwarded remote events, sampled once per second.
final class TapStats: @unchecked Sendable {
    struct Interval: Equatable {
        var events = 0
        var measured = 0
        var lagSumMs = 0.0
        var lagMaxMs = 0.0
        var warps = 0
        /// Pointer motion events captured while remote, and the spacing between consecutive
        /// ones within a stroke (gaps over `strokeGap` are a lifted finger, not jitter; the pad
        /// delivers every 8 ms and a tap's lift-and-land takes well over 100 ms).
        var motion = 0
        var motionGaps = 0
        var motionGapSumMs = 0.0
        var motionGapMaxMs = 0.0
    }

    static let strokeGap: TimeInterval = 0.1
    private let lock = NSLock()
    private var current = Interval()
    private var lastMotionAt: TimeInterval?

    func event(lagNanoseconds: UInt64?) {
        lock.withLock {
            current.events += 1
            guard let lagNanoseconds else { return }
            let lag = Double(lagNanoseconds) / 1_000_000
            current.measured += 1
            current.lagSumMs += lag
            current.lagMaxMs = max(current.lagMaxMs, lag)
        }
    }

    func warp() {
        lock.withLock { current.warps += 1 }
    }

    /// One pointer motion event while remote; measures the pad's real delivery rate.
    func motion(now: TimeInterval) {
        lock.withLock {
            current.motion += 1
            if let last = lastMotionAt, now - last <= Self.strokeGap {
                let gap = (now - last) * 1000
                current.motionGaps += 1
                current.motionGapSumMs += gap
                current.motionGapMaxMs = max(current.motionGapMaxMs, gap)
            }
            lastMotionAt = now
        }
    }

    func take() -> Interval {
        lock.withLock {
            defer { current = Interval() }
            return current
        }
    }
}

/// Round trips over the companion control stream, using the existing PING/PONG pair.
/// One ping is outstanding at a time; a ping unanswered after `timeout` counts as lost.
struct PingTracker {
    struct Interval: Equatable {
        var replies = 0
        var rttSumMs = 0.0
        var rttMaxMs = 0.0
        var lost = 0
    }

    var interval: TimeInterval = 2
    var timeout: TimeInterval = 5
    private var token: UInt32 = 0
    private var outstanding: (token: UInt32, sentAt: TimeInterval)?
    private var lastSent = -TimeInterval.infinity
    private var current = Interval()

    /// The token to send now, if a ping is due.
    mutating func next(now: TimeInterval) -> UInt32? {
        if let outstanding {
            guard now - outstanding.sentAt >= timeout else { return nil }
            current.lost += 1
            self.outstanding = nil
        }
        guard now - lastSent >= interval else { return nil }
        token &+= 1
        outstanding = (token, now)
        lastSent = now
        return token
    }

    mutating func receive(_ value: UInt32, now: TimeInterval) {
        guard let outstanding, outstanding.token == value else { return }
        let rtt = (now - outstanding.sentAt) * 1000
        current.replies += 1
        current.rttSumMs += rtt
        current.rttMaxMs = max(current.rttMaxMs, rtt)
        self.outstanding = nil
    }

    func outstandingAge(now: TimeInterval) -> TimeInterval? {
        outstanding.map { now - $0.sentAt }
    }

    /// Stops tracking without counting a loss: capture ended or the companion went away.
    mutating func cancel() {
        outstanding = nil
    }

    mutating func take() -> Interval {
        defer { current = Interval() }
        return current
    }
}

/// Events the input queue forwarded and the longest wait from capture to forwarding (the tap
/// thread to `InputPipeline`), counted on the input queue and sampled on the main thread.
final class InputQueueStats: @unchecked Sendable {
    private let lock = NSLock()
    private var interval = InputQueueInterval()

    func input(delay: TimeInterval) {
        lock.withLock {
            interval.events += 1
            interval.maxDispatchMs = max(interval.maxDispatchMs, delay * 1000)
        }
    }

    /// Counters since the previous call.
    func takeInterval() -> InputQueueInterval {
        lock.withLock {
            defer { interval = InputQueueInterval() }
            return interval
        }
    }
}

/// One interval from `InputQueueStats`.
struct InputQueueInterval: Equatable {
    var events = 0
    var maxDispatchMs = 0.0
}

/// One per-second capture health sample while controlling Windows.
struct HealthSample {
    var date: Date
    var visible: Int32
    var parked: Bool
    var blind: [UInt8]
    var queue: InputQueueInterval
    var tap: TapStats.Interval
    var ping: PingTracker.Interval

    /// Keeps the original capture health fields first so older log readings still apply.
    var line: String {
        let lagAverage = Self.average(tap.lagSumMs, tap.measured)
        let fields: [String] = [
            "visible=\(visible)", "parked=\(parked)", "events=\(queue.events)", "maxDispatchMs=\(Int(queue.maxDispatchMs))",
            "blind=\(blind)",
            "tapLagMaxMs=\(Self.ms(tap.lagMaxMs))", "tapLagAvgMs=\(Self.ms(lagAverage))", "warps=\(tap.warps)",
            "rttMaxMs=\(Self.ms(ping.rttMaxMs))", "pingLost=\(ping.lost)",
            "motion=\(tap.motion)", "motionGapAvgMs=\(Self.ms(Self.average(tap.motionGapSumMs, tap.motionGaps)))",
            "motionGapMaxMs=\(Self.ms(tap.motionGapMaxMs))"
        ]
        return fields.joined(separator: " ")
    }

    static func average(_ sum: Double, _ count: Int) -> Double {
        count == 0 ? 0 : sum / Double(count)
    }

    static func ms(_ value: Double) -> String {
        String(format: "%.1f", value)
    }
}

/// Per-minute aggregate written to the diagnostics file while controlling Windows.
struct HealthSummary {
    private(set) var samples = 0
    private var queue = InputQueueInterval()
    private var parkedMisses = 0
    private var tap = TapStats.Interval()
    private var ping = PingTracker.Interval()

    mutating func add(_ sample: HealthSample) {
        samples += 1
        if !sample.parked { parkedMisses += 1 }
        queue.events += sample.queue.events
        queue.maxDispatchMs = max(queue.maxDispatchMs, sample.queue.maxDispatchMs)
        tap.events += sample.tap.events
        tap.measured += sample.tap.measured
        tap.lagSumMs += sample.tap.lagSumMs
        tap.lagMaxMs = max(tap.lagMaxMs, sample.tap.lagMaxMs)
        tap.warps += sample.tap.warps
        tap.motion += sample.tap.motion
        tap.motionGaps += sample.tap.motionGaps
        tap.motionGapSumMs += sample.tap.motionGapSumMs
        tap.motionGapMaxMs = max(tap.motionGapMaxMs, sample.tap.motionGapMaxMs)
        ping.replies += sample.ping.replies
        ping.rttSumMs += sample.ping.rttSumMs
        ping.rttMaxMs = max(ping.rttMaxMs, sample.ping.rttMaxMs)
        ping.lost += sample.ping.lost
    }

    var line: String {
        let lagAverage = HealthSample.average(tap.lagSumMs, tap.measured)
        let rttAverage = HealthSample.average(ping.rttSumMs, ping.replies)
        let fields: [String] = [
            "samples=\(samples)", "notParked=\(parkedMisses)", "events=\(queue.events)",
            "maxDispatchMs=\(Int(queue.maxDispatchMs))", "tapEvents=\(tap.events)",
            "tapLagMaxMs=\(HealthSample.ms(tap.lagMaxMs))", "tapLagAvgMs=\(HealthSample.ms(lagAverage))", "warps=\(tap.warps)",
            "pings=\(ping.replies)",
            "rttAvgMs=\(HealthSample.ms(rttAverage))", "rttMaxMs=\(HealthSample.ms(ping.rttMaxMs))", "pingLost=\(ping.lost)",
            "motion=\(tap.motion)", "motionGapAvgMs=\(HealthSample.ms(HealthSample.average(tap.motionGapSumMs, tap.motionGaps)))",
            "motionGapMaxMs=\(HealthSample.ms(tap.motionGapMaxMs))"
        ]
        return "minute: " + fields.joined(separator: " ")
    }
}

/// The Windows section of a problem report: one request at a time, matched by token, with a
/// deadline, since an older companion or a busy link may never answer.
struct WindowsReportRequest {
    private var token: UInt32 = 0
    private(set) var waiting: UInt32?

    mutating func begin() -> UInt32 {
        token &+= 1
        waiting = token
        return token
    }

    /// True once for the awaited token, whether it answered or timed out.
    mutating func finish(_ value: UInt32) -> Bool {
        guard waiting == value else { return false }
        waiting = nil
        return true
    }

    static func lines(_ text: String) -> [String] {
        ["windows companion report:"] + text.split(separator: "\n", omittingEmptySubsequences: false).map { "windows: \($0)" }
    }
}

/// The most recent samples, kept only in memory and written out by a problem report.
struct HealthHistory {
    let capacity: Int
    private var storage: [HealthSample] = []
    private var head = 0

    init(capacity: Int) {
        self.capacity = max(1, capacity)
    }

    mutating func append(_ sample: HealthSample) {
        if storage.count < capacity {
            storage.append(sample)
        } else {
            storage[head] = sample
            head = (head + 1) % capacity
        }
    }

    /// Oldest first.
    var samples: [HealthSample] {
        Array(storage[head...] + storage[..<head])
    }
}
