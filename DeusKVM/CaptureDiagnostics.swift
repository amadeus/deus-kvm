import CoreGraphics
import Darwin
import Foundation
import os

/// Aggregate capture health only; never logs input contents or pointer coordinates.
@MainActor
final class CaptureDiagnostics {
    private let log = Logger(subsystem: "io.github.amadeus.deuskvm", category: "Capture")
    private typealias CursorVisible = @convention(c) () -> Int32
    private let cursorVisible: CursorVisible? = dlopen(nil, RTLD_LAZY)
        .flatMap { dlsym($0, "CGCursorIsVisible") }.map { unsafeBitCast($0, to: CursorVisible.self) }
    private var lastSample: TimeInterval = 0
    private var events = 0
    private var maximumDelay: TimeInterval = 0

    func transition(_ reason: String) {
        log.notice("\(reason, privacy: .public)")
        events = 0
        maximumDelay = 0
    }

    func input(capturedAt: TimeInterval) {
        events += 1
        maximumDelay = max(maximumDelay, ProcessInfo.processInfo.systemUptime - capturedAt)
    }

    func sample(parkingPoint: CGPoint?, companion: @autoclosure () -> String) {
        let now = ProcessInfo.processInfo.systemUptime
        guard now - lastSample >= 1 else { return }
        lastSample = now
        let point = CGEvent(source: nil)?.location
        let parked = point.flatMap { point in parkingPoint.map { abs(point.x - $0.x) < 2 && abs(point.y - $0.y) < 2 } } ?? false
        let visible = cursorVisible?() ?? -1
        let delay = Int(maximumDelay * 1000)
        let count = events
        let state = companion()
        log
            .notice(
                "capture health: visible=\(visible) parked=\(parked) events=\(count) maxDispatchMs=\(delay) \(state, privacy: .public)"
            )
        events = 0
        maximumDelay = 0
    }
}
