import XCTest

final class HIDDescriptorTests: XCTestCase {
    func testMouseDescriptorMatchesWireFormatAndDeclaresRelativePan() throws {
        let inputs = try inputFields()
        let mouse = inputs.filter { $0.reportID == 1 }
        XCTAssertEqual(mouse.reduce(0) { $0 + $1.size * $1.count }, MouseReport.zero.data.count * 8)
        let axes = try XCTUnwrap(mouse.first { $0.usages == [0x30, 0x31, 0x38] })
        XCTAssertEqual(axes.page, 0x01)
        XCTAssertEqual(axes.offset, 8)
        XCTAssertEqual(axes.size, 8)
        XCTAssertEqual(axes.count, 3)
        let pan = try XCTUnwrap(mouse.first { $0.page == 0x0C && $0.usages == [0x0238] })
        XCTAssertEqual(pan.offset, 32)
        XCTAssertEqual(pan.size, 8)
        XCTAssertEqual(pan.count, 1)
        XCTAssertEqual(pan.minimum, -127)
        XCTAssertEqual(pan.maximum, 127)
        XCTAssertEqual(pan.flags, 0x06) // Data, Variable, Relative
        XCTAssertEqual(inputs.filter { $0.reportID == 2 }.reduce(0) { $0 + $1.size * $1.count }, 64)
    }

    private enum FieldKind { case input, feature }

    private struct InputField: Equatable {
        let kind: FieldKind
        let reportID, page, offset, size, count, minimum, maximum, physicalMaximum, unit, unitExponent, flags: Int
        let usages: [Int]
    }

    private func inputFields() throws -> [InputField] {
        try fields(of: HIDProfile.reportMapData).filter { $0.kind == .input }
    }

    /// Calls `body` with the tag and value of each main item (input, feature, collection, end).
    /// Decode the short items used by our descriptor, keeping global state and
    /// clearing local usages at each main item as required by the HID format.
    private func fields(of data: Data) throws -> [InputField] {
        let bytes = Array(data)
        var globals: [Int: Int] = [:]
        var usages: [Int] = []
        var offsets: [Int: Int] = [:]
        var fields: [InputField] = []
        var index = 0
        while index < bytes.count {
            let prefix = Int(bytes[index])
            let length = [0, 1, 2, 4][prefix & 3]
            let type = (prefix >> 2) & 3
            let tag = prefix >> 4
            guard prefix != 0xFE, index + length < bytes.count else {
                throw DescriptorError.unsupportedOrTruncatedItem
            }
            index += 1
            var value = 0
            for byte in 0 ..< length {
                value |= Int(bytes[index + byte]) << (8 * byte)
            }
            if type == 1 {
                guard tag != 10, tag != 11 else { throw DescriptorError.unsupportedOrTruncatedItem }
                if tag == 1, length > 0, value & (1 << (length * 8 - 1)) != 0 {
                    value -= 1 << (length * 8)
                }
                globals[tag] = value
            } else if type == 2, tag == 0 {
                // Extended usages carry their page in the high 16 bits.
                usages.append(length == 4 ? value & 0xFFFF : value)
            } else if type == 0 {
                if tag == 8 || tag == 11 {
                    let kind: FieldKind = tag == 8 ? .input : .feature
                    let reportID = globals[8, default: 0]
                    let size = globals[7, default: 0]
                    let count = globals[9, default: 0]
                    let key = reportID * 2 + (kind == .input ? 0 : 1)
                    fields.append(InputField(
                        kind: kind, reportID: reportID, page: globals[0, default: 0], offset: offsets[key, default: 0],
                        size: size, count: count, minimum: globals[1, default: 0], maximum: globals[2, default: 0],
                        physicalMaximum: globals[4, default: 0], unit: globals[6, default: 0], unitExponent: globals[5, default: 0],
                        flags: value, usages: usages
                    ))
                    offsets[key, default: 0] += size * count
                }
                usages.removeAll()
            }
            index += length
        }
        return fields
    }

    private enum DescriptorError: Error {
        case unsupportedOrTruncatedItem
    }
}
