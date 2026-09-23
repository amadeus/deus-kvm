import Foundation

enum CompanionProtocol {
    static let version = 1
    static let chunkSize = 20
    static let maximumPayload = 65536
    enum Message: UInt8, Sendable {
        case hello = 0x01, ping = 0x02, pong = 0x03, screens = 0x04, config = 0x05
        case enter = 0x11, enterAck = 0x12, leave = 0x13, state = 0x14, exit = 0x15, resume = 0x16, enterCenter = 0x17
        case availability = 0x18, selection = 0x19
        case requestControl = 0x1A, releaseAck = 0x1B
        case clipGrab = 0x20, clipGet = 0x21, clipData = 0x22, clipState = 0x23
        case fileOffer = 0x24, fileGet = 0x25, fileData = 0x26, fileAccept = 0x27
        case nack = 0x7F
    }

    struct Packet: Equatable, Sendable {
        let stream: UInt8
        let type: UInt8
        let payload: Data
    }

    enum Failure: Error { case malformed, sequence, checksum }

    static func crc(_ data: Data) -> UInt32 {
        var result = UInt32.max
        for byte in data {
            result ^= UInt32(byte)
            for _ in 0 ..< 8 {
                result = (result >> 1) ^ (result & 1 == 1 ? 0x82F6_3B78 : 0)
            }
        }
        return ~result
    }

    static func u32(_ value: UInt32) -> Data {
        Data((0 ..< 4).map { UInt8(truncatingIfNeeded: value >> ($0 * 8)) })
    }

    static func read32(_ data: Data, _ offset: Int) -> UInt32 {
        (0 ..< 4).reduce(0) { $0 | UInt32(data[data.startIndex + offset + $1]) << ($1 * 8) }
    }

    struct Encoder {
        var sequence: UInt8 = 0
        mutating func encode(_ packet: Packet) -> [Data] {
            precondition(packet.payload.count <= maximumPayload && packet.stream <= 1 && (packet.stream != 0 || packet.payload.count <= 13))
            let firstCapacity = chunkSize - 7
            let multi = packet.payload.count > firstCapacity
            var offset = 0
            var result: [Data] = []
            var first = true
            repeat {
                let capacity = chunkSize - (first ? 7 : 2)
                let remaining = packet.payload.count - offset
                let last = multi ? !first && remaining <= capacity - 4 : true
                let count = last ? remaining : min(capacity, first ? remaining : max(0, remaining - (chunkSize - 6)))
                var frame = Data([sequence, packet.stream << 4 | (first ? 1 : 0) | (last ? 2 : 0)])
                sequence &+= 1
                if first { frame.append(packet.type); frame.append(u32(UInt32(packet.payload.count))) }
                frame.append(packet.payload.subdata(in: offset ..< offset + count))
                if last, multi { frame.append(u32(crc(packet.payload))) }
                result.append(frame)
                offset += count
                first = false
                if last { break }
            } while true
            return result
        }
    }

    struct Decoder {
        private var expected: UInt8?
        private var partial: Packet?
        private var length = 0
        mutating func receive(_ frame: Data) throws -> Packet? {
            let frame = Data(frame)
            guard frame.count >= 2, frame.count <= chunkSize, frame[1] & 0xCC == 0 else { throw Failure.malformed }
            let first = frame[1] & 1 != 0
            let last = frame[1] & 2 != 0
            let stream = frame[1] >> 4
            guard stream <= 1 else { throw Failure.malformed }
            defer { expected = frame[0] &+ 1 }
            if let expected, expected != frame[0] {
                partial = nil
                throw Failure.sequence
            }
            if first {
                guard frame.count >= 7, partial == nil else { partial = nil; throw Failure.malformed }
                length = Int(read32(frame, 3))
                guard length <= maximumPayload else { throw Failure.malformed }
                let payload = Data(frame.dropFirst(7))
                if last {
                    guard payload.count == length else { throw Failure.malformed }
                    return Packet(stream: stream, type: frame[2], payload: payload)
                }
                guard stream != 0, payload.count <= length else { throw Failure.malformed }
                partial = Packet(stream: stream, type: frame[2], payload: payload)
                return nil
            }
            guard let current = partial, current.stream == stream else { throw Failure.malformed }
            var payload = current.payload
            guard !last || frame.count >= 6 else { partial = nil; throw Failure.malformed }
            payload.append(frame.subdata(in: 2 ..< frame.count - (last ? 4 : 0)))
            guard payload.count <= length else { partial = nil; throw Failure.malformed }
            if last {
                partial = nil
                guard payload.count == length else { throw Failure.malformed }
                guard crc(payload) == read32(frame, frame.count - 4) else { throw Failure.checksum }
                return Packet(stream: stream, type: current.type, payload: payload)
            }
            partial = Packet(stream: stream, type: current.type, payload: payload)
            return nil
        }
    }
}

struct CompanionHello: Codable {
    let v: Int
    let role: String
    let name: String
    let chunk: Int
    var resume: Bool?
    var center: Bool?
    var clipboard: Int?
    var files: Int?
    var fileReceive: Int?
    var fileNetwork: Int?
    var computerName: String?
    var selection: Int?
    var available: Bool?
    var availabilityEpoch: UInt32?
    var takeover: Bool?
    var requestControl: Bool?
}

struct PCMonitor: Codable, Identifiable, Equatable {
    let id: String
    let x: Int
    let y: Int
    let w: Int
    let h: Int
    let dpi: Int
    let primary: Bool
}

struct PCScreenInfo: Codable { let monitors: [PCMonitor] }
struct PCConfiguration: Codable, Equatable {
    let edge: UInt8
    let monitor: String
    var span: [Double] = [0, 1]
    // send zero for compatibility with companions that still read the old threshold
    var pushCounts = 0
    var switchDelayMs = 0
    var doubleTapMs = 0
    var cornerPx = 0
    var heartbeatS = 3
}

enum CompanionEntry {
    static func packet(id: UInt8, edge: UInt8, fraction: UInt16, fromEdge: Bool, supportsCenter: Bool) -> CompanionProtocol.Packet {
        if !fromEdge, supportsCenter {
            return .init(stream: 0, type: CompanionProtocol.Message.enterCenter.rawValue, payload: Data([id, edge]))
        }
        return .init(stream: 0, type: CompanionProtocol.Message.enter.rawValue, payload: Data([
            id, edge, UInt8(truncatingIfNeeded: fraction), UInt8(fraction >> 8)
        ]))
    }
}
