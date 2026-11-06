import CoreGraphics
import IOKit.hid
import IOKit.hidsystem
import os

/// Non-exclusive input monitoring on the tap's run loop. Never seizes the keyboard
/// or changes firmware; the event tap still suppresses local remote-mode events.
final class RawKeyboardMonitor {
    private let manager = IOHIDManagerCreate(kCFAllocatorDefault, IOOptionBits(kIOHIDOptionsTypeNone))
    private var state = RawKeyboardState()
    private var opened = false
    private let changed: (Keycode, Bool) -> Void
    private let log = Logger(subsystem: "io.github.amadeus.deuskvm", category: "Keyboard")

    init(changed: @escaping (Keycode, Bool) -> Void) {
        self.changed = changed
    }

    func start(on loop: CFRunLoop) -> Bool {
        guard IOHIDCheckAccess(kIOHIDRequestTypeListenEvent) == kIOHIDAccessTypeGranted else {
            log.error("Raw keyboard monitoring needs Input Monitoring permission")
            return false
        }
        let context = Unmanaged.passUnretained(self).toOpaque()
        IOHIDManagerSetDeviceMatching(manager, [
            kIOHIDDeviceUsagePageKey: 1, kIOHIDDeviceUsageKey: 6
        ] as CFDictionary)
        IOHIDManagerSetInputValueMatching(manager, [kIOHIDElementUsagePageKey: 7] as CFDictionary)
        IOHIDManagerRegisterInputValueCallback(manager, { context, result, _, value in
            guard result == kIOReturnSuccess, let context else { return }
            Unmanaged<RawKeyboardMonitor>.fromOpaque(context).takeUnretainedValue().receive(value)
        }, context)
        IOHIDManagerRegisterDeviceMatchingCallback(manager, { context, result, _, device in
            guard result == kIOReturnSuccess, let context else { return }
            Unmanaged<RawKeyboardMonitor>.fromOpaque(context).takeUnretainedValue().snapshot(device)
        }, context)
        IOHIDManagerRegisterDeviceRemovalCallback(manager, { context, _, _, device in
            guard let context else { return }
            let owner = Unmanaged<RawKeyboardMonitor>.fromOpaque(context).takeUnretainedValue()
            for key in owner.state.remove(device: RawKeyboardMonitor.identifier(device)) {
                owner.changed(key, false)
            }
        }, context)
        IOHIDManagerScheduleWithRunLoop(manager, loop, CFRunLoopMode.commonModes.rawValue)
        let result = IOHIDManagerOpen(manager, IOOptionBits(kIOHIDOptionsTypeNone))
        opened = result == kIOReturnSuccess
        if !opened { IOHIDManagerUnscheduleFromRunLoop(manager, loop, CFRunLoopMode.commonModes.rawValue) }
        let ready = opened
        log.notice("Raw keyboard monitoring opened=\(ready) result=\(result)")
        return opened
    }

    func stop(on loop: CFRunLoop) {
        guard opened else { return }
        IOHIDManagerUnscheduleFromRunLoop(manager, loop, CFRunLoopMode.commonModes.rawValue)
        IOHIDManagerClose(manager, IOOptionBits(kIOHIDOptionsTypeNone))
        opened = false
    }

    /// Re-reads every keyboard's current key values; a release this monitor missed is
    /// reported through `changed` like any other. Tap thread only.
    func refresh() {
        guard opened, let devices = IOHIDManagerCopyDevices(manager) as? Set<IOHIDDevice> else { return }
        for device in devices {
            snapshot(device)
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
