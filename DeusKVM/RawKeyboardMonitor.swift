import CoreGraphics
import IOKit.hid
import IOKit.hidsystem
import os

/// Non-exclusive input monitoring on the tap's run loop. Never seizes the keyboard
/// or changes firmware; the event tap still suppresses local remote-mode events.
final class RawKeyboardMonitor {
    private let manager = IOHIDManagerCreate(kCFAllocatorDefault, IOHIDManagerOptions.independentDevices.rawValue)
    private var state = RawKeyboardState()
    private var monitoring = KeyboardMonitoringState()
    private var devices: [UInt64: IOHIDDevice] = [:]
    private var runLoop: CFRunLoop?
    private var retryTimer: CFRunLoopTimer?
    private var lastStatus: KeyboardMonitoringStatus?
    private let changed: (Keycode, Bool) -> Void
    private let statusChanged: (KeyboardMonitoringStatus) -> Void
    private let canRetry: () -> Bool
    private let log = Logger(subsystem: "io.github.amadeus.deuskvm", category: "Keyboard")

    init(
        changed: @escaping (Keycode, Bool) -> Void,
        statusChanged: @escaping (KeyboardMonitoringStatus) -> Void,
        canRetry: @escaping () -> Bool
    ) {
        self.changed = changed
        self.statusChanged = statusChanged
        self.canRetry = canRetry
    }

    func start(on loop: CFRunLoop) {
        runLoop = loop
        let context = Unmanaged.passUnretained(self).toOpaque()
        IOHIDManagerSetDeviceMatching(manager, [
            kIOHIDDeviceUsagePageKey: 1, kIOHIDDeviceUsageKey: 6
        ] as CFDictionary)
        IOHIDManagerRegisterDeviceMatchingCallback(manager, { context, result, _, device in
            guard result == kIOReturnSuccess, let context else { return }
            let owner = Unmanaged<RawKeyboardMonitor>.fromOpaque(context).takeUnretainedValue()
            owner.devices[RawKeyboardMonitor.identifier(device)] = device
            owner.open(device)
            owner.publishStatus()
        }, context)
        IOHIDManagerRegisterDeviceRemovalCallback(manager, { context, _, _, device in
            guard let context else { return }
            let owner = Unmanaged<RawKeyboardMonitor>.fromOpaque(context).takeUnretainedValue()
            owner.remove(device)
            owner.publishStatus()
        }, context)
        IOHIDManagerScheduleWithRunLoop(manager, loop, CFRunLoopMode.commonModes.rawValue)
        // Independent devices are opened individually in the matching callback.
        // One exclusive-access error must not discard the other input streams.
        publishStatus()
    }

    func stop(on loop: CFRunLoop) {
        if let retryTimer { CFRunLoopTimerInvalidate(retryTimer) }
        retryTimer = nil
        IOHIDManagerUnscheduleFromRunLoop(manager, loop, CFRunLoopMode.commonModes.rawValue)
        for device in Array(devices.values) {
            remove(device)
        }
        runLoop = nil
    }

    private func open(_ device: IOHIDDevice) {
        guard let runLoop else { return }
        let id = Self.identifier(device)
        let result = KeyboardMonitoringPermission.isGranted
            ? IOHIDDeviceOpen(device, IOOptionBits(kIOHIDOptionsTypeNone)) : kIOReturnNotPermitted
        if monitoring.devices[id] != result {
            let product = IOHIDDeviceGetProperty(device, kIOHIDProductKey as CFString) as? String ?? "Keyboard"
            log.notice("Raw keyboard device=\(id) product=\(product, privacy: .public) result=\(result)")
        }
        monitoring.opened(id, result: result)
        guard result == kIOReturnSuccess else { return }
        IOHIDDeviceSetInputValueMatching(device, [kIOHIDElementUsagePageKey: 7] as CFDictionary)
        IOHIDDeviceRegisterInputValueCallback(device, { context, result, _, value in
            guard result == kIOReturnSuccess, let context else { return }
            Unmanaged<RawKeyboardMonitor>.fromOpaque(context).takeUnretainedValue().receive(value)
        }, Unmanaged.passUnretained(self).toOpaque())
        IOHIDDeviceScheduleWithRunLoop(device, runLoop, CFRunLoopMode.commonModes.rawValue)
        snapshot(device)
    }

    private func remove(_ device: IOHIDDevice) {
        let id = Self.identifier(device)
        if monitoring.devices[id] == kIOReturnSuccess, let runLoop {
            IOHIDDeviceUnscheduleFromRunLoop(device, runLoop, CFRunLoopMode.commonModes.rawValue)
            IOHIDDeviceRegisterInputValueCallback(device, nil, nil)
            IOHIDDeviceClose(device, IOOptionBits(kIOHIDOptionsTypeNone))
        }
        devices.removeValue(forKey: id)
        monitoring.remove(id)
        for key in state.remove(device: id) {
            changed(key, false)
        }
    }

    private func publishStatus() {
        let status = monitoring.status(permissionGranted: KeyboardMonitoringPermission.isGranted)
        if status != lastStatus {
            lastStatus = status
            log.notice("Raw keyboard monitoring ready=\(status.isReady)")
            statusChanged(status)
        }
        if monitoring.needsRetry, retryTimer == nil, let runLoop {
            retryTimer = CFRunLoopTimerCreateWithHandler(nil, CFAbsoluteTimeGetCurrent() + 5, 5, 0, 0) { [weak self] _ in
                guard let self, canRetry() else { return }
                // Retry only failed devices, leaving active streams and held-key state intact.
                for (id, result) in monitoring.devices where result != kIOReturnSuccess {
                    if let device = devices[id] { open(device) }
                }
                publishStatus()
            }
            CFRunLoopAddTimer(runLoop, retryTimer, .commonModes)
        } else if !monitoring.needsRetry, let retryTimer {
            CFRunLoopTimerInvalidate(retryTimer)
            self.retryTimer = nil
        }
    }

    private func snapshot(_ device: IOHIDDevice) {
        guard let elements = IOHIDDeviceCopyMatchingElements(device, [kIOHIDElementUsagePageKey: 7] as CFDictionary, 0)
            as? [IOHIDElement] else { return }
        let value = UnsafeMutablePointer<Unmanaged<IOHIDValue>>.allocate(capacity: 1)
        defer { value.deallocate() }
        for element in elements where RawKeyboardState.key(usagePage: 7, usage: IOHIDElementGetUsage(element)) != nil {
            if IOHIDDeviceGetValue(device, element, value) == kIOReturnSuccess {
                receive(value.pointee.takeUnretainedValue())
            }
        }
    }

    private func receive(_ value: IOHIDValue) {
        let element = IOHIDValueGetElement(value)
        guard let key = RawKeyboardState.key(usagePage: IOHIDElementGetUsagePage(element), usage: IOHIDElementGetUsage(element))
        else { return }
        let device = IOHIDElementGetDevice(element)
        let down = IOHIDValueGetIntegerValue(value) != 0
        if state.update(device: Self.identifier(device), key: key, down: down) { changed(key, down) }
    }

    private static func identifier(_ device: IOHIDDevice) -> UInt64 {
        var id: UInt64 = 0
        IORegistryEntryGetRegistryEntryID(IOHIDDeviceGetService(device), &id)
        return id
    }
}
