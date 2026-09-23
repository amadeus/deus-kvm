import XCTest

final class ClipboardFileTests: XCTestCase {
    func testReadsOnlyRequestedBlocksAndRejectsChangedOrRemovedSource() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: url) }
        let bytes = Data((0 ..< 2500).map { UInt8($0 % 251) })
        try bytes.write(to: url)
        let file = try XCTUnwrap(ClipboardFile(url))
        XCTAssertEqual(file.size, bytes.count)
        XCTAssertEqual(file.read(offset: 0), bytes.prefix(1024))
        XCTAssertEqual(file.read(offset: 2048), bytes.suffix(452))
        XCTAssertEqual(file.read(offset: 2500), Data())
        XCTAssertNil(file.read(offset: 2501))
        try Data([1]).write(to: url)
        XCTAssertNil(file.read(offset: 0))
        try FileManager.default.removeItem(at: url)
        XCTAssertNil(file.read(offset: 0))
    }

    func testRejectsDirectoriesSymlinksAndOversizedFilesButAcceptsEmptyFiles() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        XCTAssertNil(ClipboardFile(folder))
        let url = folder.appendingPathComponent("file")
        try Data().write(to: url)
        XCTAssertEqual(ClipboardFile(url)?.read(offset: 0), Data())
        let link = folder.appendingPathComponent("link")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: url)
        XCTAssertNil(ClipboardFile(link))
        let handle = try FileHandle(forWritingTo: url)
        try handle.truncate(atOffset: UInt64(ClipboardFile.maximumBytes))
        let large = try XCTUnwrap(ClipboardFile(url))
        XCTAssertEqual(large.size, 2_000_000_000)
        XCTAssertEqual(large.read(offset: 1_999_999_997, count: 256 * 1024), Data(repeating: 0, count: 3))
        try handle.truncate(atOffset: UInt64(ClipboardFile.maximumBytes + 1))
        try handle.close()
        XCTAssertNil(ClipboardFile(url))
    }
}
