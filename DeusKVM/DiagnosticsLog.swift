import Foundation

/// A durable record for intermittent problems that outlive the unified log's retention.
/// Counts, timings and state names only: never input contents, pointer coordinates or
/// clipboard data.
///
/// Disk use is bounded by construction: lines are buffered in memory and written in
/// batches (every few minutes, or when the buffer fills, or on an explicit flush), a
/// per-minute line budget stops a flapping state from flooding the file, and the file
/// rotates to a single `.previous` copy at `maximumBytes`.
final class DiagnosticsLog: @unchecked Sendable {
    struct Limits {
        var maximumBytes = 2_000_000
        var flushInterval: TimeInterval = 300
        var flushBytes = 32768
        var linesPerMinute = 120
    }

    static let shared = DiagnosticsLog(
        directory: FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/DeusKVM")
    )

    let url: URL
    private let limits: Limits
    private let now: @Sendable () -> Date
    private let queue = DispatchQueue(label: "io.github.amadeus.deuskvm.diagnostics", qos: .utility)
    private var buffer = Data()
    private var timer: DispatchSourceTimer?
    private var window: TimeInterval = -.infinity
    private var windowLines = 0
    private var suppressed = 0
    private let formatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd HH:mm:ss.SSS"
        return formatter
    }()

    init(directory: URL, limits: Limits = Limits(), now: @escaping @Sendable () -> Date = { Date() }) {
        url = directory.appendingPathComponent("diagnostics.log")
        self.limits = limits
        self.now = now
    }

    /// Buffers one line, subject to the per-minute budget.
    func write(_ line: String) {
        let date = now()
        queue.async { [self] in
            let minute = (date.timeIntervalSinceReferenceDate / 60).rounded(.down)
            if minute != window {
                if suppressed > 0 { append("diagnostics: suppressed \(suppressed) lines", at: date) }
                window = minute; windowLines = 0; suppressed = 0
            }
            guard windowLines < limits.linesPerMinute else { suppressed += 1; return }
            windowLines += 1
            append(line, at: date)
            scheduleFlush()
        }
    }

    /// Buffers a block that the user asked for (a problem report) and writes it at once.
    /// It bypasses the per-minute budget but not the size cap.
    func writeReport(_ lines: [String]) {
        let date = now()
        queue.async { [self] in
            for line in lines {
                append(line, at: date)
            }
            flushNow()
        }
    }

    func flush() {
        queue.async { [self] in flushNow() }
    }

    /// Waits for buffered lines to reach the file; for quitting and tests.
    func flushAndWait() {
        queue.sync { flushNow() }
    }

    private func append(_ line: String, at date: Date) {
        buffer.append(Data("\(formatter.string(from: date)) \(line)\n".utf8))
    }

    private func scheduleFlush() {
        if buffer.count >= limits.flushBytes { flushNow(); return }
        guard timer == nil else { return }
        let source = DispatchSource.makeTimerSource(queue: queue)
        source.schedule(deadline: .now() + limits.flushInterval, leeway: .seconds(10))
        source.setEventHandler { [weak self] in self?.flushNow() }
        timer = source
        source.resume()
    }

    private func flushNow() {
        timer?.cancel(); timer = nil
        guard !buffer.isEmpty else { return }
        let data = buffer
        buffer.removeAll(keepingCapacity: true)
        let manager = FileManager.default
        do {
            try manager.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            let size = (try? manager.attributesOfItem(atPath: url.path)[.size] as? Int) ?? 0
            if size > 0, size + data.count > limits.maximumBytes {
                let previous = url.appendingPathExtension("previous")
                try? manager.removeItem(at: previous)
                try manager.moveItem(at: url, to: previous)
            }
            if !manager.fileExists(atPath: url.path) {
                manager.createFile(atPath: url.path, contents: nil, attributes: [.posixPermissions: 0o600])
            }
            let handle = try FileHandle(forWritingTo: url)
            defer { try? handle.close() }
            try handle.seekToEnd()
            try handle.write(contentsOf: data)
        } catch {
            // Diagnostics must never affect input; a failed write drops this batch.
        }
    }
}
