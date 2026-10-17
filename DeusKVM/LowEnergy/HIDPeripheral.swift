import Combine
import CoreBluetooth
import os
import SwiftUI

/// HID-over-GATT peripheral engine. Host selection, services and published state stay on the
/// main actor; CoreBluetooth, report sending and captured input run in BluetoothTransport.
@MainActor
final class HIDPeripheral: NSObject, ObservableObject {
    @Published private(set) var state: CBManagerState = .unknown
    @Published var isAdvertising = false
    @Published private(set) var isHIDServiceAdded = false
    /// central identifier: the set of characteristic UUIDs it is currently subscribed to
    @Published private(set) var subscribedCentrals: [UUID: Set<CBUUID>] = [:]
    @Published private(set) var hostPolicy = HIDHostPolicy(allowed: Set(
        (UserDefaults.standard.stringArray(forKey: AppSettings.allowedHostsKey) ?? []).compactMap(UUID.init(uuidString:))
    ))
    var onTargetWillChange: (() -> Void)?
    var inputSubscriptions: [UUID: Set<HIDHostPolicy.Input>] = [:]
    var advertisingStarting = false
    @Published private(set) var connectedCentrals: Set<UUID> = []
    @Published private(set) var keyboardLEDs: KeyboardLEDs = []
    @Published private(set) var lastError: String?

    let companion = CompanionService()
    let transport = BluetoothTransport()
    /// Matches the transport's manager epoch; callbacks from a replaced manager are ignored.
    private(set) var transportEpoch = 0
    var managerStarted = false
    private var route: TransportRoute?

    var centralObjects: [UUID: CBCentral] = [:]
    var lowLatencyCentral: CBCentral?

    private let log = Logger(subsystem: "io.github.amadeus.deuskvm", category: "HIDPeripheral")
    private var batteryServiceObj: CBMutableService?
    private var deviceInfoServiceObj: CBMutableService?
    private var hidServiceObj: CBMutableService?
    var isEnabled = true
    var isHIDServiceAllowed = false

    var charsByReportID: [UInt8: CBMutableCharacteristic] = [:]
    var bootMouseInputChar: CBMutableCharacteristic?
    var bootKeyboardInputChar: CBMutableCharacteristic?
    private var bootKeyboardOutputChar: CBMutableCharacteristic?
    private var batteryLevelChar: CBMutableCharacteristic?
    private var serviceChangedObj: CBMutableService?
    private var serviceChangedArmed = false
    private static let serviceChangedGrace: UInt64 = 2_000_000_000

    private struct TransportRoute: Equatable {
        let id: UUID?
        let central: ObjectIdentifier?
        let enabled: Bool
        let bootKeyboard: Bool
    }

    override init() {
        super.init()
        transport.attach(self)
        companion.transport = transport
    }

    func start() {
        guard isEnabled else { return }
        companion.setAvailability(enabled: isEnabled, allowed: hostPolicy.allowed)
        companion.clipboardTarget = { [weak self] in self?.hostPolicy.target }
        isHIDServiceAllowed = true
        if !managerStarted {
            managerStarted = true
            transport.startManager()
        } else if state == .poweredOn {
            if isHIDServiceAdded {
                reconcileAdvertising()
            } else {
                installServices()
            }
        }
    }

    func promptPowerAlert() {
        guard state != .poweredOn else { return }
        _resetForRestart()
        start()
    }

    private func _resetForRestart() {
        transport.restart()
        transportEpoch += 1
        managerStarted = false
        advertisingStarting = false
        isAdvertising = false
        isHIDServiceAdded = false
        companion.reset()
        batteryServiceObj = nil
        deviceInfoServiceObj = nil
        hidServiceObj = nil
        serviceChangedObj = nil
        serviceChangedArmed = false
        batteryLevelChar = nil
        bootMouseInputChar = nil
        bootKeyboardInputChar = nil
        bootKeyboardOutputChar = nil
        charsByReportID.removeAll()
        subscribedCentrals.removeAll()
        inputSubscriptions.removeAll()
        reconcileHosts()
        connectedCentrals.removeAll()
        centralObjects.removeAll()
        syncTransport()
    }

    func apply(_ policy: HIDHostPolicy) {
        if policy.target != hostPolicy.target {
            // Release on the old destination before changing recipients.
            onTargetWillChange?()
            transport.retarget()
            if !keyboardLEDs.isEmpty { keyboardLEDs = [] }
        }
        if policy != hostPolicy { hostPolicy = policy }
        syncTransport()
        reconcileConnectionLatency()
        companion.setAvailability(enabled: isEnabled, allowed: policy.allowed)
        reconcileAdvertising()
    }

    /// Pushes the selected host to the Bluetooth queue when its central, enablement or keyboard
    /// protocol changes. Input and releases there use the route in effect when they run.
    func syncTransport() {
        let id = hostPolicy.target
        let central = id.flatMap { centralObjects[$0] }
        let bootKeyboard = id.map { HIDHostPolicy.needsBootKeyboard(inputSubscriptions[$0] ?? []) } ?? false
        let next = TransportRoute(id: id, central: central.map(ObjectIdentifier.init), enabled: isEnabled, bootKeyboard: bootKeyboard)
        guard next != route else { return }
        route = next
        transport.route(target: central, id: id, enabled: isEnabled, bootKeyboard: bootKeyboard)
    }

    /// if host connects but never subscribes (stale GATT cache), cycle a temp service to fire Service Changed so it re-discovers
    func scheduleServiceChanged() {
        guard UserDefaults.standard.bool(forKey: AppSettings.useServiceChangedKey), !serviceChangedArmed else { return }
        serviceChangedArmed = true
        Task { [weak self] in
            try? await Task.sleep(nanoseconds: Self.serviceChangedGrace)
            self?._cycleServiceChangedIfUnsubscribed()
        }
    }

    private func _cycleServiceChangedIfUnsubscribed() {
        guard isEnabled, subscribedCentrals.isEmpty else { return }
        _cycleServiceChanged()
    }

    private func _cycleServiceChanged() {
        guard isEnabled, managerStarted else { return }
        if let svc = serviceChangedObj {
            transport.remove(svc)
            transport.add(svc)
        } else {
            let svc = buildServiceChangedTrigger()
            serviceChangedObj = svc
            transport.add(svc)
        }
        trace("Service Changed cycled")
    }

    /// temp service used to perturb the GATT to trigger Service Changed
    private func buildServiceChangedTrigger() -> CBMutableService {
        let service = CBMutableService(type: CBUUID(nsuuid: UUID()), primary: true)
        service.characteristics = [
            CBMutableCharacteristic(
                type: CBUUID(nsuuid: UUID()),
                properties: [.read, .notifyEncryptionRequired],
                value: nil,
                permissions: .readEncryptionRequired
            )
        ]
        return service
    }

    private func installServices() {
        guard managerStarted, !isHIDServiceAdded, batteryServiceObj == nil else { return }
        let battery = buildBatteryService()
        batteryServiceObj = battery
        transport.add(battery)
    }

    private func buildDeviceInfoService() -> CBMutableService {
        let service = CBMutableService(type: HIDProfile.deviceInformationService, primary: true)
        service.characteristics = [
            CBMutableCharacteristic(
                type: HIDProfile.manufacturerName,
                properties: .read,
                value: HIDProfile.manufacturerNameValue,
                permissions: .readable
            ),
            CBMutableCharacteristic(
                type: HIDProfile.modelNumber,
                properties: .read,
                value: HIDProfile.modelNumberValue,
                permissions: .readable
            ),
            CBMutableCharacteristic(
                type: HIDProfile.pnpID,
                properties: .read,
                value: HIDProfile.pnpIDValue,
                permissions: .readable
            )
        ]
        return service
    }

    private func buildBatteryService() -> CBMutableService {
        let service = CBMutableService(type: HIDProfile.batteryService, primary: true)
        let level = CBMutableCharacteristic(
            type: HIDProfile.batteryLevel,
            properties: [.read, .notifyEncryptionRequired],
            value: nil,
            permissions: .readEncryptionRequired
        )
        // expose battery as HID Report ID 4 too
        let reportRef = CBMutableDescriptor(
            type: HIDProfile.reportReference,
            value: NSData(data: ReportID.battery.descriptor(.input))
        )
        level.descriptors = [reportRef]
        service.characteristics = [level]
        batteryLevelChar = level
        return service
    }

    private func buildHIDService(includingBattery battery: CBMutableService?) -> CBMutableService {
        let service = CBMutableService(type: HIDProfile.hidService, primary: true)
        if let battery { service.includedServices = [battery] }

        let controlPoint = CBMutableCharacteristic(
            type: HIDProfile.hidControlPoint,
            properties: .read,
            value: nil,
            permissions: .readEncryptionRequired
        )

        let protocolMode = CBMutableCharacteristic(
            type: HIDProfile.protocolMode,
            properties: [.read, .writeWithoutResponse],
            value: nil,
            permissions: [.readEncryptionRequired, .writeEncryptionRequired]
        )

        let hidInfo = CBMutableCharacteristic(
            type: HIDProfile.hidInformation,
            properties: .read,
            value: HIDProfile.hidInformationValue,
            permissions: .readEncryptionRequired
        )

        let bootMouseInput = CBMutableCharacteristic(
            type: HIDProfile.bootMouseInputReport,
            properties: [.read, .notifyEncryptionRequired],
            value: nil,
            permissions: [.readEncryptionRequired, .writeEncryptionRequired]
        )
        let bootKbdInput = CBMutableCharacteristic(
            type: HIDProfile.bootKeyboardInputReport,
            properties: [.read, .notifyEncryptionRequired],
            value: nil,
            permissions: [.readEncryptionRequired, .writeEncryptionRequired]
        )
        let bootKbdOutput = CBMutableCharacteristic(
            type: HIDProfile.bootKeyboardOutputReport,
            properties: [.read, .writeWithoutResponse, .write],
            value: nil,
            permissions: [.readEncryptionRequired, .writeEncryptionRequired]
        )

        // links the HID report map to battery level
        let reportMap = CBMutableCharacteristic(
            type: HIDProfile.reportMap,
            properties: .read,
            value: HIDProfile.reportMapData,
            permissions: .readEncryptionRequired
        )
        reportMap.descriptors = [
            CBMutableDescriptor(
                type: HIDProfile.externalReportReference,
                value: NSData(data: HIDProfile.externalReportReferenceValue)
            )
        ]

        // report characteristic order matters
        let systemReportChar = makeReportChar(.systemControl, type: .input)
        let consumerReportChar = makeReportChar(.consumerControl, type: .input)
        let mouseReportChar = makeReportChar(.mouse, type: .input)
        let keyboardReportChar = makeReportChar(.keyboard, type: .input)
        let outputReportChar = CBMutableCharacteristic(
            type: HIDProfile.report,
            properties: [.read, .writeWithoutResponse, .write],
            value: nil,
            permissions: [.readEncryptionRequired, .writeEncryptionRequired]
        )
        outputReportChar.descriptors = [
            CBMutableDescriptor(
                type: HIDProfile.reportReference,
                value: NSData(data: ReportID.keyboardLEDs.descriptor(.output))
            )
        ]

        service.characteristics = [
            controlPoint,
            protocolMode,
            hidInfo,
            bootMouseInput,
            bootKbdInput,
            bootKbdOutput,
            reportMap,
            systemReportChar,
            consumerReportChar,
            mouseReportChar,
            keyboardReportChar,
            outputReportChar
        ]

        bootMouseInputChar = bootMouseInput
        bootKeyboardInputChar = bootKbdInput
        bootKeyboardOutputChar = bootKbdOutput
        charsByReportID[ReportID.systemControl.rawValue] = systemReportChar
        charsByReportID[ReportID.consumerControl.rawValue] = consumerReportChar
        charsByReportID[ReportID.mouse.rawValue] = mouseReportChar
        charsByReportID[ReportID.keyboard.rawValue] = keyboardReportChar
        charsByReportID[ReportID.keyboardLEDs.rawValue] = outputReportChar

        return service
    }

    private func makeReportChar(_ id: ReportID, type: ReportType) -> CBMutableCharacteristic {
        let char = CBMutableCharacteristic(
            type: HIDProfile.report,
            properties: [.read, .notifyEncryptionRequired],
            value: nil,
            permissions: .readEncryptionRequired
        )
        char.descriptors = [
            CBMutableDescriptor(type: HIDProfile.reportReference, value: NSData(data: id.descriptor(type)))
        ]
        return char
    }

    func trace(_ message: @autoclosure () -> String) {
        guard UserDefaults.standard.bool(forKey: AppSettings.developerModeKey) else { return }
        let text = message()
        log.info("\(text, privacy: .public)")
    }

    private func _trackInteraction(from central: CBCentral) {
        centralObjects[central.identifier] = central
        syncTransport()
        guard !connectedCentrals.contains(central.identifier) else { return }
        connectedCentrals.insert(central.identifier)
        trace("central tracked: \(central.identifier)")
    }
}

/// CoreBluetooth callbacks forwarded in order from BluetoothTransport.
extension HIDPeripheral {
    func transportStateDidChange(_ newState: CBManagerState) {
        state = newState
        trace("CB state -> \(newState.rawValue)")
        if newState == .poweredOn, isHIDServiceAllowed, !isHIDServiceAdded {
            installServices()
        }
        if newState != .poweredOn {
            advertisingStarting = false
            isAdvertising = false
            inputSubscriptions.removeAll()
            subscribedCentrals.removeAll()
            connectedCentrals.removeAll()
            reconcileHosts()
        } else { reconcileAdvertising() }
    }

    func transportDidAdd(_ uuid: CBUUID, error: String?) {
        if let error {
            log.error("didAddService(\(uuid)) error: \(error, privacy: .public)")
            lastError = error
        }
        switch uuid {
        case HIDProfile.batteryService:
            let dis = buildDeviceInfoService()
            deviceInfoServiceObj = dis
            transport.add(dis)
        case HIDProfile.deviceInformationService:
            let hid = buildHIDService(includingBattery: batteryServiceObj)
            hidServiceObj = hid
            transport.configureHID(
                reports: charsByReportID, bootMouse: bootMouseInputChar,
                bootKeyboard: bootKeyboardInputChar, battery: batteryLevelChar
            )
            transport.add(hid)
        case HIDProfile.hidService:
            guard error == nil else { return }
            isHIDServiceAdded = true
            transport.add(companion.build())
        case CompanionService.uuid:
            reconcileAdvertising()
        default:
            break
        }
    }

    func transportDidStartAdvertising(error: String?) {
        advertisingStarting = false
        if let error {
            isAdvertising = false
            lastError = error
            log.error("startAdvertising error: \(error, privacy: .public)")
        } else {
            isAdvertising = true
            trace("advertising started")
            reconcileAdvertising()
        }
    }

    func transportDidSubscribe(_ central: CBCentral, to characteristic: CBCharacteristic) {
        if companion.owns(characteristic) { companion.subscribed(central, characteristic); return }
        _trackInteraction(from: central)
        subscribedCentrals[central.identifier, default: []].insert(characteristic.uuid)
        trace("subscribe: \(central.identifier) -> \(characteristic.uuid)")
        if let input = inputKind(characteristic) {
            inputSubscriptions[central.identifier, default: []].insert(input)
        }
        reconcileHosts()
        guard central.identifier == hostPolicy.target else { return }
        transport.replay(characteristic)
    }

    func transportDidUnsubscribe(_ central: CBCentral, from characteristic: CBCharacteristic) {
        if companion.owns(characteristic) { companion.unsubscribed(central); return }
        trace("unsubscribe: \(central.identifier) <- \(characteristic.uuid)")
        if let input = inputKind(characteristic) {
            inputSubscriptions[central.identifier]?.remove(input)
        }
        reconcileHosts()
        guard var chars = subscribedCentrals[central.identifier] else { return }
        // Several report characteristics share 0x2A4D. Retain the summary while
        // this central still subscribes to another input report.
        if characteristic.uuid != HIDProfile.report ||
            !(inputSubscriptions[central.identifier]?.contains(.mouse) == true ||
                inputSubscriptions[central.identifier]?.contains(.keyboard) == true)
        {
            chars.remove(characteristic.uuid)
        }
        if chars.isEmpty {
            transport.dropRelease(for: central.identifier)
            subscribedCentrals.removeValue(forKey: central.identifier)
            centralObjects.removeValue(forKey: central.identifier)
            inputSubscriptions.removeValue(forKey: central.identifier)
            connectedCentrals.remove(central.identifier)
            serviceChangedArmed = false
            syncTransport()
        } else {
            subscribedCentrals[central.identifier] = chars
        }
    }

    /// The transport already answered from its replay state; this is bookkeeping.
    func transportDidRead(_ central: CBCentral, characteristic: CBCharacteristic) {
        trace("read: \(central.identifier) -> \(characteristic.uuid)")
        _trackInteraction(from: central)
        scheduleServiceChanged()
    }

    /// The transport already acknowledged these HID writes; keyboard LEDs and bookkeeping remain.
    func transportDidWrite(_ writes: [HIDWrite]) {
        for write in writes {
            let characteristic = write.characteristic
            trace("write: \(write.central.identifier) -> \(characteristic.uuid)")
            _trackInteraction(from: write.central)
            guard write.central.identifier == hostPolicy.target, let byte = write.value?.first else { continue }
            // Windows repeats LED output reports; unchanged values must not re-render the app scene.
            let isLEDReport = characteristic.uuid == HIDProfile.bootKeyboardOutputReport ||
                (characteristic.uuid == HIDProfile.report && reportID(forCharacteristic: characteristic) == ReportID.keyboardLEDs.rawValue)
            if isLEDReport, KeyboardLEDs(byte: byte) != keyboardLEDs { keyboardLEDs = KeyboardLEDs(byte: byte) }
        }
    }
}
