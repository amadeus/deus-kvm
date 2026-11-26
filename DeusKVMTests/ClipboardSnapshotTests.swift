import AppKit
import XCTest

final class ClipboardSnapshotTests: XCTestCase {
    func testFreshRequestCannotReexportAnImportedWindowsClipboard() {
        let board = NSPasteboard(name: .init("DeusKVMTests.\(UUID().uuidString)"))
        defer { board.releaseGlobally() }
        let item = NSPasteboardItem()
        item.setString("imported from Windows", forType: .string)
        item.setData(Data(), forType: ClipboardPasteboard.ownType)
        board.writeObjects([item])
        let answered = expectation(description: "explicit unavailable snapshot")
        let clipboard = ClipboardPasteboard(board: board, snapshot: { _ in }, yielded: { _, _ in })
        clipboard.configure(epoch: 3, enabled: true)
        clipboard.capture(epoch: 3) { snapshot in
            XCTAssertNotNil(snapshot, "A private clipboard should still yield explicit metadata")
            let reply = ClipboardSnapshot(epoch: 3, generation: 9, snapshot: snapshot)
            XCTAssertEqual(reply.kind, .unavailable)
            XCTAssertNil(reply.text)
            answered.fulfill()
        }
        wait(for: [answered], timeout: 2)
        clipboard.configure(epoch: 3, enabled: false)
    }

    func testSelectedSubscriptionObservesFreshCopyWithoutAnotherEdgeCrossing() {
        let board = NSPasteboard(name: .init("DeusKVMTests.\(UUID().uuidString)"))
        defer { board.releaseGlobally() }
        board.setString("initial", forType: .string)
        let primed = expectation(description: "initial copy")
        let changed = expectation(description: "copy while still selected")
        let clipboard = ClipboardPasteboard(board: board, snapshot: { snapshot in
            if snapshot.text == Data("initial".utf8) { primed.fulfill() }
            if snapshot.text == Data("new copy".utf8) { changed.fulfill() }
        }, yielded: { _, _ in })
        clipboard.configure(epoch: 3, enabled: true)
        wait(for: [primed], timeout: 2)
        clipboard.watchChanges(true)
        board.clearContents(); board.setString("new copy", forType: .string)
        wait(for: [changed], timeout: 2)
        clipboard.configure(epoch: 3, enabled: false)
    }

    func testPrivateAndMissingContentAreExplicitUnavailableAnswers() throws {
        let unavailable = ClipboardSnapshot(epoch: 3, generation: 9, snapshot: nil)
        XCTAssertEqual(unavailable.kind, .unavailable)
        let privateSnapshot = ClipboardPasteboard.Snapshot(
            epoch: 3, generation: 4, revision: 8, text: Data("secret".utf8), file: nil, network: nil, shareable: false
        )
        let answer = ClipboardSnapshot(epoch: 3, generation: 9, snapshot: privateSnapshot)
        XCTAssertEqual(answer.kind, .unavailable)
        XCTAssertNil(answer.text)
        XCTAssertNil(answer.file)
        XCTAssertEqual(try JSONDecoder().decode(ClipboardSnapshot.self, from: answer.encoded()).generation, 9)
    }

    func testEmptyAndTextSnapshotsRetainRevisionAndOwnership() throws {
        let empty = ClipboardPasteboard.Snapshot(
            epoch: 3, generation: 4, revision: 8, text: nil, file: nil, network: nil, shareable: true
        )
        XCTAssertEqual(ClipboardSnapshot(epoch: 3, generation: 9, snapshot: empty).kind, .empty)
        let bytes = Data(repeating: 65, count: ClipboardTransfer.maximumBytes)
        let source = ClipboardPasteboard.Snapshot(
            epoch: 3, generation: 4, revision: 8, text: bytes, file: nil, network: nil, shareable: true
        )
        let answer = ClipboardSnapshot(epoch: 3, generation: 9, snapshot: source)
        let encoded = try answer.encoded()
        XCTAssertLessThanOrEqual(encoded.count, ClipboardSnapshot.maximumBytes)
        let decoded = try JSONDecoder().decode(ClipboardSnapshot.self, from: encoded)
        XCTAssertEqual(decoded.text, bytes)
        XCTAssertEqual(decoded.revision, 8)
        XCTAssertEqual(decoded.generation, 9)
    }

    func testSnapshotRequestRequiresExplicitMarkerAndGeneration() throws {
        XCTAssertEqual(try ClipboardSnapshotRequest.decode(Data(#"{"epoch":3,"generation":9,"request":true}"#.utf8)).generation, 9)
        for invalid in [#"{"epoch":3,"generation":0,"request":true}"#, #"{"epoch":3,"generation":9}"#] {
            XCTAssertThrowsError(try ClipboardSnapshotRequest.decode(Data(invalid.utf8)))
        }
    }
}
