import AppKit
import ApplicationServices

/// bounded in-process diagnostic; never starts Bluetooth or forwards input
@MainActor
final class CursorSpike {
    private static var active: CursorSpike?
    private let cursor = CursorConcealer()
    private var tap: InputTap?
    private var motions = 0
    private var timeout: DispatchWorkItem?

    static func run() {
        let spike = CursorSpike()
        active = spike
        spike.start()
    }

    private func start() {
        _log("AX=\(AXIsProcessTrusted()) post=\(CGPreflightPostEventAccess())")
        _log("frontmost=\(NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? "unknown")")
        tap = InputTap(output: { [weak self] event in self?._receive(event) }, forward: { [weak self] _, input, _ in
            guard case .mouseMove = input.kind else { return }
            Task { @MainActor in self?.motions += 1 }
        })
        tap?.configure(TapConfiguration(
            targetAvailable: true,
            geometry: EdgeGeometry(bounds: CGDisplayBounds(CGMainDisplayID()), edge: .right, otherDisplays: [])
        ))
        tap?.start()
        let timeout = DispatchWorkItem { [weak self] in self?._finish() }
        self.timeout = timeout
        DispatchQueue.main.asyncAfter(deadline: .now() + 8, execute: timeout)
    }

    private func _receive(_ event: TapOutput) {
        switch event {
        case .keyboardMonitoring: break
        case let .installed(_, success):
            _log("tap=\(success)")
            if success { tap?.requestToggle() } else { _finish() }
        case let .begin(_, origin, _):
            let bounds = CGDisplayBounds(CGMainDisplayID())
            let point = CGPoint(x: bounds.midX, y: bounds.midY)
            let success = cursor.hide(at: point, returningTo: origin)
            _log("hide=\(success) point=\(String(describing: CGEvent(source: nil)?.location))")
            DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in self?._finish() }
        case .end, .disabled: _finish()
        }
    }

    private func _finish() {
        guard Self.active != nil else { return }
        timeout?.cancel()
        _log("parked=\(String(describing: CGEvent(source: nil)?.location)) motions=\(motions)")
        tap?.forceLocal()
        cursor.restore()
        tap?.stop()
        _log("restored=\(String(describing: CGEvent(source: nil)?.location))")
        Self.active = nil
        NSApp.terminate(nil)
    }

    private func _log(_ value: String) {
        let path = "/tmp/bt-cursor-spike.log"
        let line = value + "\n"
        if let handle = FileHandle(forWritingAtPath: path) {
            handle.seekToEndOfFile()
            handle.write(Data(line.utf8))
            try? handle.close()
        } else { try? line.write(toFile: path, atomically: true, encoding: .utf8) }
    }
}
