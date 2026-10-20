import AppKit
import ImageIO
import UniformTypeIdentifiers

enum ClipboardImage {
    static let maximumPixels = 40_000_000

    /// Bound decoded dimensions before asking ImageIO to allocate the bitmap.
    static func decode(_ data: Data) -> CGImage? {
        guard !data.isEmpty, data.count <= ClipboardFile.maximumImageBytes,
              let source = CGImageSourceCreateWithData(data as CFData, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int,
              let height = properties[kCGImagePropertyPixelHeight] as? Int,
              width > 0, height > 0, width <= maximumPixels / height else { return nil }
        return CGImageSourceCreateImageAtIndex(source, 0, nil)
    }

    static func encode(_ image: CGImage, type: UTType) -> Data? {
        let bytes = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(bytes, type.identifier as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination), bytes.length <= ClipboardFile.maximumImageBytes else { return nil }
        return bytes as Data
    }

    static func capture(_ board: NSPasteboard) -> Data? {
        for type in [NSPasteboard.PasteboardType.png, .tiff] {
            if let bytes = board.data(forType: type), let image = decode(bytes),
               let png = encode(image, type: .png) { return png }
        }
        return nil
    }

    static func item(_ png: Data) -> NSPasteboardItem? {
        guard let image = decode(png) else { return nil }
        let item = NSPasteboardItem()
        item.setData(png, forType: .png)
        if let tiff = encode(image, type: .tiff) { item.setData(tiff, forType: .tiff) }
        return item
    }
}

final class ClipboardImageLease: @unchecked Sendable {
    private let lock = NSLock()
    private var active = true
    var valid: Bool {
        lock.withLock { active }
    }

    func cancel() {
        lock.withLock { active = false }
    }
}
