import CoreBluetooth
import XCTest

final class StatusBarConnectionStateTests: XCTestCase {
    func testHandshakeCannotClaimReadyWithoutCaptureOrWhileWaiting() {
        let pc = UUID()
        var link = StatusBarConnectionState.Link(target: pc, companions: [pc], lastSeen: [pc: 100], captureReady: false)
        XCTAssertEqual(StatusBarConnectionState.resolve(enabled: true, bluetooth: .poweredOn, link: link, now: 100), .connecting)
        link.waiting = true
        XCTAssertEqual(StatusBarConnectionState.resolve(enabled: true, bluetooth: .poweredOn, link: link, now: 100), .waiting)
        link.captureReady = true
        XCTAssertEqual(StatusBarConnectionState.resolve(enabled: true, bluetooth: .poweredOn, link: link, now: 100), .waiting)
    }

    func testConnectionProgressAndHeartbeatLoss() {
        let pc = UUID()
        var link = StatusBarConnectionState.Link()
        var now: TimeInterval = 100
        func state() -> StatusBarConnectionState {
            .resolve(
                enabled: true, bluetooth: .poweredOn, link: link, now: now
            )
        }
        XCTAssertEqual(state(), .searching)
        link.subscribers.insert(pc)
        XCTAssertEqual(state(), .connecting)
        link.target = pc
        XCTAssertEqual(state(), .connecting)
        link.companions.insert(pc)
        XCTAssertEqual(state(), .connecting, "HELLO without a heartbeat timestamp cannot claim readiness")
        link.lastSeen[pc] = now
        XCTAssertEqual(state(), .ready)
        now += 10
        XCTAssertEqual(state(), .connecting, "A stale companion must stop displaying ready")
        link.lastSeen[pc] = now
        XCTAssertEqual(state(), .ready)
        link.target = nil
        XCTAssertEqual(state(), .connecting, "Losing HID subscriptions or permission for this PC revokes readiness")
        link.subscribers.removeAll()
        link.companions.removeAll()
        XCTAssertEqual(state(), .searching)
    }

    func testUnrelatedCompanionCannotMakeSelectedPCReady() {
        let pc = UUID(), other = UUID()
        XCTAssertEqual(StatusBarConnectionState.resolve(
            enabled: true, bluetooth: .poweredOn,
            link: .init(target: pc, subscribers: [pc, other], companions: [other], lastSeen: [other: 100]), now: 100
        ), .connecting)
    }

    func testDisabledAndBluetoothUnavailableOverrideRetainedConnections() {
        let pc = UUID()
        for bluetooth: CBManagerState in [.unknown, .resetting, .unsupported, .unauthorized, .poweredOff, .poweredOn] {
            XCTAssertEqual(StatusBarConnectionState.resolve(
                enabled: false, bluetooth: bluetooth,
                link: .init(target: pc, subscribers: [pc], companions: [pc], lastSeen: [pc: 100]), now: 100
            ), .disabled)
            if bluetooth != .poweredOn {
                XCTAssertEqual(StatusBarConnectionState.resolve(
                    enabled: true, bluetooth: bluetooth,
                    link: .init(target: pc, subscribers: [pc], companions: [pc], lastSeen: [pc: 100]), now: 100
                ), .unavailable)
            }
        }
    }
}
