import XCTest

final class HIDDescriptorTests: XCTestCase {
    func testMouseDescriptorMatchesWireFormatAndDeclaresRelativePan() throws {
        let inputs = try inputFields()
        XCTAssertEqual(HIDProfile.reportMap(touchpad: false), HIDProfile.reportMapData)
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

    func testTouchpadDescriptorMatchesWireFormatAndMicrosoftRequirements() throws {
        let map = HIDProfile.reportMap(touchpad: true)
        XCTAssertEqual(map.prefix(HIDProfile.reportMapData.count), HIDProfile.reportMapData, "existing collections are untouched")
        let fields = try fields(of: map)
        XCTAssertEqual(fields.filter { $0.reportID == 1 }, try inputFields().filter { $0.reportID == 1 })

        let touchpad = fields.filter { $0.reportID == 7 && $0.kind == .input }
        XCTAssertEqual(touchpad.reduce(0) { $0 + $1.size * $1.count }, TouchpadReport.zero.data.count * 8)
        let flags = touchpad.filter { $0.page == 0x0D && $0.usages == [0x47, 0x42] }
        XCTAssertEqual(flags.map(\.offset), [0, 48, 96, 144, 192], "confidence and tip switch open each finger")
        XCTAssertTrue(flags.allSatisfy { $0.size == 1 && $0.count == 2 && $0.physicalMaximum == 0 && $0.unit == 0 })
        let ids = touchpad.filter { $0.page == 0x0D && $0.usages == [0x51] }
        XCTAssertEqual(ids.map(\.offset), [8, 56, 104, 152, 200])
        XCTAssertTrue(ids.allSatisfy { $0.size == 8 && $0.maximum == 127 })
        let xs = touchpad.filter { $0.page == 0x01 && $0.usages == [0x30] }
        XCTAssertEqual(xs.map(\.offset), [16, 64, 112, 160, 208])
        XCTAssertTrue(xs
            .allSatisfy { $0.size == 16 && $0.maximum == 4095 && $0.physicalMaximum == 614 && $0.unit == 0x13 && $0.unitExponent == 0x0E })
        let ys = touchpad.filter { $0.page == 0x01 && $0.usages == [0x31] }
        XCTAssertEqual(ys.map(\.offset), [32, 80, 128, 176, 224])
        XCTAssertTrue(ys.allSatisfy { $0.size == 16 && $0.maximum == 4095 && $0.physicalMaximum == 435 && $0.unit == 0x13 })
        let scanTime = try XCTUnwrap(touchpad.first { $0.page == 0x0D && $0.usages == [0x56] })
        XCTAssertEqual(
            [scanTime.offset, scanTime.size, scanTime.maximum, scanTime.unit, scanTime.unitExponent],
            [240, 16, 65535, 0x1001, 0x0C]
        )
        let contactCount = try XCTUnwrap(touchpad.first { $0.page == 0x0D && $0.usages == [0x54] })
        XCTAssertEqual(
            [contactCount.offset, contactCount.size, contactCount.maximum, contactCount.physicalMaximum, contactCount.unit],
            [256, 8, 127, 0, 0]
        )
        let button = try XCTUnwrap(touchpad.first { $0.page == 0x09 && $0.usages == [0x01] })
        XCTAssertEqual([button.offset, button.size, button.count, button.maximum], [264, 1, 1, 1])

        let capabilities = try XCTUnwrap(fields.first { $0.reportID == 8 })
        XCTAssertEqual(capabilities.kind, .feature)
        XCTAssertEqual([capabilities.page, capabilities.size, capabilities.count], [0x0D, 4, 2])
        XCTAssertEqual(capabilities.usages, [0x55, 0x59], "Contact Count Maximum, Pad Type")
        XCTAssertEqual(capabilities.size * capabilities.count, TouchpadReport.capabilities.count * 8)
        let certification = try XCTUnwrap(fields.first { $0.reportID == 9 })
        XCTAssertEqual([certification.page, certification.usages[0], certification.size, certification.count], [0xFF00, 0xC5, 8, 256])
        XCTAssertEqual(certification.kind, .feature)
        let inputMode = try XCTUnwrap(fields.first { $0.reportID == 10 })
        XCTAssertEqual([inputMode.page, inputMode.usages[0], inputMode.size, inputMode.count, inputMode.maximum], [0x0D, 0x52, 8, 1, 10])
        let selective = fields.filter { $0.reportID == 11 }
        XCTAssertEqual(selective.first?.usages, [0x57, 0x58], "Surface Switch, Button Switch")
        XCTAssertEqual(selective.reduce(0) { $0 + $1.size * $1.count }, 8)
        XCTAssertTrue(fields.filter { $0.reportID >= 8 }.allSatisfy { $0.kind == .feature })
    }

    func testTouchpadDescriptorNestsCollectionsCorrectly() throws {
        var depth = 0
        var applications = 0
        try walkMainItems(HIDProfile.touchpadReportMapData) { tag, value in
            if tag == 10 {
                if depth == 0 { applications += 1 }
                depth += 1
                if depth == 1 { XCTAssertEqual(value, 1, "top-level collections are Application collections") }
            } else if tag == 12 {
                depth -= 1
                XCTAssertGreaterThanOrEqual(depth, 0)
            }
        }
        XCTAssertEqual(depth, 0)
        XCTAssertEqual(applications, 2, "touchpad and configuration collections")
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
    private func walkMainItems(_ data: Data, _ body: (Int, Int) throws -> Void) throws {
        let bytes = Array(data)
        var index = 0
        while index < bytes.count {
            let prefix = Int(bytes[index])
            let length = [0, 1, 2, 4][prefix & 3]
            guard prefix != 0xFE, index + length < bytes.count else { throw DescriptorError.unsupportedOrTruncatedItem }
            index += 1
            var value = 0
            for byte in 0 ..< length {
                value |= Int(bytes[index + byte]) << (8 * byte)
            }
            if (prefix >> 2) & 3 == 0 { try body(prefix >> 4, value) }
            index += length
        }
    }

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
