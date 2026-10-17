import CoreGraphics
import Darwin
import Foundation
import os

/// Aggregate capture health only; never logs input contents or pointer coordinates.
@MainActor
final class CaptureDiagnostics {
    private let log = Logger(subsystem: "io.github.amadeus.deuskvm", category: "Capture")
    /// Input timing is recorded on the Bluetooth queue, where forwarding now happens.
    private let stats: TransportStats
    private typealias CursorVisible = @convention(c) () -> Int32
    private let cursorVisible: CursorVisible? = dlopen(nil, RTLD_LAZY)
        .flatMap { dlsym($0, "CGCursorIsVisible") }.map { unsafeBitCast($0, to: CursorVisible.self) }
    private var lastSample: TimeInterval = 0

    init(stats: TransportStats) {
        self.stats = stats
    }

    func transition(_ reason: String) {
        log.notice("\(reason, privacy: .public)")
        stats.resetInput()
    }

    func sample(parkingPoint: CGPoint?, companion: @autoclosure () -> String) {
        let now = ProcessInfo.processInfo.systemUptime
        guard now - lastSample >= 1 else { return }
        lastSample = now
        let point = CGEvent(source: nil)?.location
        let parked = point.flatMap { point in parkingPoint.map { abs(point.x - $0.x) < 2 && abs(point.y - $0.y) < 2 } } ?? false
        let visible = cursorVisible?() ?? -1
        let input = stats.takeInput()
        let delay = Int(input.maxDelay * 1000)
        let count = input.events
        let state = companion()
        log
            .notice(
                "capture health: visible=\(visible) parked=\(parked) events=\(count) maxDispatchMs=\(delay) \(state, privacy: .public)"
            )
    }
}
