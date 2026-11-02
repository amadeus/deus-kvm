import Darwin
import Foundation

/// Time from an input event's creation to the tap forwarding it. A slow tap callback makes
/// the window server queue events behind it; the Bluetooth-queue delay cannot see that.
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
    }

    private let lock = NSLock()
    private var current = Interval()

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

/// One Bluetooth-queue interval from `TransportStats`.
struct TransportInterval: Equatable {
    var events = 0
    var maxDispatchMs = 0.0
    var hidSent = 0
    var hidRejected = 0
    var hidMerged = 0
    var hidPending = 0
    var hidPendingMax = 0
    var stallMs = 0.0
    var stallMaxMs = 0.0
    var framesOut = 0
    var bytesOut = 0
    var framesIn = 0
    var bytesIn = 0
    var companionQueued = 0
    var companionBlocked = false
}

/// One per-second capture health sample while controlling Windows.
struct HealthSample {
    var date: Date
    var visible: Int32
    var parked: Bool
    var blind: [UInt8]
    var transport: TransportInterval
    var tap: TapStats.Interval
    var ping: PingTracker.Interval

    /// Keeps the original capture health fields first so older log readings still apply.
    var line: String {
        let link = transport
        let lagAverage = Self.average(tap.lagSumMs, tap.measured)
        let fields: [String] = [
            "visible=\(visible)", "parked=\(parked)", "events=\(link.events)", "maxDispatchMs=\(Int(link.maxDispatchMs))",
            "queued=\(link.companionQueued)", "blocked=\(link.companionBlocked)", "blind=\(blind)",
            "hidPending=\(link.hidPending)", "hidRejected=\(link.hidRejected)", "hidMerged=\(link.hidMerged)",
            "hidSent=\(link.hidSent)", "stallMs=\(Int(link.stallMs))", "stallMaxMs=\(Int(link.stallMaxMs))",
            "tapLagMaxMs=\(Self.ms(tap.lagMaxMs))", "tapLagAvgMs=\(Self.ms(lagAverage))", "warps=\(tap.warps)",
            "compOut=\(link.framesOut)/\(link.bytesOut)", "compIn=\(link.framesIn)/\(link.bytesIn)",
            "rttMaxMs=\(Self.ms(ping.rttMaxMs))", "pingLost=\(ping.lost)"
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
    private var transport = TransportInterval()
    private var parkedMisses = 0
    private var tap = TapStats.Interval()
    private var ping = PingTracker.Interval()

    mutating func add(_ sample: HealthSample) {
        samples += 1
        if !sample.parked { parkedMisses += 1 }
        let link = sample.transport
        transport.events += link.events
        transport.maxDispatchMs = max(transport.maxDispatchMs, link.maxDispatchMs)
        transport.hidSent += link.hidSent
        transport.hidRejected += link.hidRejected
        transport.hidMerged += link.hidMerged
        transport.hidPendingMax = max(transport.hidPendingMax, link.hidPendingMax, link.hidPending)
        transport.stallMs += link.stallMs
        transport.stallMaxMs = max(transport.stallMaxMs, link.stallMaxMs)
        transport.framesOut += link.framesOut
        transport.bytesOut += link.bytesOut
        transport.framesIn += link.framesIn
        transport.bytesIn += link.bytesIn
        transport.companionQueued = max(transport.companionQueued, link.companionQueued)
        tap.events += sample.tap.events
        tap.measured += sample.tap.measured
        tap.lagSumMs += sample.tap.lagSumMs
        tap.lagMaxMs = max(tap.lagMaxMs, sample.tap.lagMaxMs)
        tap.warps += sample.tap.warps
        ping.replies += sample.ping.replies
        ping.rttSumMs += sample.ping.rttSumMs
        ping.rttMaxMs = max(ping.rttMaxMs, sample.ping.rttMaxMs)
        ping.lost += sample.ping.lost
    }

    var line: String {
        let link = transport
        let lagAverage = HealthSample.average(tap.lagSumMs, tap.measured)
        let rttAverage = HealthSample.average(ping.rttSumMs, ping.replies)
        let fields: [String] = [
            "samples=\(samples)", "notParked=\(parkedMisses)", "events=\(link.events)",
            "maxDispatchMs=\(Int(link.maxDispatchMs))", "tapEvents=\(tap.events)",
            "tapLagMaxMs=\(HealthSample.ms(tap.lagMaxMs))", "tapLagAvgMs=\(HealthSample.ms(lagAverage))", "warps=\(tap.warps)",
            "hidSent=\(link.hidSent)", "hidRejected=\(link.hidRejected)", "hidMerged=\(link.hidMerged)",
            "hidPendingMax=\(link.hidPendingMax)", "stallMs=\(Int(link.stallMs))", "stallMaxMs=\(Int(link.stallMaxMs))",
            "compOut=\(link.framesOut)/\(link.bytesOut)", "compIn=\(link.framesIn)/\(link.bytesIn)",
            "companionQueuedMax=\(link.companionQueued)", "pings=\(ping.replies)",
            "rttAvgMs=\(HealthSample.ms(rttAverage))", "rttMaxMs=\(HealthSample.ms(ping.rttMaxMs))", "pingLost=\(ping.lost)"
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
