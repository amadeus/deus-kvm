import Foundation

@main
struct FileNetworkSmoke {
    static func main() throws {
        let folder = URL(fileURLWithPath: CommandLine.arguments[1])
        let source = folder.appendingPathComponent("source.bin")
        let bytes = Data((0 ..< 2 * 1024 * 1024 + 37).map { UInt8($0 % 251) })
        try bytes.write(to: source)
        let captured = ClipboardFile(source)!
        let png = Data(base64Encoded:
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1sAAAAASUVORK5CYII="
        )! + bytes
        let imageSource = ClipboardFile(png: png)!
        let queue = DispatchQueue(label: "file-network-smoke")
        let imageServer = FileNetworkServer(queue: queue) { epoch, sequence, offset, count in
            guard epoch == 5, sequence == 6 else { return nil }
            return imageSource.read(offset: offset, count: count)
        }
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
            imageServer.configure(true)
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
                let imageOffer = ClipboardFileOffer(
                    epoch: 5, clipboardSequence: 0, sequence: 6, name: "Screenshot.png", size: png.count,
                    network: imageServer.offer(epoch: 5, sequence: 6, file: true), contentType: "image/png"
                )
                try! JSONEncoder().encode(imageOffer).write(to: folder.appendingPathComponent("image-offer.json"), options: .atomic)
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
        let imageReceiver = FileNetworkReceiver(
            offer: ClipboardFileOffer(
                epoch: 7, clipboardSequence: 0, sequence: 8, name: "Screenshot.png", size: png.count, contentType: "image/png"
            ),
            destination: nil, canAccess: { true },
            ready: { offer in try! JSONEncoder().encode(offer).write(
                to: folder.appendingPathComponent("image-request.json"), options: .atomic
            ) },
            progress: { _ in }, completion: { error in
                if let error { try! Data(error.utf8).write(to: folder.appendingPathComponent("image-error")) }
            }, received: { data in
                try! Data((data == png ? "OK" : "MISMATCH").utf8).write(
                    to: folder.appendingPathComponent("image-result"), options: .atomic
                )
            }
        )
        receiver.start()
        imageReceiver.start()
        withExtendedLifetime((receiver, imageReceiver, imageServer)) { dispatchMain() }
    }
}
