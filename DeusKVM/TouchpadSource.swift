import CoreFoundation
import Foundation

/// Raw contact frames from the Mac's trackpad, system-wide, through the private
/// MultitouchSupport framework: the calls and record layout `scripts/mtprobe.c` validated on
/// this Mac. The framework is loaded at run time, so a missing framework or symbol only
/// leaves the mouse path in place.
///
/// One session at a time. `start` creates the device list and starts every device; frames
/// arrive on the framework's own thread, go through the tracker under the lock and leave
/// through `deliver`. `presence` is readable from any thread, the tap's included.
final class TouchpadSource: @unchecked Sendable {
    typealias Delivery = @Sendable ([TouchpadContact], TimeInterval) -> Void
    typealias Fault = @Sendable (String) -> Void

    enum StartOutcome: Sendable, Equatable {
        case started(String)
        case unavailable(String)
    }

    private typealias DeviceRef = UnsafeMutableRawPointer
    private typealias Callback = @convention(c) (DeviceRef?, UnsafeRawPointer?, Int32, Double, Int32) -> Int32
    private typealias CreateList = @convention(c) () -> Unmanaged<CFArray>?
    private typealias Register = @convention(c) (DeviceRef?, Callback) -> Void
    private typealias Start = @convention(c) (DeviceRef?, Int32) -> Int32
    private typealias Stop = @convention(c) (DeviceRef?) -> Void
    private typealias Family = @convention(c) (DeviceRef?, UnsafeMutablePointer<Int32>?) -> Int32
    private typealias Dimensions = @convention(c) (DeviceRef?, UnsafeMutablePointer<Int32>?, UnsafeMutablePointer<Int32>?) -> Int32

    private struct API: @unchecked Sendable {
        let createList: CreateList
        let register: Register
        let start: Start
        let stop: Stop
        let unregister: Register?
        let family: Family?
        let dimensions: Dimensions?

        static func load() -> API? {
            guard let handle = dlopen("/System/Library/PrivateFrameworks/MultitouchSupport.framework/MultitouchSupport", RTLD_NOW)
            else { return nil }
            func symbol<T>(_ name: String, as _: T.Type) -> T? {
                dlsym(handle, name).map { unsafeBitCast($0, to: T.self) }
            }
            guard let createList = symbol("MTDeviceCreateList", as: CreateList.self),
                  let register = symbol("MTRegisterContactFrameCallback", as: Register.self),
                  let start = symbol("MTDeviceStart", as: Start.self),
                  let stop = symbol("MTDeviceStop", as: Stop.self)
            else { return nil }
            return API(
                createList: createList, register: register, start: start, stop: stop,
                unregister: symbol("MTUnregisterContactFrameCallback", as: Register.self),
                family: symbol("MTDeviceGetFamilyID", as: Family.self),
                dimensions: symbol("MTDeviceGetSensorSurfaceDimensions", as: Dimensions.self)
            )
        }
    }

    /// Byte offsets into the framework's 96-byte contact record (`MTTouch` in the probe):
    /// frame, timestamp, identifier, state, finger, hand, normalized position and velocity.
    private enum Record {
        static let stride = 96
        static let identifier = 16
        static let state = 20
        static let x = 32
        static let y = 36
    }

    /// The C callback cannot capture, so it finds the running source here.
    private final class Registry: @unchecked Sendable {
        private let lock = NSLock()
        private var source: TouchpadSource?

        var current: TouchpadSource? {
            get { lock.withLock { source } }
            set { lock.withLock { source = newValue } }
        }
    }

    private static let api = API.load()
    private static let registry = Registry()

    private static let callback: Callback = { _, touches, count, timestamp, _ in
        registry.current?.receive(touches, count: Int(count), timestamp: timestamp)
        return 0
    }

    private let lock = NSLock()
    private var running = false
    private var verified = false
    private var faulted = false
    private var tracker = TouchpadTracker()
    private var activity = TouchpadActivity()
    /// The network pointer stream is up: a lone finger is not reported as a contact.
    private var hidesLoneFinger = false
    private var deliver: Delivery?
    private var fault: Fault?
    /// The device list keeps its devices alive; both go away when the session stops.
    private struct Session {
        let list: CFArray
        let devices: [DeviceRef]
    }

    private var session: Session?
    /// Uptime minus the framework's frame timestamp at the first verified frame: tells the
    /// log which clock the framework stamps frames with.
    private(set) var frameClockOffset: Double?

    /// Starts one session's stream. Creating the device list per session is what makes
    /// trackpad reconnects and sleep/wake work without special handling.
    func start(deliver: @escaping Delivery, fault: @escaping Fault) -> StartOutcome {
        guard let api = Self.api else { return .unavailable("MultitouchSupport is not available") }
        let fresh: Bool = lock.withLock {
            guard !running else { return false }
            running = true
            verified = false
            faulted = false
            frameClockOffset = nil
            tracker = TouchpadTracker()
            activity = TouchpadActivity()
            self.deliver = deliver
            self.fault = fault
            return true
        }
        guard fresh else { return .started("already streaming") }
        Self.registry.current = self
        guard let list = api.createList()?.takeRetainedValue() else { return finishStart(nil, "no device list") }
        var started: [DeviceRef] = []
        var details: [String] = []
        for index in 0 ..< CFArrayGetCount(list) {
            guard let value = CFArrayGetValueAtIndex(list, index) else { continue }
            let device = DeviceRef(mutating: value)
            var family: Int32 = -1, width: Int32 = -1, height: Int32 = -1
            _ = api.family?(device, &family)
            _ = api.dimensions?(device, &width, &height)
            api.register(device, Self.callback)
            let status = api.start(device, 0)
            details.append("family=\(family) surface=\(width)x\(height) start=\(status)")
            if status == 0 { started.append(device) } else { api.unregister?(device, Self.callback) }
        }
        let detail = "devices=\(started.count)/\(CFArrayGetCount(list)) unregister=\(api.unregister != nil) " +
            details.joined(separator: " ")
        return finishStart(started.isEmpty ? nil : Session(list: list, devices: started), detail)
    }

    private func finishStart(_ session: Session?, _ detail: String) -> StartOutcome {
        lock.withLock {
            self.session = session
            if session == nil { running = false }
        }
        guard session != nil else { Self.registry.current = nil; return .unavailable(detail) }
        return .started(detail)
    }

    /// Stops the stream and returns the tip-off reports for every finger still down.
    func stop() -> [TouchpadContact] {
        let (session, lifted): (Session?, [TouchpadContact]) = lock.withLock {
            guard running else { return (nil, []) }
            running = false
            defer { self.session = nil; deliver = nil; fault = nil }
            return (self.session, faulted ? [] : tracker.lift())
        }
        Self.registry.current = nil
        if let api = Self.api, let session {
            for device in session.devices {
                api.stop(device)
                api.unregister?(device, Self.callback)
            }
        }
        return lifted
    }

    /// Any thread: whether lone fingers stay out of the contact reports (see `TouchpadTracker.track`).
    func setHidesLoneFinger(_ value: Bool) {
        lock.withLock { hidesLoneFinger = value }
    }

    /// Any thread: what the tap should assume about the pad right now.
    func presence(now: TimeInterval) -> TouchpadPresence {
        lock.withLock { running && !faulted ? activity.presence(now: now) : .none }
    }

    /// For the session's diagnostics line: the frame clock's offset from uptime in ms.
    var clockDetail: String {
        lock.withLock { frameClockOffset.map { "frameClockOffsetMs=\(Int($0 * 1000))" } ?? "frameClockOffsetMs=none" }
    }

    // MARK: Framework thread

    private func receive(_ touches: UnsafeRawPointer?, count: Int, timestamp: Double) {
        var frame: [TouchpadRawContact] = []
        if let touches, count > 0 {
            frame.reserveCapacity(count)
            for index in 0 ..< count {
                let base = index * Record.stride
                frame.append(TouchpadRawContact(
                    identifier: touches.loadUnaligned(fromByteOffset: base + Record.identifier, as: Int32.self),
                    state: touches.loadUnaligned(fromByteOffset: base + Record.state, as: Int32.self),
                    x: touches.loadUnaligned(fromByteOffset: base + Record.x, as: Float.self),
                    y: touches.loadUnaligned(fromByteOffset: base + Record.y, as: Float.self)
                ))
            }
        }
        // Stamped here, within a fraction of a millisecond of the frame; the framework's own
        // timestamp base is only recorded for the log.
        let now = ProcessInfo.processInfo.systemUptime
        let outcome: Outcome = lock.withLock {
            guard running, !faulted else { return Outcome() }
            if !verified, !frame.isEmpty {
                guard frame.allSatisfy(Self.plausible) else {
                    faulted = true
                    return Outcome(fault: fault)
                }
                verified = true
                frameClockOffset = now - timestamp
            }
            activity.update(touching: frame.filter(\.isTouching).count, now: now)
            return Outcome(contacts: tracker.track(frame, hidingLoneFinger: hidesLoneFinger), deliver: deliver)
        }
        if let fault = outcome.fault, let first = frame.first {
            fault("record layout mismatch: id=\(first.identifier) state=\(first.state) x=\(first.x) y=\(first.y)")
        } else if !outcome.contacts.isEmpty, let deliver = outcome.deliver {
            deliver(outcome.contacts, now)
        }
    }

    /// What one frame produced under the lock, delivered outside it.
    private struct Outcome {
        var contacts: [TouchpadContact] = []
        var deliver: Delivery?
        var fault: Fault?
    }

    /// The fields the probe validated: small identifiers, states 0...7 and positions in or
    /// barely outside 0...1. Anything else means the record layout changed.
    private static func plausible(_ contact: TouchpadRawContact) -> Bool {
        (0 ..< 4096).contains(contact.identifier) && (0 ... 7).contains(contact.state) &&
            contact.x.isFinite && contact.y.isFinite &&
            (-0.05 ... 1.05).contains(contact.x) && (-0.05 ... 1.05).contains(contact.y)
    }
}
