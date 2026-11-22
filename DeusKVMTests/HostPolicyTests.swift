import XCTest

final class HostPolicyTests: XCTestCase {
    private let first = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!
    private let second = UUID(uuidString: "00000000-0000-0000-0000-000000000002")!

    func testOnlyAReadyAllowedPCBecomesTheTarget() {
        var policy = HostPolicy()
        policy.reconcile(ready: [first])
        XCTAssertNil(policy.target, "connected but not allowed")
        policy.allowed = [first]
        policy.reconcile(ready: [first])
        XCTAssertEqual(policy.target, first)
        policy.reconcile(ready: [])
        XCTAssertNil(policy.target, "a disconnected PC is no target")
    }

    func testANewAllowedPCDoesNotStealTheCurrentTargetButCanBeSelected() {
        var policy = HostPolicy(allowed: [second])
        policy.reconcile(ready: [second])
        policy.allowed.insert(first)
        policy.reconcile(ready: [first, second])
        XCTAssertEqual(policy.target, second)
        policy.select(first)
        XCTAssertEqual(policy.target, first)
        policy.allowed.remove(first)
        policy.reconcile(ready: [first, second])
        XCTAssertEqual(policy.target, second, "revoking permission moves to the other allowed PC")
    }
}
