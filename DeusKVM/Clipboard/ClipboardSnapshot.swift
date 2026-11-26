import Foundation

/// The complete clipboard answer for one ownership generation. File/image bytes stay on
/// the authenticated file server; text is bounded and travels inline with its manifest.
struct ClipboardSnapshot: Codable, Sendable {
    static let maximumBytes = 96 * 1024
    enum Kind: String, Codable, Sendable { case empty, unavailable, text, file, image }

    let epoch: UInt32
    let generation: UInt64
    let revision: UInt32
    let kind: Kind
    var text: Data?
    var file: ClipboardFileOffer?

    init(epoch: UInt32, generation: UInt64, snapshot: ClipboardPasteboard.Snapshot?) {
        self.epoch = epoch; self.generation = generation
        revision = snapshot.map { UInt32(truncatingIfNeeded: $0.revision) } ?? 0
        guard let snapshot, snapshot.shareable else { kind = .unavailable; return }
        if let source = snapshot.file {
            guard let network = snapshot.network else { kind = .unavailable; return }
            let offer = ClipboardFileOffer(
                epoch: epoch, clipboardSequence: revision, sequence: revision,
                name: source.url.lastPathComponent, size: source.size, network: network, contentType: source.contentType
            )
            guard offer.valid else { kind = .unavailable; return }
            kind = offer.isImage ? .image : .file; file = offer
        } else if let bytes = snapshot.text {
            kind = .text; text = bytes
        } else { kind = .empty }
    }

    func encoded() throws -> Data {
        let data = try JSONEncoder().encode(self)
        guard data.count <= Self.maximumBytes else { throw CompanionProtocol.Failure.malformed }
        return data
    }
}

struct ClipboardSnapshotRequest: Codable {
    let epoch: UInt32
    let generation: UInt64
    let request: Bool

    static func decode(_ data: Data) throws -> Self {
        guard data.count <= 256 else { throw CompanionProtocol.Failure.malformed }
        let value = try JSONDecoder().decode(Self.self, from: data)
        guard value.request, value.generation > 0 else { throw CompanionProtocol.Failure.malformed }
        return value
    }
}
