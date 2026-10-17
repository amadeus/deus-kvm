import CoreBluetooth
import Foundation

/// CoreBluetooth's queue, shared with captured input. Nothing on it waits for the main thread.
enum BluetoothQueue {
    static let queue = DispatchQueue(label: "io.github.amadeus.deuskvm.bluetooth", qos: .userInteractive)
}

/// CoreBluetooth objects are not Sendable. They cross between the main thread and the
/// Bluetooth queue only as identities or values neither side mutates.
struct Unchecked<Value>: @unchecked Sendable {
    let value: Value
}

/// A companion write reduced to values the main thread can decode.
struct CompanionWrite: Sendable {
    let central: UUID
    let index: Int
    let offset: Int
    let value: Data?
}

/// A HID write the Bluetooth queue has already acknowledged.
struct HIDWrite {
    let central: CBCentral
    let characteristic: CBCharacteristic
    let value: Data?
}

/// Owns CoreBluetooth on BluetoothQueue. HID reports and their backpressure queue, companion
/// frames and captured input are handled here without the main thread. HIDPeripheral keeps
/// host selection, services, UI state and companion semantics on the main actor and drives
/// this queue in order, so release, EXIT and retarget steps never interleave with input.
final class BluetoothTransport: NSObject, CBPeripheralManagerDelegate, @unchecked Sendable {
    let stats = TransportStats()
    private static let batteryLevel: UInt8 = 100
    private let queue = BluetoothQueue.queue
    private weak var owner: HIDPeripheral?
    private var manager: CBPeripheralManager?
    private var epoch = 0

    private var reports: [UInt8: CBMutableCharacteristic] = [:]
    private var bootMouse: CBMutableCharacteristic?
    private var bootKeyboard: CBMutableCharacteristic?
    private var battery: CBMutableCharacteristic?
    private var target: CBCentral?
    private var targetID: UUID?
    private var enabled = true
    private var bootKeyboardNeeded = false
    /// last-sent payloads for reads and new subscriptions, relative fields cleared
    private var cached = HIDPeripheral.emptyReports
    private var cachedBootMouse = MouseReport.zero.bootData
    private var pending = HIDNotificationQueue<CBMutableCharacteristic>()
    private var ready = true
    private var releaseRecipients: [CBCentral] = []
    private var releaseCallbacks: [@Sendable () -> Void] = []

    private struct Frame {
        let data: Data
        let index: Int
        let central: CBCentral
    }

    private var companionChars: [CBMutableCharacteristic] = []
    private var controls: [Frame] = []
    private var bulk: [Frame] = []
    private var companionBlocked = false
    /// clients whose frames overflowed; ignored until the main thread disconnects them
    private var overflowed: Set<UUID> = []

    private let controller = DirectInputController()
    private var gate = InputGate<(DirectInputEvent, TimeInterval)>()

    /// Main thread, once, before the manager starts.
    func attach(_ owner: HIDPeripheral) {
        self.owner = owner
    }

    // MARK: Manager and services (main thread enqueues)

    func startManager() {
        queue.async { [self] in
            guard manager == nil else { return }
            manager = CBPeripheralManager(delegate: self, queue: queue, options: [CBPeripheralManagerOptionShowPowerAlertKey: true])
        }
    }

    /// Drops the manager and everything bound to it. Callbacks already sent to the main
    /// thread carry the old epoch and are ignored there.
    func restart() {
        queue.async { [self] in
            manager?.stopAdvertising()
            manager?.delegate = nil
            manager = nil
            epoch += 1
            reports.removeAll(); bootMouse = nil; bootKeyboard = nil; battery = nil
            pending.removeAll(); ready = true; releaseRecipients.removeAll(); releaseCallbacks.removeAll()
            companionChars.removeAll(); controls.removeAll(); bulk.removeAll(); companionBlocked = false; overflowed.removeAll()
            stats.setPending(0); stats.companion(queued: 0, blocked: false)
        }
    }

    func add(_ service: CBMutableService) {
        let service = Unchecked(value: service)
        queue.async { [self] in manager?.add(service.value) }
    }

    func remove(_ service: CBMutableService) {
        let service = Unchecked(value: service)
        queue.async { [self] in manager?.remove(service.value) }
    }

    func startAdvertising(_ advertisement: [String: Any]) {
        let advertisement = Unchecked(value: advertisement)
        queue.async { [self] in manager?.startAdvertising(advertisement.value) }
    }

    func stopAdvertising() {
        queue.async { [self] in manager?.stopAdvertising() }
    }

    func setLatency(_ latency: CBPeripheralManagerConnectionLatency, for central: CBCentral) {
        let central = Unchecked(value: central)
        queue.async { [self] in manager?.setDesiredConnectionLatency(latency, for: central.value) }
    }

    /// Main thread, at quit: waits until queued EXIT and release reports reach CoreBluetooth,
    /// as when they were sent on the main thread. The queue never waits for the main thread.
    func flush() {
        queue.sync {}
    }

    // MARK: HID routing (main thread enqueues, applied in order with input)

    func configureHID(
        reports: [UInt8: CBMutableCharacteristic], bootMouse: CBMutableCharacteristic?,
        bootKeyboard: CBMutableCharacteristic?, battery: CBMutableCharacteristic?
    ) {
        let characteristics = Unchecked(value: (reports, bootMouse, bootKeyboard, battery))
        queue.async { [self] in
            (self.reports, self.bootMouse, self.bootKeyboard, self.battery) = characteristics.value
        }
    }

    /// The selected host's central (nil until it interacts), enablement and keyboard protocol.
    func route(target: CBCentral?, id: UUID?, enabled: Bool, bootKeyboard: Bool) {
        let target = Unchecked(value: target)
        queue.async { [self] in
            self.target = target.value
            targetID = id
            self.enabled = enabled
            bootKeyboardNeeded = bootKeyboard
        }
    }

    /// The host changed: unsent input for the old host is dropped unless it is a release, and
    /// reads and resubscriptions start from neutral reports.
    func retarget() {
        queue.async { [self] in
            if releaseRecipients.isEmpty { pending.removeAll(); stats.setPending(0) }
            resetReplay()
        }
    }

    func resetReports() {
        queue.async { [self] in resetReplay() }
    }

    /// Releases every input on a host that loses control, ahead of anything still queued.
    func release(to central: CBCentral) {
        let central = Unchecked(value: central)
        queue.async { [self] in
            releaseRecipients = [central.value]
            pending.removeAll()
            for (id, data) in HIDPeripheral.emptyReports {
                if let characteristic = reports[id] { pending.append(data, target: characteristic) }
            }
            if let bootMouse { pending.append(MouseReport.zero.bootData, target: bootMouse) }
            if let bootKeyboard { pending.append(KeyboardReport.zero.data, target: bootKeyboard) }
            drainHID()
        }
    }

    /// Runs `completion` once queued releases reach CoreBluetooth (at once if none are queued).
    func afterRelease(_ completion: @escaping @Sendable () -> Void) {
        queue.async { [self] in
            if releaseRecipients.isEmpty { completion() } else { releaseCallbacks.append(completion) }
        }
    }

    func dropRelease(for id: UUID) {
        queue.async { [self] in
            guard releaseRecipients.contains(where: { $0.identifier == id }) else { return }
            pending.removeAll(); releaseRecipients.removeAll(); releaseCallbacks.removeAll()
            stats.setPending(0)
        }
    }

    /// Re-sends the replay state for a characteristic the target just subscribed to.
    func replay(_ characteristic: CBCharacteristic) {
        let characteristic = Unchecked(value: characteristic)
        queue.async { [self] in
            let subscribed = characteristic.value
            if let id = reportID(subscribed), let data = cached[id], let report = reports[id] {
                send(data, to: report)
            } else if subscribed.uuid == HIDProfile.bootMouseInputReport, let bootMouse {
                send(MouseReport.zero.bootData, to: bootMouse)
            } else if subscribed.uuid == HIDProfile.bootKeyboardInputReport, let bootKeyboard {
                send(KeyboardReport.zero.data, to: bootKeyboard)
            } else if subscribed.uuid == HIDProfile.batteryLevel, let battery {
                send(Data([Self.batteryLevel]), to: battery)
            }
        }
    }

    // MARK: Captured input (tap and main thread enqueue; the queue sends)

    /// Event-tap thread: remote input goes straight to this queue.
    func input(_ event: DirectInputEvent, generation: Int, capturedAt: TimeInterval) {
        queue.async { [self] in
            for (event, captured) in gate.receive((event, capturedAt), generation: generation, droppable: event.isRelative) {
                forward(event, captured)
            }
        }
    }

    /// The coordinator approved the crossing and queued ENTER: forward this session, oldest first.
    func startInput(generation: Int) {
        queue.async { [self] in
            controller.start(HIDInput(
                sendMouse: { [weak self] in self?.sendMouse($0) },
                sendKeyboard: { [weak self] in self?.sendKeyboard($0) },
                sendConsumer: { [weak self] in self?.sendConsumer($0) },
                isActive: true, isConnected: true, activeError: nil
            ))
            for (event, captured) in gate.start(generation) {
                forward(event, captured)
            }
        }
    }

    /// Returning locally: release held input on the host, then ignore the session.
    func stopInput() {
        queue.async { [self] in
            gate.stop()
            controller.stop()
        }
    }

    private func forward(_ event: DirectInputEvent, _ capturedAt: TimeInterval) {
        stats.input(delay: ProcessInfo.processInfo.systemUptime - capturedAt)
        controller.handle(event)
    }

    // MARK: Reports (queue only)

    private func sendMouse(_ report: MouseReport) {
        cachedBootMouse = report.held.bootData
        let sameButtons = report.buttons.rawValue == cached[ReportID.mouse.rawValue]?.first
        let coalesceMotion = sameButtons && report.wheel == 0 && report.pan == 0
        broadcast(report.data, reportID: .mouse, replay: report.held.data, coalesceMotion: coalesceMotion)
    }

    private func sendKeyboard(_ report: KeyboardReport) {
        broadcast(report.data, reportID: .keyboard)
        // boot-protocol hosts read this characteristic instead
        if bootKeyboardNeeded, let bootKeyboard { send(report.data, to: bootKeyboard) }
    }

    private func sendConsumer(_ report: ConsumerReport) {
        broadcast(report.data, reportID: .consumerControl)
    }

    private func resetReplay() {
        cached = HIDPeripheral.emptyReports
        cachedBootMouse = MouseReport.zero.bootData
    }

    private func broadcast(_ data: Data, reportID: ReportID, replay: Data? = nil, coalesceMotion: Bool = false) {
        guard enabled else { return }
        cached[reportID.rawValue] = replay ?? data
        guard let characteristic = reports[reportID.rawValue] else { return }
        send(data, to: characteristic, coalesceMotion: coalesceMotion)
    }

    @discardableResult
    private func send(_ data: Data, to characteristic: CBMutableCharacteristic, coalesceMotion: Bool = false) -> Bool {
        guard let manager, enabled, let target else { return false }
        if !ready {
            let merged = pending.append(data, target: characteristic, coalesceMotion: coalesceMotion)
            stats.notification(merged: merged, pending: pending.count)
            return false
        }
        guard manager.updateValue(data, for: characteristic, onSubscribedCentrals: [target]) else {
            ready = false
            let merged = pending.append(data, target: characteristic, coalesceMotion: coalesceMotion)
            stats.notification(rejected: true, merged: merged, pending: pending.count)
            return false
        }
        return true
    }

    private func drainHID() {
        guard let manager else { return }
        let recipients = releaseRecipients.isEmpty ? (enabled ? target.map { [$0] } ?? [] : []) : releaseRecipients
        guard !recipients.isEmpty else { pending.removeAll(); stats.setPending(0); return }
        while let entry = pending.first {
            guard manager.updateValue(entry.data, for: entry.target, onSubscribedCentrals: recipients) else {
                ready = false
                stats.setPending(pending.count)
                return
            }
            pending.removeFirst()
        }
        stats.setPending(0)
        releaseRecipients.removeAll()
        let callbacks = releaseCallbacks
        releaseCallbacks.removeAll()
        callbacks.forEach { $0() }
    }

    // MARK: Companion frames (main thread enqueues; the queue sends)

    func configureCompanion(_ characteristics: [CBMutableCharacteristic]) {
        let characteristics = Unchecked(value: characteristics)
        queue.async { [self] in companionChars = characteristics.value }
    }

    /// One packet's frames, in order; control frames go ahead of bulk. More than 512 queued
    /// frames drops the client, as before.
    func enqueueCompanion(_ frames: [Data], control: Bool, to central: CBCentral) {
        let central = Unchecked(value: central)
        queue.async { [self] in
            let id = central.value.identifier
            guard !overflowed.contains(id) else { return }
            guard controls.count + bulk.count + frames.count <= 512 else {
                // A partial stream would corrupt the client's decoder; stop here like a disconnect.
                overflowed.insert(id)
                removeFrames(for: id)
                toMain { $0.companion.overflowed(id) }
                return
            }
            let items = frames.map { Frame(data: $0, index: control ? 0 : 2, central: central.value) }
            if control { controls.append(contentsOf: items) } else { bulk.append(contentsOf: items) }
            drainCompanion()
        }
    }

    /// The main thread disconnected this client; a later client with the same id starts clean.
    func dropCompanion(for id: UUID) {
        queue.async { [self] in
            overflowed.remove(id)
            removeFrames(for: id)
        }
    }

    func resetCompanion() {
        queue.async { [self] in
            companionChars.removeAll(); controls.removeAll(); bulk.removeAll(); companionBlocked = false; overflowed.removeAll()
            stats.companion(queued: 0, blocked: false)
        }
    }

    private func removeFrames(for id: UUID) {
        controls.removeAll { $0.central.identifier == id }
        bulk.removeAll { $0.central.identifier == id }
        stats.companion(queued: controls.count + bulk.count, blocked: companionBlocked)
    }

    private func drainCompanion() {
        defer { stats.companion(queued: controls.count + bulk.count, blocked: companionBlocked) }
        guard let manager, !companionBlocked else { return }
        while let item = controls.first ?? bulk.first {
            guard item.index < companionChars.count else { controls.removeAll(); bulk.removeAll(); return }
            guard manager.updateValue(item.data, for: companionChars[item.index], onSubscribedCentrals: [item.central]) else {
                companionBlocked = true
                return
            }
            if !controls.isEmpty { controls.removeFirst() } else { bulk.removeFirst() }
        }
    }

    // MARK: CBPeripheralManagerDelegate (queue)

    func peripheralManagerDidUpdateState(_ peripheral: CBPeripheralManager) {
        let state = peripheral.state
        toMain { $0.transportStateDidChange(state) }
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, didAdd service: CBService, error: Error?) {
        let uuid = Unchecked(value: service.uuid), message = error?.localizedDescription
        toMain { $0.transportDidAdd(uuid.value, error: message) }
    }

    func peripheralManagerDidStartAdvertising(_ peripheral: CBPeripheralManager, error: Error?) {
        let message = error?.localizedDescription
        toMain { $0.transportDidStartAdvertising(error: message) }
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, central: CBCentral, didSubscribeTo characteristic: CBCharacteristic) {
        let central = Unchecked(value: central), characteristic = Unchecked(value: characteristic)
        toMain { $0.transportDidSubscribe(central.value, to: characteristic.value) }
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, central: CBCentral, didUnsubscribeFrom characteristic: CBCharacteristic) {
        let central = Unchecked(value: central), characteristic = Unchecked(value: characteristic)
        toMain { $0.transportDidUnsubscribe(central.value, from: characteristic.value) }
    }

    func peripheralManagerIsReady(toUpdateSubscribers peripheral: CBPeripheralManager) {
        ready = true
        drainHID()
        companionBlocked = false
        drainCompanion()
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, didReceiveRead request: CBATTRequest) {
        let central = Unchecked(value: request.central), characteristic = Unchecked(value: request.characteristic)
        guard companionIndex(request.characteristic) == nil else {
            // The companion status read reflects main-thread handshake state.
            let request = Unchecked(value: request), epoch = epoch
            toMain { owner in
                owner.trace("read: \(central.value.identifier) -> \(characteristic.value.uuid)")
                let value = owner.companion.readValue(for: central.value.identifier)
                self.queue.async { if self.epoch == epoch { self.respond(request.value, with: value) } }
            }
            return
        }
        respond(request, with: value(for: request))
        toMain { $0.transportDidRead(central.value, characteristic: characteristic.value) }
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, didReceiveWrite requests: [CBATTRequest]) {
        guard let first = requests.first else { return }
        guard companionIndex(first.characteristic) == nil else {
            // Companion frames are decoded on the main thread; the ATT response waits for the result.
            let writes = requests.map { request in
                CompanionWrite(
                    central: request.central.identifier, index: companionIndex(request.characteristic) ?? -1,
                    offset: request.offset, value: request.value
                )
            }
            let request = Unchecked(value: first), epoch = epoch
            toMain { owner in
                var result = CBATTError.Code.success
                for write in writes {
                    result = owner.companion.receive(write)
                    if result != .success { break }
                }
                let response = Unchecked(value: result)
                self.queue.async { if self.epoch == epoch { self.manager?.respond(to: request.value, withResult: response.value) } }
            }
            return
        }
        peripheral.respond(to: first, withResult: .success)
        let writes = Unchecked(value: requests.map { HIDWrite(central: $0.central, characteristic: $0.characteristic, value: $0.value) })
        toMain { $0.transportDidWrite(writes.value) }
    }

    private func respond(_ request: CBATTRequest, with value: Data?) {
        guard let manager else { return }
        guard let value else { manager.respond(to: request, withResult: .invalidAttributeValueLength); return }
        guard request.offset <= value.count else { manager.respond(to: request, withResult: .invalidOffset); return }
        request.value = value.subdata(in: request.offset ..< value.count)
        manager.respond(to: request, withResult: .success)
    }

    private func value(for request: CBATTRequest) -> Data? {
        let isTarget = request.central.identifier == targetID
        let replay = isTarget ? cached : HIDPeripheral.emptyReports
        switch request.characteristic.uuid {
        case HIDProfile.batteryLevel: return Data([Self.batteryLevel])
        case HIDProfile.hidInformation: return HIDProfile.hidInformationValue
        case HIDProfile.reportMap: return HIDProfile.reportMapData
        case HIDProfile.protocolMode: return Data([0x01]) // report protocol
        case HIDProfile.manufacturerName: return HIDProfile.manufacturerNameValue
        case HIDProfile.modelNumber: return HIDProfile.modelNumberValue
        case HIDProfile.pnpID: return HIDProfile.pnpIDValue
        case HIDProfile.report: return reportID(request.characteristic).map { replay[$0] ?? Data() } ?? Data()
        case HIDProfile.bootMouseInputReport: return isTarget ? cachedBootMouse : MouseReport.zero.bootData
        case HIDProfile.bootKeyboardInputReport: return replay[ReportID.keyboard.rawValue]
        default: return Data()
        }
    }

    private func reportID(_ characteristic: CBCharacteristic) -> UInt8? {
        for (id, report) in reports where report === characteristic as AnyObject {
            return id
        }
        // fallback for restored characteristics
        guard characteristic.uuid == HIDProfile.report,
              let descriptor = (characteristic as? CBMutableCharacteristic)?.descriptors?
              .first(where: { $0.uuid == HIDProfile.reportReference }),
              let value = descriptor.value as? Data else { return nil }
        return value.first
    }

    private func companionIndex(_ characteristic: CBCharacteristic) -> Int? {
        companionChars.firstIndex { $0.uuid == characteristic.uuid }
    }

    /// Delivers a callback to the main actor in order, unless the manager was replaced since.
    private func toMain(_ body: @escaping @Sendable @MainActor (HIDPeripheral) -> Void) {
        let epoch = epoch, owner = owner
        DispatchQueue.main.async { [weak owner] in
            MainActor.assumeIsolated {
                guard let owner, owner.transportEpoch == epoch else { return }
                body(owner)
            }
        }
    }
}

/// Aggregate counts from the Bluetooth queue, sampled on the main thread for Capture logs.
final class TransportStats: @unchecked Sendable {
    private let lock = NSLock()
    private var events = 0
    private var maxDelay: TimeInterval = 0
    private var rejected = 0
    private var merged = 0
    private var pending = 0
    private var companionQueued = 0
    private var companionBlocked = false

    func input(delay: TimeInterval) {
        lock.withLock { events += 1; maxDelay = max(maxDelay, delay) }
    }

    func resetInput() {
        lock.withLock { events = 0; maxDelay = 0 }
    }

    func takeInput() -> (events: Int, maxDelay: TimeInterval) {
        lock.withLock {
            defer { events = 0; maxDelay = 0 }
            return (events, maxDelay)
        }
    }

    func notification(rejected wasRejected: Bool = false, merged wasMerged: Bool, pending count: Int) {
        lock.withLock {
            if wasRejected { rejected += 1 }
            if wasMerged { merged += 1 }
            pending = count
        }
    }

    func setPending(_ count: Int) {
        lock.withLock { pending = count }
    }

    /// Counts only; resets so each capture health line covers its own interval.
    func takeHID() -> String {
        lock.withLock {
            defer { rejected = 0; merged = 0 }
            return "hidPending=\(pending) hidRejected=\(rejected) hidMerged=\(merged)"
        }
    }

    func companion(queued: Int, blocked: Bool) {
        lock.withLock { companionQueued = queued; companionBlocked = blocked }
    }

    var companionState: (queued: Int, blocked: Bool) {
        lock.withLock { (companionQueued, companionBlocked) }
    }
}
