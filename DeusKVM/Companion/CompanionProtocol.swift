import Foundation

enum CompanionProtocol {
    static let version = 1
    /// The largest message; each travels whole in one ControlLink frame.
    static let maximumPayload = 131_072

    enum Message: UInt8, Sendable {
        case hello = 0x01, ping = 0x02, pong = 0x03, screens = 0x04, config = 0x05
        case enter = 0x11, enterAck = 0x12, leave = 0x13, state = 0x14, exit = 0x15, resume = 0x16, enterCenter = 0x17
        case availability = 0x18, selection = 0x19
        case clipGrab = 0x20, clipGet = 0x21, clipData = 0x22, clipState = 0x23
        case fileOffer = 0x24, fileGet = 0x25, fileData = 0x26, fileAccept = 0x27
        /// Problem report: the Mac asks (control stream, u32 token); Windows answers on the bulk
        /// stream with the token and UTF-8 diagnostic text. Offered via HELLO `report: 1`.
        case reportRequest = 0x30, report = 0x31
        /// The Mac's local-network pointer listener (bulk stream, JSON `InputNetworkOffer`).
        /// Offered via HELLO `inputNetwork: 1` from the companion; 2 also takes keyboard, media-key
        /// and scroll records on the stream.
        case inputOffer = 0x32
        case clipboardSnapshot = 0x52
        case nack = 0x7F
    }

    struct Packet: Equatable, Sendable {
        let stream: UInt8
        let type: UInt8
        let payload: Data
    }

    enum Failure: Error { case malformed, sequence }

    static func u64(_ value: UInt64) -> Data {
        Data((0 ..< 8).map { UInt8(truncatingIfNeeded: value >> ($0 * 8)) })
    }

    static func read64(_ data: Data, _ offset: Int) -> UInt64 {
        (0 ..< 8).reduce(0) { $0 | UInt64(data[data.startIndex + offset + $1]) << ($1 * 8) }
    }

    static func u32(_ value: UInt32) -> Data {
        Data((0 ..< 4).map { UInt8(truncatingIfNeeded: value >> ($0 * 8)) })
    }

    static func read32(_ data: Data, _ offset: Int) -> UInt32 {
        (0 ..< 4).reduce(0) { $0 | UInt32(data[data.startIndex + offset + $1]) << ($1 * 8) }
    }
}

struct CompanionHello: Codable {
    let v: Int
    let role: String
    let name: String
    var resume: Bool?
    var center: Bool?
    var clipboard: Int?
    var files: Int?
    var fileReceive: Int?
    var fileNetwork: Int?
    var images: Int?
    var computerName: String?
    var selection: Int?
    var available: Bool?
    var availabilityEpoch: UInt32?
    var report: Int?
    var inputNetwork: Int?
    var multiInput: Int?
    var clipboardSnapshot: Int?
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
    static func packet(
        id: UInt8,
        edge: UInt8,
        fraction: UInt16,
        fromEdge: Bool,
        supportsCenter: Bool,
        capture: UInt64 = 0
    ) -> CompanionProtocol.Packet {
        if !fromEdge, supportsCenter {
            return .init(
                stream: 0,
                type: CompanionProtocol.Message.enterCenter.rawValue,
                payload: Data([id, edge]) + CompanionProtocol.u64(capture)
            )
        }
        return .init(stream: 0, type: CompanionProtocol.Message.enter.rawValue, payload: Data([
            id, edge, UInt8(truncatingIfNeeded: fraction), UInt8(fraction >> 8)
        ]) + CompanionProtocol.u64(capture))
    }
}
