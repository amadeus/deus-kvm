import IOKit
import XCTest

final class KeyboardMonitoringStateTests: XCTestCase {
    func testSeizedPhysicalKeyboardDoesNotDiscardVirtualKeyboard() {
        var state = KeyboardMonitoringState()
        state.opened(1, result: kIOReturnExclusiveAccess)
        XCTAssertEqual(state.status(permissionGranted: true), .unavailable(kIOReturnExclusiveAccess))
        state.opened(2, result: kIOReturnSuccess)
        XCTAssertEqual(state.status(permissionGranted: true), .ready)
        XCTAssertTrue(state.needsRetry)

        // Arrival order must not turn a usable virtual stream into a global failure.
        state.opened(3, result: kIOReturnExclusiveAccess)
        XCTAssertEqual(state.status(permissionGranted: true), .ready)
    }

    func testNoRemapperUsesPhysicalKeyboardWithoutRetries() {
        var state = KeyboardMonitoringState()
        state.opened(1, result: kIOReturnSuccess)
        state.opened(2, result: kIOReturnSuccess)
        XCTAssertEqual(state.status(permissionGranted: true), .ready)
        XCTAssertFalse(state.needsRetry)
    }

    func testRemapperExitRecoversPhysicalKeyboardAndReconnectRestoresVirtualKeyboard() {
        var state = KeyboardMonitoringState()
        state.opened(1, result: kIOReturnExclusiveAccess)
        state.opened(2, result: kIOReturnSuccess)
        state.remove(2)
        XCTAssertEqual(state.status(permissionGranted: true), .unavailable(kIOReturnExclusiveAccess))
        state.opened(1, result: kIOReturnSuccess)
        XCTAssertEqual(state.status(permissionGranted: true), .ready)
        XCTAssertFalse(state.needsRetry)
        state.remove(1)
        XCTAssertEqual(state.status(permissionGranted: true), .waitingForDevice)
        state.opened(3, result: kIOReturnSuccess)
        XCTAssertEqual(state.status(permissionGranted: true), .ready)
    }

    func testPermissionIsSeparateFromDeviceErrors() {
        var state = KeyboardMonitoringState()
        XCTAssertEqual(state.status(permissionGranted: false), .permissionRequired)
        state.opened(1, result: kIOReturnNotPermitted)
        XCTAssertEqual(state.status(permissionGranted: false), .permissionRequired)
        XCTAssertEqual(state.status(permissionGranted: true), .unavailable(kIOReturnNotPermitted))
        state.opened(1, result: kIOReturnSuccess)
        XCTAssertEqual(state.status(permissionGranted: true), .ready)
    }
}
