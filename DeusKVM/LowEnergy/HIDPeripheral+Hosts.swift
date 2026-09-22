import CoreBluetooth
import Foundation
import IOBluetooth

/// Allowed-host selection and advertising share one readiness decision.
extension HIDPeripheral {
    func stop() {
        isHIDServiceAllowed = false
        advertisingStarting = false
        pManager?.stopAdvertising()
        isAdvertising = false
    }

    func setEnabled(_ enabled: Bool) {
        isEnabled = enabled
        companion.setAvailability(enabled: enabled, allowed: hostPolicy.allowed)
        if enabled {
            start()
        } else {
            stop()
            releaseInputForDisable()
            cachedReports = Self.emptyReports
            cachedBootMouseReport = MouseReport.zero.bootData
        }
    }

    static let emptyReports: [UInt8: Data] = [
        ReportID.mouse.rawValue: MouseReport.zero.data,
        ReportID.keyboard.rawValue: KeyboardReport.zero.data,
        ReportID.systemControl.rawValue: SystemControlReport.zero.data,
        ReportID.consumerControl.rawValue: ConsumerReport.zero.data
    ]

    func setAllowed(_ id: UUID, _ allowed: Bool) {
        var policy = hostPolicy
        if allowed { policy.allowed.insert(id) } else { policy.allowed.remove(id) }
        policy.reconcile(ready: hostPolicy.ready)
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
        policy.reconcile(ready: policy.ready)
        apply(policy)
        UserDefaults.standard.removeObject(forKey: AppSettings.allowedHostsKey)
    }

    func reconcileHosts() {
        var policy = hostPolicy
        policy.reconcile(ready: Set(inputSubscriptions.compactMap { id, inputs in
            HIDHostPolicy.inputReady(inputs) ? id : nil
        }))
        apply(policy)
    }

    func reconcileAdvertising() {
        guard let pManager else { return }
        let wanted = hostPolicy.shouldAdvertise(
            enabled: isEnabled && isHIDServiceAllowed,
            poweredOn: state == .poweredOn,
            serviceAdded: isHIDServiceAdded
        )
        if !wanted {
            if isAdvertising || advertisingStarting { pManager.stopAdvertising() }
            advertisingStarting = false
            if isAdvertising { isAdvertising = false }
        } else if !isAdvertising, !advertisingStarting {
            advertisingStarting = true
            var advertisement: [String: Any] = [
                CBAdvertisementDataServiceUUIDsKey: [HIDProfile.hidService]
            ]
            // Match the system Bluetooth identity instead of advertising an app alias.
            if let name = IOBluetoothHostController.default()?.nameAsString(), !name.isEmpty {
                advertisement[CBAdvertisementDataLocalNameKey] = name
                trace("advertising with system Bluetooth name: \(name)")
            }
            pManager.startAdvertising(advertisement)
        }
    }

    func activeRecipients() -> [CBCentral] {
        guard isEnabled, let id = hostPolicy.target, let central = centralObjects[id] else { return [] }
        return [central]
    }

    func inputKind(_ characteristic: CBCharacteristic) -> HIDHostPolicy.Input? {
        if characteristic.uuid == HIDProfile.bootMouseInputReport { return .bootMouse }
        if characteristic.uuid == HIDProfile.bootKeyboardInputReport { return .bootKeyboard }
        switch reportID(forCharacteristic: characteristic) {
        case ReportID.mouse.rawValue: return .mouse
        case ReportID.keyboard.rawValue: return .keyboard
        default: return nil
        }
    }

    func reportID(forCharacteristic char: CBCharacteristic) -> UInt8? {
        for (id, c) in charsByReportID where c.uuid == char.uuid && c === char as AnyObject {
            return id
        }
        // fallback for restored characteristics
        if char.uuid == HIDProfile.report,
           let descriptor = (char as? CBMutableCharacteristic)?
           .descriptors?
           .first(where: { $0.uuid == HIDProfile.reportReference }),
           let value = descriptor.value as? Data,
           let id = value.first
        {
            return id
        }
        return nil
    }

    func drainPendingBroadcast() {
        guard let pManager else { return }
        let recipients = releaseRecipients.isEmpty ? activeRecipients() : releaseRecipients
        guard !recipients.isEmpty else { pendingBroadcasts.removeAll(); return }
        while let entry = pendingBroadcasts.first {
            guard pManager.updateValue(entry.data, for: entry.target, onSubscribedCentrals: recipients) else {
                isReadyToSendNotification = false
                return
            }
            pendingBroadcasts.removeFirst()
        }
        releaseRecipients.removeAll()
        let callbacks = releaseCallbacks
        releaseCallbacks.removeAll()
        callbacks.forEach { $0() }
    }

    func releaseInputForDisable() {
        guard let target = hostPolicy.target, let central = centralObjects[target] else { return }
        releaseRecipients = [central]
        pendingBroadcasts.removeAll()
        for (id, data) in Self.emptyReports {
            if let characteristic = charsByReportID[id] { pendingBroadcasts.append(data, target: characteristic) }
        }
        if let bootMouseInputChar { pendingBroadcasts.append(MouseReport.zero.bootData, target: bootMouseInputChar) }
        if let bootKeyboardInputChar { pendingBroadcasts.append(KeyboardReport.zero.data, target: bootKeyboardInputChar) }
        drainPendingBroadcast()
    }

    func afterInputRelease(_ completion: @escaping () -> Void) {
        if releaseRecipients.isEmpty { completion() } else { releaseCallbacks.append(completion) }
    }
}
