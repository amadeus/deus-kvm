import Foundation

/// Captures metadata only. File contents are opened on the clipboard queue, on demand.
struct ClipboardFile: Sendable {
    static let maximumBytes = 10 * 1024 * 1024
    let url: URL
    let size: Int
    let modified: Date
    let identity: UInt64

    init?(_ url: URL) {
        guard url.isFileURL,
              let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
              attributes[.type] as? FileAttributeType == .typeRegular,
              let size = attributes[.size] as? Int, size <= Self.maximumBytes,
              let modified = attributes[.modificationDate] as? Date,
              let identity = attributes[.systemFileNumber] as? UInt64 else { return nil }
        self.url = url; self.size = size; self.modified = modified; self.identity = identity
    }

    func read(offset: UInt32, count: Int = ClipboardTransfer.blockBytes) -> Data? {
        guard offset <= size, count > 0, count <= FileNetworkCrypto.maximumBlock, let current = ClipboardFile(url), current.size == size,
              current.modified == modified, current.identity == identity,
              let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        do {
            try handle.seek(toOffset: UInt64(offset))
            let count = min(count, size - Int(offset))
            let bytes = try handle.read(upToCount: count) ?? Data()
            guard bytes.count == count, let after = ClipboardFile(url),
                  after.size == size, after.modified == modified, after.identity == identity else { return nil }
            return bytes
        } catch { return nil }
    }
}

struct ClipboardFileOffer: Codable {
    let epoch: UInt32
    let clipboardSequence: UInt32
    let sequence: UInt32
    let name: String
    let size: Int
    var network: FileNetworkOffer?
}
