import AppKit
import UniformTypeIdentifiers
import XCTest

final class ClipboardImageTests: XCTestCase {
    private func png() throws -> Data {
        let bitmap = try XCTUnwrap(NSBitmapImageRep(
            bitmapDataPlanes: nil, pixelsWide: 2, pixelsHigh: 1, bitsPerSample: 8,
            samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 8, bitsPerPixel: 32
        ))
        let pixels: [UInt8] = [128, 0, 0, 128, 0, 0, 255, 255]
        for (index, byte) in pixels.enumerated() {
            bitmap.bitmapData?[index] = byte
        }
        return try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
    }

    func testTiffCaptureBecomesPngAndNativeItemOffersBothImageFormats() throws {
        let board = NSPasteboard(name: .init("DeusKVMTests.\(UUID().uuidString)"))
        defer { board.releaseGlobally() }
        let image = try XCTUnwrap(ClipboardImage.decode(png()))
        try board.setData(XCTUnwrap(ClipboardImage.encode(image, type: .tiff)), forType: .tiff)
        let captured = try XCTUnwrap(ClipboardImage.capture(board))
        let item = try XCTUnwrap(ClipboardImage.item(captured))
        XCTAssertNotNil(item.data(forType: .png))
        XCTAssertNotNil(item.data(forType: .tiff))
        let decoded = try XCTUnwrap(NSBitmapImageRep(data: captured))
        XCTAssertEqual(decoded.pixelsWide, 2)
        XCTAssertEqual(try XCTUnwrap(decoded.colorAt(x: 0, y: 0)).alphaComponent, 0.5, accuracy: 0.01)
        XCTAssertNil(ClipboardImage.item(Data("not an image".utf8)))
    }

    func testImageSnapshotHonorsPrivacyAndDoesNotFallBackToText() throws {
        for privateCopy in [false, true] {
            let board = NSPasteboard(name: .init("DeusKVMTests.\(UUID().uuidString)"))
            defer { board.releaseGlobally() }
            let item = NSPasteboardItem()
            try item.setData(png(), forType: .png)
            item.setString("screenshot label", forType: .string)
            if privateCopy { item.setData(Data(), forType: .init("org.nspasteboard.ConcealedType")) }
            board.writeObjects([item])
            let snapshot = expectation(description: "image snapshot")
            let clipboard = ClipboardPasteboard(board: board, snapshot: { value in
                XCTAssertNil(value.text)
                XCTAssertEqual(value.file?.contentType, privateCopy ? nil : "image/png")
                snapshot.fulfill()
            }, yielded: { _, _ in })
            clipboard.configure(epoch: 1, enabled: true)
            wait(for: [snapshot], timeout: 3)
            clipboard.configure(epoch: 1, enabled: false)
        }
    }

    func testImageSourceRangesAndWireLimits() throws {
        let bytes = try png()
        let source = try XCTUnwrap(ClipboardFile(png: bytes))
        XCTAssertEqual(source.read(offset: 0, count: bytes.count), bytes)
        XCTAssertEqual(source.read(offset: UInt32(bytes.count)), Data())
        XCTAssertNil(source.read(offset: UInt32(bytes.count + 1)))
        XCTAssertNil(source.read(offset: 0, count: FileNetworkCrypto.maximumBlock + 1))
        var offer = ClipboardFileOffer(
            epoch: 1, clipboardSequence: 2, sequence: 3, name: "Screenshot.png",
            size: bytes.count, contentType: "image/png"
        )
        XCTAssertTrue(offer.valid)
        let encoded = try JSONEncoder().encode(offer)
        XCTAssertTrue(try JSONDecoder().decode(ClipboardFileOffer.self, from: encoded).isImage)
        offer.contentType = "image/jpeg"
        XCTAssertFalse(offer.valid)
        XCTAssertFalse(ClipboardFileOffer(
            epoch: 1, clipboardSequence: 2, sequence: 3, name: "Screenshot.png",
            size: ClipboardFile.maximumImageBytes + 1, contentType: "image/png"
        ).valid)
        XCTAssertFalse(ClipboardFileOffer(
            epoch: 1, clipboardSequence: 2, sequence: 3, name: "Screenshot.png", size: 0, contentType: "image/png"
        ).valid)
    }

    func testImageOfferCannotReplaceANewerLocalCopy() {
        let board = NSPasteboard(name: .init("DeusKVMTests.\(UUID().uuidString)"))
        defer { board.releaseGlobally() }
        board.setString("initial", forType: .string)
        let revision = board.changeCount
        let primed = expectation(description: "primed")
        let changed = expectation(description: "newer local copy")
        let requested = expectation(description: "obsolete image must not download")
        requested.isInverted = true
        let clipboard = ClipboardPasteboard(board: board, snapshot: { snapshot in
            if snapshot.revision == revision { primed.fulfill() } else { changed.fulfill() }
        }, yielded: { _, _ in })
        clipboard.requestImage = { _, _ in requested.fulfill() }
        clipboard.configure(epoch: 1, enabled: true)
        wait(for: [primed], timeout: 2)
        board.clearContents(); board.setString("new local copy", forType: .string)
        clipboard.writeFile(ClipboardFileOffer(
            epoch: 1, clipboardSequence: 2, sequence: 3, name: "Screenshot.png", size: 100, contentType: "image/png"
        ), revision: revision)
        wait(for: [changed, requested], timeout: 0.5)
        XCTAssertEqual(board.string(forType: .string), "new local copy")
        clipboard.configure(epoch: 1, enabled: false)
    }
}
