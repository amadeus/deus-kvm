import XCTest

@MainActor
final class DeviceNameStoreTests: XCTestCase {
    func testHandshakeNameIsOptionalAndPersistsForOnlyItsDevice() throws {
        let suite = "DeusKVMTests.Names.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let store = DeviceNameStore(defaults: defaults)
        let pc = UUID(), other = UUID()
        let old = Data(#"{"v":1,"role":"pc","name":"DeusKVM Companion"}"#.utf8)
        XCTAssertNil(try JSONDecoder().decode(CompanionHello.self, from: old).computerName)
        let current = Data(#"{"v":1,"role":"pc","name":"DeusKVM Companion","computerName":"Gaming PC"}"#.utf8)
        let hello = try JSONDecoder().decode(CompanionHello.self, from: current)
        try store.rememberName(XCTUnwrap(hello.computerName), for: pc)
        XCTAssertEqual(store.name(for: pc), "Gaming PC")
        XCTAssertNil(store.name(for: other))
        XCTAssertEqual(DeviceNameStore(defaults: defaults).name(for: pc), "Gaming PC")
        store.clear()
        XCTAssertNil(DeviceNameStore(defaults: defaults).name(for: pc))
    }

    func testDiscoveryDoesNotReplaceAnExistingAlias() throws {
        let suite = "DeusKVMTests.Names.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let store = DeviceNameStore(defaults: defaults)
        let pc = UUID()
        store.setName("Gaming PC", for: pc)
        store.rememberName("MAINGEAR", for: pc)
        XCTAssertEqual(store.name(for: pc), "Gaming PC")
        XCTAssertEqual(DeviceNameStore(defaults: defaults).name(for: pc), "Gaming PC")
    }

    func testInvalidNamesDoNotFillTheCache() throws {
        let suite = "DeusKVMTests.Names.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let store = DeviceNameStore(defaults: defaults)
        let pc = UUID()
        for name in ["", "  \n", "PC\nname", String(repeating: "x", count: 257)] {
            store.rememberName(name, for: pc)
            XCTAssertNil(store.name(for: pc))
        }
        store.rememberName("  Gaming PC  ", for: pc)
        XCTAssertEqual(store.name(for: pc), "Gaming PC")
    }
}
