import Foundation

@main
struct FileNetworkSmoke {
    static func main() throws {
        let folder = URL(fileURLWithPath: CommandLine.arguments[1])
        let source = folder.appendingPathComponent("source.bin")
        let bytes = Data((0 ..< 2 * 1024 * 1024 + 37).map { UInt8($0 % 251) })
        try bytes.write(to: source)
        let captured = ClipboardFile(source)!
        let queue = DispatchQueue(label: "file-network-smoke")
        // State below is only used on this serial queue.
        let server = FileNetworkServer(queue: queue) { epoch, sequence, offset, count in
            guard epoch == 1, sequence == 2 else { return nil }
            try? Data([1]).write(to: folder.appendingPathComponent("read-started"))
            return captured.read(offset: offset, count: count)
        }
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now(), repeating: .milliseconds(50))
        timer.setEventHandler {
            if FileManager.default.fileExists(atPath: folder.appendingPathComponent("revoke").path) {
                server.revoke()
                try? Data().write(to: folder.appendingPathComponent("revoked"))
                timer.cancel()
            }
        }
        queue.async {
            server.configure(true)
            queue.asyncAfter(deadline: .now() + 1.5) {
                guard let network = server.offer(epoch: 1, sequence: 2, file: true) else {
                    fputs("No local IPv4 listener available\n", stderr); exit(1)
                }
                let offer = ClipboardFileOffer(
                    epoch: 1,
                    clipboardSequence: 0,
                    sequence: 2,
                    name: "source.bin",
                    size: captured.size,
                    network: network
                )
                do { try JSONEncoder().encode(offer).write(to: folder.appendingPathComponent("offer.json"), options: .atomic) }
                catch { fputs("Cannot publish test offer\n", stderr); exit(1) }
                timer.resume()
            }
        }
        let receiver = FileNetworkReceiver(
            offer: ClipboardFileOffer(epoch: 3, clipboardSequence: 0, sequence: 4, name: "reverse.bin", size: bytes.count),
            destination: folder.appendingPathComponent("reverse.bin"), canAccess: { true },
            ready: { offer in try! JSONEncoder().encode(offer).write(
                to: folder.appendingPathComponent("reverse-request.json"),
                options: .atomic
            ) },
            progress: { _ in }, completion: { error in
                try! Data((error ?? "OK").utf8).write(to: folder.appendingPathComponent("reverse-result"), options: .atomic)
            }
        )
        receiver.start()
        withExtendedLifetime(receiver) { dispatchMain() }
    }
}
