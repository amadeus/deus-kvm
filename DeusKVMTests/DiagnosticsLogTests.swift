import XCTest

final class DiagnosticsLogTests: XCTestCase {
    private final class Clock: @unchecked Sendable {
        private let lock = NSLock()
        private var value = Date(timeIntervalSinceReferenceDate: 600)
        var now: Date {
            lock.withLock { value }
        }

        func advance(_ seconds: TimeInterval) {
            lock.withLock { value += seconds }
        }
    }

    private var directory: URL!

    override func setUpWithError() throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("DeusKVMDiagnostics-\(UUID().uuidString)")
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: directory)
    }

    private func contents(_ log: DiagnosticsLog) -> [String] {
        ((try? String(contentsOf: log.url, encoding: .utf8)) ?? "").split(separator: "\n").map(String.init)
    }

    func testLinesStayInMemoryUntilFlushedAndThenAppend() {
        let log = DiagnosticsLog(directory: directory)
        log.write("first")
        log.write("second")
        XCTAssertFalse(FileManager.default.fileExists(atPath: log.url.path), "no disk write per line")
        log.flushAndWait()
        log.write("third")
        log.flushAndWait()
        let lines = contents(log)
        XCTAssertEqual(lines.map { $0.split(separator: " ", maxSplits: 2).last.map(String.init) }, ["first", "second", "third"])
        XCTAssertTrue(lines[0].range(of: #"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} "#, options: .regularExpression) != nil)
        let permissions = (try? FileManager.default.attributesOfItem(atPath: log.url.path)[.posixPermissions]) as? Int
        XCTAssertEqual(permissions, 0o600)
    }

    func testPerMinuteBudgetSuppressesFloodsButNotReports() {
        let clock = Clock()
        let log = DiagnosticsLog(directory: directory, limits: .init(linesPerMinute: 3), now: { clock.now })
        for index in 1 ... 5 {
            log.write("flap \(index)")
        }
        log.writeReport(["report a", "report b"])
        clock.advance(60)
        log.write("next minute")
        log.flushAndWait()
        let lines = contents(log).map { String($0.dropFirst(24)) }
        XCTAssertEqual(lines, ["flap 1", "flap 2", "flap 3", "report a", "report b", "diagnostics: suppressed 2 lines", "next minute"])
    }

    func testRotationKeepsOnePreviousFileWithinTheCap() {
        let log = DiagnosticsLog(directory: directory, limits: .init(maximumBytes: 200))
        for index in 0 ..< 3 {
            log.write(String(repeating: "\(index)", count: 80))
            log.flushAndWait()
        }
        let previous = log.url.appendingPathExtension("previous")
        XCTAssertTrue(FileManager.default.fileExists(atPath: previous.path))
        let size = { (url: URL) in (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int) ?? 0 }
        XCTAssertLessThanOrEqual(size(log.url), 200)
        XCTAssertLessThanOrEqual(size(previous), 200)
        XCTAssertTrue(contents(log).last?.hasSuffix(String(repeating: "2", count: 80)) == true)
    }

    func testBufferedLinesFlushAfterTheIntervalOrWhenTheBufferFills() {
        let timed = DiagnosticsLog(directory: directory, limits: .init(flushInterval: 0.05))
        timed.write("timed")
        let written = expectation(description: "timer flush")
        DispatchQueue.global().asyncAfter(deadline: .now() + 0.5) {
            if FileManager.default.fileExists(atPath: timed.url.path) { written.fulfill() }
        }
        wait(for: [written], timeout: 2)

        let other = directory.appendingPathComponent("full")
        let full = DiagnosticsLog(directory: other, limits: .init(flushBytes: 100))
        full.write(String(repeating: "x", count: 120))
        full.write("sync") // runs after the first write's size-triggered flush
        let synced = expectation(description: "size flush")
        DispatchQueue.global().asyncAfter(deadline: .now() + 0.2) {
            if FileManager.default.fileExists(atPath: full.url.path) { synced.fulfill() }
        }
        wait(for: [synced], timeout: 2)
    }
}
