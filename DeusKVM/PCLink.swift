import Combine
import Foundation
import os

/// The Mac's side of everything that reaches a PC: the companion link (control server and
/// protocol), which PC is allowed and targeted, and the queue captured input runs on. Input
/// itself travels on the local-network stream (InputNetworkServer).
@MainActor
final class PCLink: ObservableObject {
    /// The control link listener is up (and published over Bonjour).
    @Published private(set) var listening = false
    @Published private(set) var hostPolicy = HostPolicy(allowed: Set(
        (UserDefaults.standard.stringArray(forKey: AppSettings.allowedHostsKey) ?? []).compactMap(UUID.init(uuidString:))
    ))
    @Published private(set) var lastError: String?
    var onTargetWillChange: (() -> Void)?
    /// Lines for the diagnostics log.
    var note: ((String) -> Void)?

    let companion = CompanionService()
    let server = ControlServer()
    let input = InputPipeline()
    private(set) var isEnabled = true
    private var subscriptions = Set<AnyCancellable>()
    private var started = false

    init() {
        companion.server = server
        server.onConnect = { [weak self] id, token in self?.companion.connected(id, token: token) }
        server.onPacket = { [weak self] id, token, packet in self?.companion.receive(packet, from: id, token: token) }
        server.onDisconnect = { [weak self] id, token in self?.companion.disconnected(id, token: token) }
        server.onState = { [weak self] up, line in
            guard let self else { return }
            if listening != up { listening = up }
            lastError = up ? nil : (line.hasSuffix("stopped") ? nil : line)
            note?(line)
        }
        companion.$ready.sink { [weak self] ready in
            DispatchQueue.main.async { MainActor.assumeIsolated { self?.reconcile(ready: ready) } }
        }.store(in: &subscriptions)
    }

    /// The Mac's id as PCs see it.
    var id: UUID? {
        server.id
    }

    func start() {
        guard !started else { return }
        started = true
        companion.setAvailability(enabled: isEnabled, allowed: hostPolicy.allowed)
        companion.clipboardTarget = { [weak self] in self?.hostPolicy.target }
        server.start(identity: ControlIdentity.load())
    }

    /// The link stays up while disabled, so a PC sees this Mac as paused rather than gone.
    func setEnabled(_ enabled: Bool) {
        isEnabled = enabled
        companion.setAvailability(enabled: enabled, allowed: hostPolicy.allowed)
        if !enabled { input.stopInput() }
        start()
    }

    func setAllowed(_ id: UUID, _ allowed: Bool) {
        var policy = hostPolicy
        if allowed { policy.allowed.insert(id) } else { policy.allowed.remove(id) }
        policy.reconcile(ready: companion.ready)
        apply(policy)
        UserDefaults.standard.set(policy.allowed.map(\.uuidString).sorted(), forKey: AppSettings.allowedHostsKey)
    }

    func selectHost(_ id: UUID) {
        var policy = hostPolicy
        policy.select(id)
        apply(policy)
    }

    func clearAllowedHosts() {
        var policy = hostPolicy
        policy.allowed.removeAll()
        policy.reconcile(ready: companion.ready)
        apply(policy)
        UserDefaults.standard.removeObject(forKey: AppSettings.allowedHostsKey)
    }

    private func reconcile(ready: Set<UUID>) {
        var policy = hostPolicy
        policy.reconcile(ready: ready)
        apply(policy)
    }

    private func apply(_ policy: HostPolicy) {
        if policy.target != hostPolicy.target { onTargetWillChange?() }
        if policy != hostPolicy { hostPolicy = policy }
        companion.setAvailability(enabled: isEnabled, allowed: policy.allowed)
    }
}

/// Values that cross between threads without being shared mutably.
struct Unchecked<Value>: @unchecked Sendable {
    let value: Value
}

/// The serial queue captured input runs on, between the tap thread and the network stream:
/// InputGate holds input captured during a crossing until ENTER is queued, and
/// DirectInputController turns events into stream records.
final class InputPipeline: @unchecked Sendable {
    let stats = InputQueueStats()
    private let queue = DispatchQueue(label: "io.github.amadeus.deuskvm.input", qos: .userInteractive)
    private let controller = DirectInputController()
    private var gate = InputGate<(DirectInputEvent, TimeInterval)>()

    /// Event-tap thread: remote input goes straight to this queue. Pointer steps, the
    /// bulk of it, run synchronously: on an idle queue the block runs on the tap thread, so no
    /// worker thread wakes per step, and FIFO order with everything already queued is kept. The
    /// block only seals and writes without blocking, and nothing on this queue waits for the tap.
    func input(_ event: DirectInputEvent, generation: Int, capturedAt: TimeInterval) {
        let work = { [self] in
            for (event, captured) in gate.receive((event, capturedAt), generation: generation, droppable: event.isRelative) {
                forward(event, captured)
            }
        }
        if event.isPointer { queue.sync(execute: work) } else { queue.async(execute: work) }
    }

    /// The coordinator approved the crossing and queued ENTER: forward this session, oldest first.
    func startInput(generation: Int, capture: UInt64, network: InputNetworkServer) {
        queue.async { [self] in
            controller.start(InputSink(
                send: { [weak network] in network?.send($0) },
                pointerReady: { [weak network] in network?.isUp ?? false },
                keysReady: { [weak network] in network?.keysUp ?? false }
            ))
            network.resetSession(capture: capture)
            for (event, captured) in gate.start(generation) {
                forward(event, captured)
            }
        }
    }

    /// Returning locally: release held input on the PC, then ignore the session.
    func stopInput() {
        queue.async { [self] in
            gate.stop()
            controller.stop()
        }
    }

    /// Runs `completion` on the queue after everything queued before it.
    func afterRelease(_ completion: @escaping @Sendable () -> Void) {
        queue.async(execute: completion)
    }

    private func forward(_ event: DirectInputEvent, _ capturedAt: TimeInterval) {
        stats.input(delay: ProcessInfo.processInfo.systemUptime - capturedAt)
        controller.handle(event)
    }
}
