import Foundation

/// bluetoothd records the negotiated LE link parameters (interval, latency, supervision timeout)
/// that CoreBluetooth does not expose, but the unified log keeps them only for hours. A lag
/// report copies the recent lines so they survive until someone reads the report.
enum BluetoothLinkLog {
    /// Link topology and parameter negotiation for peripheral-role links (the PC is one),
    /// plus the Classic HID power-mode changes that share the radio (such as a trackpad).
    /// Lines that name hardware addresses are excluded.
    static let predicate = """
    process == "bluetoothd" AND (eventMessage CONTAINS "role: peripheral" \
    OR eventMessage CONTAINS "LE Connection Parameters Updated" \
    OR eventMessage CONTAINS "Remote side (central)" \
    OR eventMessage CONTAINS "Accepting following parameters" \
    OR eventMessage CONTAINS "Bandwidth: Sending" \
    OR eventMessage BEGINSWITH "Sniff interval changed")
    """

    static func capture(minutes: Int = 10, limit: Int = 600, completion: @escaping @Sendable ([String]) -> Void) {
        DispatchQueue.global(qos: .utility).async {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/log")
            process.arguments = ["show", "--last", "\(minutes)m", "--style", "compact", "--predicate", predicate]
            let output = Pipe()
            process.standardOutput = output
            process.standardError = FileHandle.nullDevice
            do { try process.run() } catch {
                completion(["bluetoothd: log show failed: \(error.localizedDescription)"])
                return
            }
            let deadline = DispatchWorkItem { if process.isRunning { process.terminate() } }
            DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 30, execute: deadline)
            let data = output.fileHandleForReading.readDataToEndOfFile()
            process.waitUntilExit()
            deadline.cancel()
            completion(lines(String(bytes: data, encoding: .utf8) ?? "", limit: limit))
        }
    }

    /// Drops the column header, keeps the newest `limit` lines and labels each one.
    static func lines(_ output: String, limit: Int) -> [String] {
        let body = output.split(separator: "\n").filter { !$0.hasPrefix("Timestamp ") && !$0.hasPrefix("Filtering the log data") }
        let kept = body.suffix(limit).map { "bluetoothd: \($0)" }
        return ["bluetoothd link lines: \(kept.count) of \(body.count)"] + kept
    }
}
