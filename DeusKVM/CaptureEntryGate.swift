import Foundation

/// Input may leave its capture queue only after Windows acknowledges that exact entry.
/// Cancelling or replacing an entry makes delayed acknowledgements harmless.
struct CaptureEntryGate {
    private var pending: (generation: Int, capture: UInt64)?

    var pendingCapture: UInt64? {
        pending?.capture
    }

    mutating func arm(generation: Int, capture: UInt64) {
        pending = capture == 0 ? nil : (generation, capture)
    }

    mutating func consume(capture: UInt64) -> Int? {
        guard let pending, capture == pending.capture else { return nil }
        self.pending = nil
        return pending.generation
    }

    mutating func cancel() {
        pending = nil
    }
}

/// Owns the pending entry's timeout and makes cancellation/acknowledgement one operation.
@MainActor
final class CaptureEntryWait {
    private var gate = CaptureEntryGate()
    private var timer: Timer?

    func arm(generation: Int, capture: UInt64, expired: @escaping @MainActor () -> Void) {
        cancel()
        gate.arm(generation: generation, capture: capture)
        timer = Timer.scheduledTimer(withTimeInterval: 6, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, self.gate.pendingCapture == capture else { return }
                self.cancel()
                expired()
            }
        }
    }

    func consume(capture: UInt64) -> Int? {
        guard let generation = gate.consume(capture: capture) else { return nil }
        timer?.invalidate(); timer = nil
        return generation
    }

    func cancel() {
        gate.cancel(); timer?.invalidate(); timer = nil
    }
}
