import AppKit
import XCTest

final class ClipboardHandoffTests: XCTestCase {
    func testLocalCopyIsNotReadUntilExplicitHandoff() {
        let board = NSPasteboard(name: .init("DeusKVMTests.\(UUID().uuidString)"))
        defer { board.releaseGlobally() }
        board.setString("initial", forType: .string)
        let primed = expectation(description: "initial snapshot")
        let changed = expectation(description: "handoff snapshot")
        let idle = expectation(description: "no clipboard observation while idle")
        idle.isInverted = true
        let yielded = expectation(description: "handoff follows snapshot")
        let log = Snapshots()
        let clipboard = ClipboardPasteboard(board: board, snapshot: { snapshot in
            if log.append(snapshot.text) == 1 { primed.fulfill() } else if log.handoffRequested {
                changed.fulfill()
            } else { idle.fulfill() }
        }, yielded: { _, _ in
            XCTAssertEqual(log.texts, ["initial", "latest menu copy"])
            yielded.fulfill()
        })
        clipboard.configure(epoch: 1, enabled: true)
        wait(for: [primed], timeout: 2)
        board.clearContents(); board.setString("latest menu copy", forType: .string)
        // A full interval beyond the removed 200 ms timer must not discover this copy.
        wait(for: [idle], timeout: 0.4)
        log.beginHandoff()
        clipboard.yield()
        wait(for: [changed, yielded], timeout: 2)
        clipboard.configure(epoch: 1, enabled: false)
    }

    func testIncomingWriteCannotReplaceANewerUnobservedLocalCopy() {
        let board = NSPasteboard(name: .init("DeusKVMTests.\(UUID().uuidString)"))
        defer { board.releaseGlobally() }
        board.setString("initial", forType: .string)
        let revision = board.changeCount
        let primed = expectation(description: "initial snapshot")
        let local = expectation(description: "new local copy detected at incoming write")
        let log = Snapshots()
        let clipboard = ClipboardPasteboard(board: board, snapshot: { snapshot in
            if log.append(snapshot.text) == 1 { primed.fulfill() } else { local.fulfill() }
        }, yielded: { _, _ in XCTFail("Incoming writes must not announce local clipboard") })
        clipboard.configure(epoch: 1, enabled: true)
        wait(for: [primed], timeout: 2)
        board.clearContents(); board.setString("new local copy", forType: .string)
        clipboard.write(Data("late Windows text".utf8), epoch: 1, revision: revision)
        wait(for: [local], timeout: 2)
        XCTAssertEqual(board.string(forType: .string), "new local copy")
        clipboard.configure(epoch: 1, enabled: false)
    }

    func testIncomingTextAppliesDirectlyWithoutPeriodicPolling() {
        let board = NSPasteboard(name: .init("DeusKVMTests.\(UUID().uuidString)"))
        defer { board.releaseGlobally() }
        board.setString("initial", forType: .string)
        let revision = board.changeCount
        let primed = expectation(description: "initial snapshot")
        let clipboard = ClipboardPasteboard(board: board, snapshot: { _ in primed.fulfill() }, yielded: { _, _ in
            XCTFail("Incoming writes must not announce local clipboard")
        })
        clipboard.configure(epoch: 1, enabled: true)
        wait(for: [primed], timeout: 2)
        clipboard.write(Data("Windows text".utf8), epoch: 1, revision: revision)
        let applied = expectation(for: NSPredicate { _, _ in board.string(forType: .string) == "Windows text" }, evaluatedWith: nil)
        wait(for: [applied], timeout: 2)
        XCTAssertNotNil(board.data(forType: ClipboardPasteboard.ownType))
        clipboard.configure(epoch: 1, enabled: false)
    }

    func testFileCopiedAfterPrimingIsPreparedOnHandoff() throws {
        let board = NSPasteboard(name: .init("DeusKVMTests.\(UUID().uuidString)"))
        defer { board.releaseGlobally() }
        board.setString("initial", forType: .string)
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try Data("on-demand file".utf8).write(to: url)
        defer { try? FileManager.default.removeItem(at: url) }
        let primed = expectation(description: "initial snapshot")
        let file = expectation(description: "file metadata captured on handoff")
        let yielded = expectation(description: "handoff ready")
        let log = Snapshots()
        let clipboard = ClipboardPasteboard(board: board, snapshot: { snapshot in
            if log.append(snapshot.text) == 1 { primed.fulfill() } else {
                XCTAssertEqual(snapshot.file?.url, url)
                XCTAssertEqual(snapshot.file?.size, 14)
                XCTAssertNil(snapshot.text)
                file.fulfill()
            }
        }, yielded: { _, _ in yielded.fulfill() })
        clipboard.configure(epoch: 1, enabled: true)
        wait(for: [primed], timeout: 2)
        board.clearContents(); board.writeObjects([url as NSURL])
        clipboard.yield()
        wait(for: [file, yielded], timeout: 3)
        clipboard.configure(epoch: 1, enabled: false)
    }

    private final class Snapshots: @unchecked Sendable {
        private let lock = NSLock()
        private var values: [String?] = []
        private var requested = false
        func beginHandoff() {
            lock.withLock { requested = true }
        }

        var handoffRequested: Bool {
            lock.withLock { requested }
        }

        func append(_ data: Data?) -> Int {
            lock.withLock {
                values.append(data.flatMap(ClipboardTransfer.decode))
                return values.count
            }
        }

        var texts: [String?] {
            lock.withLock { values }
        }
    }
}
