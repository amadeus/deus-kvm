import Combine
import CoreBluetooth
import os
import SwiftUI

/// HID-over-GATT peripheral engine
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
    private let batteryLevel: UInt8 = 100

    var centralObjects: [UUID: CBCentral] = [:]

    private let log = Logger(subsystem: "io.github.amadeus.deuskvm", category: "HIDPeripheral")
    var pManager: CBPeripheralManager?
    private var batteryServiceObj: CBMutableService?
    private var deviceInfoServiceObj: CBMutableService?
    private var hidServiceObj: CBMutableService?
    var isEnabled = true
    var isHIDServiceAllowed = false
    var isReadyToSendNotification = true

    var charsByReportID: [UInt8: CBMutableCharacteristic] = [:]
    var bootMouseInputChar: CBMutableCharacteristic?
    var bootKeyboardInputChar: CBMutableCharacteristic?
    private var bootKeyboardOutputChar: CBMutableCharacteristic?
    private var batteryLevelChar: CBMutableCharacteristic?
    private var serviceChangedObj: CBMutableService?
    private var serviceChangedArmed = false
    private static let serviceChangedGrace: UInt64 = 2_000_000_000

    /// last-sent payloads for reads and new subscriptions
    var cachedReports = HIDPeripheral.emptyReports

    var pendingBroadcasts = HIDNotificationQueue<CBMutableCharacteristic>()
    var releaseRecipients: [CBCentral] = []
    var releaseCallbacks: [() -> Void] = []
    var cachedBootMouseReport = MouseReport.zero.bootData

    func start() {
        guard isEnabled else { return }
        companion.setAvailability(enabled: isEnabled, allowed: hostPolicy.allowed)
        companion.clipboardTarget = { [weak self] in self?.hostPolicy.target }
        isHIDServiceAllowed = true
        if pManager == nil {
            pManager = CBPeripheralManager(
                delegate: self,
                queue: nil,
                options: [CBPeripheralManagerOptionShowPowerAlertKey: true]
            )
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
        pManager?.stopAdvertising()
        pManager = nil
        advertisingStarting = false
        isAdvertising = false
        isHIDServiceAdded = false
        isReadyToSendNotification = true
        pendingBroadcasts.removeAll()
        releaseRecipients.removeAll()
        releaseCallbacks.removeAll()
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
    }

    func sendMouse(_ report: MouseReport) {
        cachedBootMouseReport = report.bootData
        let sameButtons = report.buttons.rawValue == cachedReports[ReportID.mouse.rawValue]?.first
        let coalesceMotion = sameButtons && report.wheel == 0 && report.pan == 0
        broadcast(report.data, reportID: .mouse, coalesceMotion: coalesceMotion)
    }

    func sendKeyboard(_ report: KeyboardReport) {
        broadcast(report.data, reportID: .keyboard)
        // boot mode hosts read this characteristic instead
        bootKeyboardInputChar.map { _ = updateValue(report.data, for: $0) }
    }

    func sendConsumer(_ report: ConsumerReport) {
        broadcast(report.data, reportID: .consumerControl)
    }

    func sendSystemControl(_ report: SystemControlReport) {
        broadcast(report.data, reportID: .systemControl)
    }

    func apply(_ policy: HIDHostPolicy) {
        if policy.target != hostPolicy.target {
            // Release on the old destination before changing recipients.
            onTargetWillChange?()
            if releaseRecipients.isEmpty { pendingBroadcasts.removeAll() }
            cachedReports = Self.emptyReports
            cachedBootMouseReport = MouseReport.zero.bootData
            keyboardLEDs = []
        }
        if policy != hostPolicy { hostPolicy = policy }
        companion.setAvailability(enabled: isEnabled, allowed: policy.allowed)
        reconcileAdvertising()
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
        guard isEnabled, let pManager else { return }
        if let svc = serviceChangedObj {
            pManager.remove(svc)
            pManager.add(svc)
        } else {
            let svc = buildServiceChangedTrigger()
            serviceChangedObj = svc
            pManager.add(svc)
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
        guard let pManager, !isHIDServiceAdded, batteryServiceObj == nil else { return }
        let battery = buildBatteryService()
        batteryServiceObj = battery
        pManager.add(battery)
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

    private func broadcast(_ data: Data, reportID: ReportID, coalesceMotion: Bool = false) {
        guard isEnabled else { return }
        cachedReports[reportID.rawValue] = data
        guard let char = charsByReportID[reportID.rawValue] else { return }
        _ = updateValue(data, for: char, coalesceMotion: coalesceMotion)
    }

    @discardableResult
    private func updateValue(_ data: Data, for char: CBMutableCharacteristic, coalesceMotion: Bool = false) -> Bool {
        guard let pManager else { return false }
        let recipients = activeRecipients()
        guard !recipients.isEmpty else { return false }
        if !isReadyToSendNotification {
            pendingBroadcasts.append(data, target: char, coalesceMotion: coalesceMotion)
            return false
        }
        let accepted = pManager.updateValue(data, for: char, onSubscribedCentrals: recipients)
        if !accepted {
            isReadyToSendNotification = false
            pendingBroadcasts.append(data, target: char, coalesceMotion: coalesceMotion)
        }
        return accepted
    }

    func trace(_ message: @autoclosure () -> String) {
        guard UserDefaults.standard.bool(forKey: AppSettings.developerModeKey) else { return }
        let text = message()
        log.info("\(text, privacy: .public)")
    }

    private func _trackInteraction(from central: CBCentral) {
        centralObjects[central.identifier] = central
        guard !connectedCentrals.contains(central.identifier) else { return }
        connectedCentrals.insert(central.identifier)
        trace("central tracked: \(central.identifier)")
    }
}

extension HIDPeripheral: @preconcurrency CBPeripheralManagerDelegate {
    func peripheralManagerDidUpdateState(_ peripheral: CBPeripheralManager) {
        state = peripheral.state
        trace("CB state -> \(peripheral.state.rawValue)")
        if peripheral.state == .poweredOn, isHIDServiceAllowed, !isHIDServiceAdded {
            installServices()
        }
        if peripheral.state != .poweredOn {
            advertisingStarting = false
            isAdvertising = false
            inputSubscriptions.removeAll()
            subscribedCentrals.removeAll()
            connectedCentrals.removeAll()
            reconcileHosts()
        } else { reconcileAdvertising() }
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, didAdd service: CBService, error: Error?) {
        if let error {
            log.error("didAddService(\(service.uuid)) error: \(error.localizedDescription, privacy: .public)")
            lastError = error.localizedDescription
        }
        switch service.uuid {
        case HIDProfile.batteryService:
            let dis = buildDeviceInfoService()
            deviceInfoServiceObj = dis
            peripheral.add(dis)
        case HIDProfile.deviceInformationService:
            let hid = buildHIDService(includingBattery: batteryServiceObj)
            hidServiceObj = hid
            peripheral.add(hid)
        case HIDProfile.hidService:
            guard error == nil else { return }
            isHIDServiceAdded = true
            peripheral.add(companion.build(peripheral))
        case CompanionService.uuid:
            reconcileAdvertising()
        default:
            break
        }
    }

    func peripheralManagerDidStartAdvertising(_ peripheral: CBPeripheralManager, error: Error?) {
        advertisingStarting = false
        if let error {
            isAdvertising = false
            lastError = error.localizedDescription
            log.error("startAdvertising error: \(error.localizedDescription, privacy: .public)")
        } else {
            isAdvertising = true
            trace("advertising started")
            reconcileAdvertising()
        }
    }

    func peripheralManager(
        _ peripheral: CBPeripheralManager,
        central: CBCentral,
        didSubscribeTo characteristic: CBCharacteristic
    ) {
        if companion.owns(characteristic) { companion.subscribed(central, characteristic); return }
        _trackInteraction(from: central)
        subscribedCentrals[central.identifier, default: []].insert(characteristic.uuid)
        trace("subscribe: \(central.identifier) -> \(characteristic.uuid)")
        if let input = inputKind(characteristic) {
            inputSubscriptions[central.identifier, default: []].insert(input)
        }
        reconcileHosts()
        guard central.identifier == hostPolicy.target else { return }
        if let id = reportID(forCharacteristic: characteristic),
           let cached = cachedReports[id],
           let char = charsByReportID[id]
        {
            _ = updateValue(cached, for: char)
        } else if characteristic.uuid == HIDProfile.bootMouseInputReport, let bootMouseInputChar {
            _ = updateValue(MouseReport.zero.bootData, for: bootMouseInputChar)
        } else if characteristic.uuid == HIDProfile.bootKeyboardInputReport, let bootKeyboardInputChar {
            _ = updateValue(KeyboardReport.zero.data, for: bootKeyboardInputChar)
        } else if characteristic.uuid == HIDProfile.batteryLevel, let batteryLevelChar {
            _ = updateValue(Data([batteryLevel]), for: batteryLevelChar)
        }
    }

    func peripheralManager(
        _ peripheral: CBPeripheralManager,
        central: CBCentral,
        didUnsubscribeFrom characteristic: CBCharacteristic
    ) {
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
            if releaseRecipients.contains(where: { $0.identifier == central.identifier }) {
                pendingBroadcasts.removeAll(); releaseRecipients.removeAll(); releaseCallbacks.removeAll()
            }
            subscribedCentrals.removeValue(forKey: central.identifier)
            centralObjects.removeValue(forKey: central.identifier)
            inputSubscriptions.removeValue(forKey: central.identifier)
            connectedCentrals.remove(central.identifier)
            serviceChangedArmed = false
        } else {
            subscribedCentrals[central.identifier] = chars
        }
    }

    func peripheralManagerIsReady(toUpdateSubscribers peripheral: CBPeripheralManager) {
        isReadyToSendNotification = true
        drainPendingBroadcast()
        companion.readyToSend()
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, didReceiveRead request: CBATTRequest) {
        trace("read: \(request.central.identifier) -> \(request.characteristic.uuid)")
        if companion.owns(request.characteristic) { companion.respond(to: request, using: peripheral); return }
        _trackInteraction(from: request.central)
        scheduleServiceChanged()
        let value = readValue(forRequest: request)
        guard let value else {
            peripheral.respond(to: request, withResult: .invalidAttributeValueLength)
            return
        }
        guard request.offset <= value.count else {
            peripheral.respond(to: request, withResult: .invalidOffset)
            return
        }
        request.value = value.subdata(in: request.offset ..< value.count)
        peripheral.respond(to: request, withResult: .success)
    }

    private func readValue(forRequest request: CBATTRequest) -> Data? {
        let reports = request.central.identifier == hostPolicy.target ? cachedReports : Self.emptyReports
        switch request.characteristic.uuid {
        case HIDProfile.batteryLevel: return Data([batteryLevel])
        case HIDProfile.hidInformation: return HIDProfile.hidInformationValue
        case HIDProfile.reportMap: return HIDProfile.reportMapData
        case HIDProfile.protocolMode: return Data([0x01]) // report protocol
        case HIDProfile.manufacturerName: return HIDProfile.manufacturerNameValue
        case HIDProfile.modelNumber: return HIDProfile.modelNumberValue
        case HIDProfile.pnpID: return HIDProfile.pnpIDValue
        case HIDProfile.report:
            if let id = reportID(forCharacteristic: request.characteristic) {
                return reports[id] ?? Data()
            }
            return Data()
        case HIDProfile.bootMouseInputReport:
            return request.central.identifier == hostPolicy.target ? cachedBootMouseReport : MouseReport.zero.bootData
        case HIDProfile.bootKeyboardInputReport: return reports[ReportID.keyboard.rawValue]
        default: return Data()
        }
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, didReceiveWrite requests: [CBATTRequest]) {
        if let first = requests.first, companion.owns(first.characteristic) {
            var result = CBATTError.Code.success
            for request in requests {
                result = companion.receive(request)
                if result != .success { break }
            }
            peripheral.respond(to: first, withResult: result)
            return
        }
        for request in requests {
            trace("write: \(request.central.identifier) -> \(request.characteristic.uuid)")
            _trackInteraction(from: request.central)
            handleWriteRequest(request)
        }
        if let first = requests.first {
            peripheral.respond(to: first, withResult: .success)
        }
    }

    private func handleWriteRequest(_ request: CBATTRequest) {
        guard request.central.identifier == hostPolicy.target, let value = request.value else { return }
        switch request.characteristic.uuid {
        case HIDProfile.bootKeyboardOutputReport:
            if let byte = value.first { keyboardLEDs = KeyboardLEDs(byte: byte) }
        case HIDProfile.report:
            if reportID(forCharacteristic: request.characteristic) == ReportID.keyboardLEDs.rawValue,
               let byte = value.first
            {
                keyboardLEDs = KeyboardLEDs(byte: byte)
            }
        case HIDProfile.protocolMode, HIDProfile.hidControlPoint:
            break
        default:
            break
        }
    }
}
