import Foundation
import XCTest

final class CompanionProtocolTests: XCTestCase {
    private struct Vector: Decodable {
        let stream: UInt8
        let type: UInt8
        let payload: String
        let sequence: UInt8
        let chunk: Int?
        let frames: [String]
    }

    private func data(_ hex: String) -> Data {
        let chars = Array(hex)
        return Data(stride(from: 0, to: chars.count, by: 2).map { UInt8(String(chars[$0 ..< $0 + 2]), radix: 16)! })
    }

    func testSharedWireFixturesAndStandardCRC() throws {
        let url = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("protocol/fixtures.json")
        for vector in try JSONDecoder().decode([Vector].self, from: Data(contentsOf: url)) {
            let packet = CompanionProtocol.Packet(stream: vector.stream, type: vector.type, payload: data(vector.payload))
            var encoder = CompanionProtocol.Encoder(sequence: vector.sequence, chunkSize: vector.chunk ?? CompanionProtocol.chunkSize)
            let frames = encoder.encode(packet)
            XCTAssertEqual(frames, vector.frames.map(data))
            var decoder = CompanionProtocol.Decoder()
            var result: CompanionProtocol.Packet?
            for frame in frames {
                result = try decoder.receive(frame)
            }
            XCTAssertEqual(result, packet)
        }
        XCTAssertEqual(CompanionProtocol.crc(Data("123456789".utf8)), 0xE306_9283)
    }

    func testCorruptionSequenceGapAndOversizeAreRejected() throws {
        var encoder = CompanionProtocol.Encoder()
        var frames = encoder.encode(.init(stream: 1, type: 4, payload: Data(0 ..< 40)))
        var decoder = CompanionProtocol.Decoder()
        _ = try decoder.receive(frames[0])
        XCTAssertThrowsError(try decoder.receive(frames[2]))
        decoder = CompanionProtocol.Decoder()
        frames[2][frames[2].count - 1] ^= 1
        _ = try decoder.receive(frames[0]); _ = try decoder.receive(frames[1])
        XCTAssertThrowsError(try decoder.receive(frames[2]))
        decoder = CompanionProtocol.Decoder()
        XCTAssertThrowsError(try decoder.receive(Data([0, 0x13, 4, 1, 0, 1, 0])))
        XCTAssertThrowsError(try decoder.receive(Data([0, 0x17, 4, 0, 0, 0, 0])))
    }

    func testNegotiatedFramesStayWithinBothLimitsAndRoundTrip() throws {
        XCTAssertEqual(CompanionProtocol.frameLimit(17), 20)
        XCTAssertEqual(CompanionProtocol.frameLimit(182), 182)
        XCTAssertEqual(CompanionProtocol.frameLimit(524), 244)
        for chunk in [20, 64, 182, 244] {
            for size in [0, 13, 14, chunk - 7, chunk - 6, 1036, 4096, 65536] {
                let payload = Data((0 ..< size).map { UInt8(truncatingIfNeeded: $0 * 7) })
                let packet = CompanionProtocol.Packet(stream: 1, type: 0x22, payload: payload)
                var encoder = CompanionProtocol.Encoder(sequence: 250, chunkSize: chunk)
                var decoder = CompanionProtocol.Decoder()
                var result: CompanionProtocol.Packet?
                for frame in encoder.encode(packet) {
                    XCTAssertLessThanOrEqual(frame.count, chunk)
                    result = try decoder.receive(frame)
                }
                XCTAssertEqual(result, packet, "chunk \(chunk) size \(size)")
            }
        }
        var decoder = CompanionProtocol.Decoder()
        XCTAssertThrowsError(try decoder.receive(Data([0, 0x13, 4, 0, 0, 0, 0]) + Data(count: 238)))
    }

    func testFragmentBoundariesAndSequenceWrap() throws {
        for size in [13, 14, 27, 28, 256, 65536] {
            let packet = CompanionProtocol.Packet(stream: 1, type: 4, payload: Data((0 ..< size).map { UInt8(truncatingIfNeeded: $0) }))
            var encoder = CompanionProtocol.Encoder()
            var decoder = CompanionProtocol.Decoder()
            var result: CompanionProtocol.Packet?
            for frame in encoder.encode(packet) {
                XCTAssertLessThanOrEqual(frame.count, 20)
                result = try decoder.receive(frame)
            }
            XCTAssertEqual(result, packet)
        }
    }

    func testClipboardCapabilityIsOptionalForExistingCompanions() throws {
        let legacy = Data(#"{"v":1,"role":"pc","name":"Companion","chunk":20}"#.utf8)
        let current = Data(#"{"v":1,"role":"pc","name":"Companion","chunk":20,"clipboard":1}"#.utf8)
        XCTAssertNil(try JSONDecoder().decode(CompanionHello.self, from: legacy).clipboard)
        XCTAssertEqual(try JSONDecoder().decode(CompanionHello.self, from: current).clipboard, 1)
    }

    func testResumeCapabilityIsOptionalForExistingCompanions() throws {
        let legacy = Data(#"{"v":1,"role":"pc","name":"Companion","chunk":20}"#.utf8)
        let current = Data(#"{"v":1,"role":"pc","name":"Companion","chunk":20,"resume":true}"#.utf8)
        XCTAssertNil(try JSONDecoder().decode(CompanionHello.self, from: legacy).resume)
        XCTAssertEqual(try JSONDecoder().decode(CompanionHello.self, from: current).resume, true)
        XCTAssertEqual(CompanionProtocol.Message.resume.rawValue, 0x16)
    }
}
