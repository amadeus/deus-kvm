import AppKit
import CoreBluetooth
import CoreGraphics
import Darwin
import Foundation
import os

/// Aggregate capture health only; never logs input contents or pointer coordinates.
///
/// Each per-second sample goes to the unified log (as before), to an in-memory history
/// for lag reports, and to a per-minute summary in the durable diagnostics file. Only
/// the summaries and transitions reach the disk unless the user asks for a report.
@MainActor
final class CaptureDiagnostics {
    private let log = Logger(subsystem: "io.github.amadeus.deuskvm", category: "Capture")
    /// Input timing is recorded on the Bluetooth queue, where forwarding now happens.
    private let stats: TransportStats
    let tapStats = TapStats()
    private let file: DiagnosticsLog
    private typealias CursorVisible = @convention(c) () -> Int32
    private let cursorVisible: CursorVisible? = dlopen(nil, RTLD_LAZY)
        .flatMap { dlsym($0, "CGCursorIsVisible") }.map { unsafeBitCast($0, to: CursorVisible.self) }
    private var lastSample: TimeInterval = 0
    /// Ten minutes of remote samples, written only by a lag report.
    private var history = HealthHistory(capacity: 600)
    private var summary = HealthSummary()
    private var summaryStarted: TimeInterval?
    /// Last link states written to the file, so only changes are recorded.
    private var logged: LinkState?
    private var observers: [NSObjectProtocol] = []
    private struct LinkState {
        let bluetooth: CBManagerState
        let target: Bool
        let companion: Bool
    }

    private let clock: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "HH:mm:ss.SSS"
        return formatter
    }()

    init(stats: TransportStats, file: DiagnosticsLog = .shared) {
        self.stats = stats
        self.file = file
    }

    var fileURL: URL {
        file.url
    }

    /// A state change worth keeping: unified log and diagnostics file.
    func note(_ message: String) {
        log.notice("\(message, privacy: .public)")
        file.write(message)
    }

    static var version: String {
        let info = Bundle.main.infoDictionary
        return "version=\(info?["CFBundleShortVersionString"] as? String ?? "?") build=\(info?["CFBundleVersion"] as? String ?? "?")"
    }

    /// Records the launch and starts recording display sleep/wake and screen lock/unlock.
    func started(enabled: Bool) {
        note("app started \(Self.version) enabled=\(enabled)")
        guard observers.isEmpty else { return }
        let workspace = NSWorkspace.shared.notificationCenter
        let displays = [
            (NSWorkspace.screensDidSleepNotification, "displays slept"),
            (NSWorkspace.screensDidWakeNotification, "displays woke")
        ]
        for (name, message) in displays {
            observers.append(workspace.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.note(message) }
            })
        }
        let distributed = DistributedNotificationCenter.default()
        for (name, message) in [("com.apple.screenIsLocked", "screen locked"), ("com.apple.screenIsUnlocked", "screen unlocked")] {
            observers.append(distributed.addObserver(forName: .init(name), object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.note(message) }
            })
        }
    }

    /// Sleep, wake and session changes; other notifications are ignored.
    func system(_ name: Notification.Name) {
        let messages: [Notification.Name: String] = [
            NSWorkspace.willSleepNotification: "mac will sleep", NSWorkspace.didWakeNotification: "mac did wake",
            NSWorkspace.sessionDidResignActiveNotification: "session resigned",
            NSWorkspace.sessionDidBecomeActiveNotification: "session became active"
        ]
        guard let message = messages[name] else { return }
        note(message)
        if name == NSWorkspace.willSleepNotification { flush() }
    }

    /// Records link changes only, not every refresh.
    func link(bluetooth: CBManagerState, target: Bool, companion: Bool) {
        let previous = logged
        logged = LinkState(bluetooth: bluetooth, target: target, companion: companion)
        if previous?.bluetooth != bluetooth { note("bluetooth state=\(bluetooth.rawValue)") }
        if previous?.target != target { note(target ? "PC keyboard/mouse link ready" : "PC keyboard/mouse link lost") }
        if previous?.companion != companion { note(companion ? "Windows companion ready" : "Windows companion not ready") }
    }

    /// Remote capture began: counters restart so the first sample covers only this session.
    func began(_ message: String) {
        let now = ProcessInfo.processInfo.systemUptime
        _ = stats.takeInterval(now: now)
        _ = tapStats.take()
        lastSample = now
        summary = HealthSummary()
        summaryStarted = now
        note(message)
    }

    /// Remote capture ended: keep the partial minute, then the reason.
    func ended(_ message: String) {
        writeSummary()
        summaryStarted = nil
        note(message)
    }

    func flush() {
        file.flush()
    }

    func stopping() {
        note("app stopping")
        flushAndWait()
    }

    func flushAndWait() {
        writeSummary()
        file.flushAndWait()
    }

    func sample(parkingPoint: CGPoint?, blind: [UInt8], pings: @autoclosure () -> PingTracker.Interval) {
        let now = ProcessInfo.processInfo.systemUptime
        guard now - lastSample >= 1 else { return }
        lastSample = now
        let point = CGEvent(source: nil)?.location
        let parked = point.flatMap { point in parkingPoint.map { abs(point.x - $0.x) < 2 && abs(point.y - $0.y) < 2 } } ?? false
        let sample = HealthSample(
            date: Date(), visible: cursorVisible?() ?? -1, parked: parked, blind: blind,
            transport: stats.takeInterval(now: now), tap: tapStats.take(), ping: pings()
        )
        let line = sample.line
        log.notice("capture health: \(line, privacy: .public)")
        history.append(sample)
        summary.add(sample)
        if let started = summaryStarted, now - started >= 60 {
            writeSummary()
            summaryStarted = now
        } else if summaryStarted == nil {
            summaryStarted = now
        }
    }

    /// Writes the context, the Bluetooth queue state and the recent per-second samples at once,
    /// then appends bluetoothd's recent link lines (a few seconds later) and calls `completion`.
    func writeLagReport(
        context: [String], transport: BluetoothTransport, completion: @escaping @MainActor @Sendable (URL) -> Void
    ) {
        transport.snapshot { state in
            DispatchQueue.main.async {
                MainActor.assumeIsolated { [weak self] in
                    guard let self else { return }
                    var lines = context + [state, "partial \(summary.line)"]
                    let samples = history.samples
                    lines.append("recent samples: \(samples.count) (oldest first)")
                    lines += samples.map { "sample \(clock.string(from: $0.date)) \($0.line)" }
                    file.writeReport(lines)
                    BluetoothLinkLog.capture { [file] lines in
                        file.writeReport(lines + ["=== end lag report ==="])
                        DispatchQueue.main.async { MainActor.assumeIsolated { completion(file.url) } }
                    }
                }
            }
        }
    }

    private func writeSummary() {
        guard summary.samples > 0 else { return }
        file.write(summary.line)
        summary = HealthSummary()
    }
}
