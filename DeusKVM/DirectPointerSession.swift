import Foundation

/// One acknowledged packet in flight bounds latency under Bluetooth backpressure.
@MainActor
final class DirectPointerSession {
    private let sendPacket: (Data) -> Void
    private let now: () -> TimeInterval
    private let failed: () -> Void
    private let target: UUID
    private let id: UInt8
    private var buffer = DirectPointerBuffer()
    private var timer: Timer?
    private var entered = false
    private var serial: UInt8 = 0
    private var awaiting: UInt8?
    private var deadline: TimeInterval
    private var stopped = false
    private var lastSent = 0.0
    private var heartbeat = DirectPointerBuffer.Sample(x: 0, y: 0, buttons: 0, wheel: 0, pan: 0, barrier: false)

    init(
        target: UUID, id: UInt8, now: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
        send: @escaping (Data) -> Void, failed: @escaping () -> Void
    ) {
        sendPacket = send
        self.now = now
        deadline = now() + 2
        self.target = target
        self.id = id
        self.failed = failed
        timer = Timer.scheduledTimer(withTimeInterval: 1 / 60, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
    }

    func receive(target: UUID, payload: Data) {
        guard !stopped, target == self.target, payload.count == 3, payload[0] == id else { return }
        // serial zero acknowledges entry; subsequent serials acknowledge mouse packets.
        guard entered ? awaiting == payload[1] : payload[1] == 0 else { return }
        guard payload[2] == 1 else { fail(); return }
        entered = true
        awaiting = nil
        // A slow link is already pacing us; do not add another timer interval
        // after its acknowledgement when newer input is waiting.
        if !buffer.pending.isEmpty { tick() }
    }

    func send(dx: Double, dy: Double, report: MouseReport) {
        guard !stopped else { return }
        if !buffer.append(dx: dx, dy: dy, report: report) { fail() }
    }

    func stop() {
        stopped = true
        timer?.invalidate()
        timer = nil
    }

    private func fail() {
        stop(); failed()
    }

    func tick() {
        guard !stopped else { return }
        if !entered || awaiting != nil {
            if now() >= deadline { fail() }
            return
        }
        let pending = buffer.take()
        guard pending != nil || now() - lastSent >= 0.5 else { return }
        let sample = pending ?? heartbeat
        heartbeat = sample
        heartbeat.wheel = 0
        heartbeat.pan = 0
        heartbeat.barrier = false
        lastSent = now()
        serial &+= 1
        awaiting = serial
        deadline = now() + 2
        sendPacket(sample.payload(id: id, serial: serial))
    }
}
