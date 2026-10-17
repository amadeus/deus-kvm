import XCTest

final class HIDHostPolicyTests: XCTestCase {
    private let pc = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!
    private let other = UUID(uuidString: "00000000-0000-0000-0000-000000000002")!

    func testUnknownSubscriptionCannotStopAdvertisingOrBecomeTarget() {
        var policy = HIDHostPolicy()
        policy.reconcile(ready: [other])
        XCTAssertNil(policy.target)
        XCTAssertTrue(policy.needsAdvertising)
        policy.allowed.insert(pc)
        policy.reconcile(ready: [pc, other])
        XCTAssertEqual(policy.target, pc)
        XCTAssertFalse(policy.needsAdvertising)
        policy.select(other)
        XCTAssertEqual(policy.target, pc)
    }

    func testBootKeyboardCopyOnlyForBootProtocolHosts() {
        XCTAssertFalse(HIDHostPolicy.needsBootKeyboard([.mouse, .keyboard, .bootMouse, .bootKeyboard]))
        XCTAssertFalse(HIDHostPolicy.needsBootKeyboard([.mouse, .keyboard]))
        XCTAssertTrue(HIDHostPolicy.needsBootKeyboard([.bootMouse, .bootKeyboard]))
    }

    func testReadyRequiresBothKeyboardAndMouseInTheSameProtocol() {
        XCTAssertFalse(HIDHostPolicy.inputReady([]))
        XCTAssertFalse(HIDHostPolicy.inputReady([.mouse]))
        XCTAssertFalse(HIDHostPolicy.inputReady([.keyboard]))
        XCTAssertFalse(HIDHostPolicy.inputReady([.bootKeyboard, .mouse]))
        XCTAssertTrue(HIDHostPolicy.inputReady([.mouse, .keyboard]))
        XCTAssertTrue(HIDHostPolicy.inputReady([.bootMouse, .bootKeyboard]))
    }

    func testNewAllowedDeviceDoesNotStealCurrentTarget() {
        var policy = HIDHostPolicy(allowed: [pc, other])
        policy.reconcile(ready: [other])
        policy.reconcile(ready: [other, pc])
        XCTAssertEqual(policy.target, other)
        policy.select(pc)
        XCTAssertEqual(policy.target, pc)
        policy.reconcile(ready: [other, pc])
        XCTAssertEqual(policy.target, pc)
    }

    func testDisconnectAndRevocationResumeAdvertisingUnlessAnotherAllowedHostIsReady() {
        var policy = HIDHostPolicy(allowed: [pc, other])
        policy.reconcile(ready: [pc])
        policy.reconcile(ready: [])
        XCTAssertTrue(policy.needsAdvertising)
        XCTAssertEqual(policy.allowed, [pc, other])
        policy.reconcile(ready: [pc, other])
        XCTAssertEqual(policy.target, pc)
        policy.allowed.remove(pc)
        policy.reconcile(ready: policy.ready)
        XCTAssertEqual(policy.target, other)
        policy.allowed.remove(other)
        policy.reconcile(ready: policy.ready)
        XCTAssertNil(policy.target)
        XCTAssertTrue(policy.needsAdvertising)
    }
}
