import XCTest

final class FinderPasteGestureTests: XCTestCase {
    func testOnlyCurrentLocalFinderCommandVIsHandled() {
        XCTAssertTrue(FinderPasteGesture.matches(
            key: 9,
            flags: .maskCommand,
            local: true,
            revisions: (2, 2),
            bundle: "com.apple.finder"
        ))
        XCTAssertFalse(FinderPasteGesture.matches(
            key: 9,
            flags: .maskCommand,
            local: false,
            revisions: (2, 2),
            bundle: "com.apple.finder"
        ))
        XCTAssertFalse(FinderPasteGesture.matches(
            key: 9,
            flags: .maskCommand,
            local: true,
            revisions: (2, 3),
            bundle: "com.apple.finder"
        ))
        XCTAssertFalse(FinderPasteGesture.matches(
            key: 9,
            flags: .maskCommand,
            local: true,
            revisions: (2, 2),
            bundle: "com.apple.TextEdit"
        ))
        XCTAssertFalse(FinderPasteGesture.matches(
            key: 9,
            flags: [.maskCommand, .maskAlternate],
            local: true,
            revisions: (2, 2),
            bundle: "com.apple.finder"
        ))
        XCTAssertFalse(FinderPasteGesture.matches(
            key: 8,
            flags: .maskCommand,
            local: true,
            revisions: (2, 2),
            bundle: "com.apple.finder"
        ))
    }

    func testRemoteNamesAndTwoGigabyteMetadataAreValidated() throws {
        let json = Data(#"{"epoch":1,"clipboardSequence":2,"sequence":3,"name":"file.bin","size":2000000000}"#.utf8)
        let file = try JSONDecoder().decode(ClipboardFileOffer.self, from: json)
        XCTAssertTrue(file.valid)
        for (name, size) in [("../escape", 3), ("a/b", 3), ("x\\y", 3), ("..", 3), ("file", -1), ("file", 2_000_000_001)] {
            XCTAssertFalse(ClipboardFileOffer(epoch: 1, clipboardSequence: 2, sequence: 3, name: name, size: size).valid)
        }
    }
}
