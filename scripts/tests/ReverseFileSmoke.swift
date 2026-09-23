import Foundation

@main
struct ReverseFileSmoke {
    static func main() throws {
        let folder = URL(fileURLWithPath: CommandLine.arguments[1])
        let scenario = CommandLine.arguments[2]
        if scenario == "forward-large" { try forward(folder); return }
        let size = scenario == "large" ? 2_000_000_000 : scenario == "empty" ? 0 : 2 * 1024 * 1024 + 37
        let destination = folder.appendingPathComponent("received.bin")
        if scenario == "collision" { try Data("existing".utf8).write(to: destination) }
        let receiver = FileNetworkReceiver(
            offer: ClipboardFileOffer(epoch: 7, clipboardSequence: 8, sequence: 9, name: "received.bin", size: size),
            destination: destination,
            canAccess: { !FileManager.default.fileExists(atPath: folder.appendingPathComponent("cancel").path) },
            ready: { offer in try! JSONEncoder().encode(offer).write(to: folder.appendingPathComponent("request.json"), options: .atomic) },
            progress: { bytes in
                if scenario == "cancel", bytes > 0 { try! Data().write(to: folder.appendingPathComponent("cancel")) }
            }, completion: { error in
                try! Data((error ?? "OK").utf8).write(to: folder.appendingPathComponent("result"), options: .atomic)
            })
        receiver.start()
        withExtendedLifetime(receiver) { dispatchMain() }
    }
    static func forward(_ folder: URL) throws {
        let url = folder.appendingPathComponent("source.bin")
        try Data().write(to: url)
        let file = try FileHandle(forWritingTo: url)
        try file.truncate(atOffset: 2_000_000_000); try file.seek(toOffset: 1_999_999_997)
        try file.write(contentsOf: Data([7, 8, 9])); try file.close()
        let captured = ClipboardFile(url)!
        let queue = DispatchQueue(label: "large-forward")
        let server = FileNetworkServer(queue: queue) { _, _, offset, count in captured.read(offset: offset, count: count) }
        queue.async {
            server.configure(true)
            queue.asyncAfter(deadline: .now() + 1.5) {
                let network = server.offer(epoch: 7, sequence: 9, file: true)!
                let offer = ClipboardFileOffer(epoch: 7, clipboardSequence: 8, sequence: 9, name: "source.bin", size: captured.size, network: network)
                try! JSONEncoder().encode(offer).write(to: folder.appendingPathComponent("request.json"), options: .atomic)
            }
        }
        withExtendedLifetime(server) { dispatchMain() }
    }

}
