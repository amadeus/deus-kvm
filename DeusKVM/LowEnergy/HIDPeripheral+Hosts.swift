import CoreBluetooth
import Foundation
import IOBluetooth

/// Allowed-host selection and advertising share one readiness decision.
extension HIDPeripheral {
    func stop() {
        isHIDServiceAllowed = false
        advertisingStarting = false
        transport.stopAdvertising()
        isAdvertising = false
    }

    func setEnabled(_ enabled: Bool) {
        isEnabled = enabled
        companion.setAvailability(enabled: enabled, allowed: hostPolicy.allowed)
        syncTransport()
        if enabled {
            start()
        } else {
            stop()
            releaseInputForDisable()
            transport.resetReports()
        }
        reconcileConnectionLatency()
    }

    /// A preference, not a guaranteed interval. Only the enabled HID target
    /// requests low latency; still-connected former targets return to balanced.
    func reconcileConnectionLatency() {
        guard managerStarted, state == .poweredOn else { lowLatencyCentral = nil; return }
        let next = isEnabled ? hostPolicy.target.flatMap { centralObjects[$0] } : nil
        guard lowLatencyCentral?.identifier != next?.identifier else { return }
        if let previous = lowLatencyCentral, hostPolicy.ready.contains(previous.identifier) {
            transport.setLatency(.medium, for: previous)
        }
        if let next {
            transport.setLatency(.low, for: next)
            trace("requested low connection latency for active HID target: \(next.identifier)")
        }
        lowLatencyCentral = next
    }

    nonisolated static let emptyReports: [UInt8: Data] = [
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
        guard managerStarted else { return }
        let wanted = hostPolicy.shouldAdvertise(
            enabled: isEnabled && isHIDServiceAllowed,
            poweredOn: state == .poweredOn,
            serviceAdded: isHIDServiceAdded
        )
        if !wanted {
            if isAdvertising || advertisingStarting { transport.stopAdvertising() }
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
            transport.startAdvertising(advertisement)
        }
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

    func releaseInputForDisable() {
        guard let target = hostPolicy.target, let central = centralObjects[target] else { return }
        transport.release(to: central)
    }

    /// Runs on the main thread once the release reports reach CoreBluetooth.
    func afterInputRelease(_ completion: @escaping () -> Void) {
        let completion = Unchecked(value: completion)
        transport.afterRelease { DispatchQueue.main.async { MainActor.assumeIsolated { completion.value() } } }
    }

    /// Counts only; resets so each capture health line covers its own interval.
    func takeNotificationDiagnostics() -> String {
        transport.stats.takeHID()
    }
}
