import Foundation

/// One finger on the pad, in the touchpad collection's logical range (0...4095 on both axes,
/// origin at the top left like a Windows digitizer).
struct TouchpadContact: Sendable, Equatable {
    var id: UInt8
    var x: UInt16
    var y: UInt16
    /// False once, when the finger lifts; Windows then forgets the identifier.
    var tip = true
    /// True for a finger, false for a palm.
    var confidence = true
}

/// The Windows Precision Touchpad input report (Report ID 7): two contact slots, scan time,
/// contact count and the pad's button, 16 bytes. The pad reports in hybrid mode: a frame with
/// more contacts than slots is sent as several reports with the same scan time, the first
/// carrying the frame's contact count and the rest zero (`reports(contacts:scanTime:button:)`).
/// Unused slots stay zero.
struct TouchpadReport: Sendable, Equatable {
    /// Contact Count Maximum in Device Capabilities: what a frame may hold.
    static let maximumContacts = 5
    /// Slots in the descriptor: what one report may hold.
    static let contactsPerReport = 2
    static let logicalMaximum: UInt16 = 4095
    private static let contactBytes = 6

    var contacts: [TouchpadContact] = []
    /// Relative scan time in 100 µs units, wrapping at 16 bits.
    var scanTime: UInt16 = 0
    /// Contacts in the whole frame; zero in a frame's second and later reports.
    var contactCount: UInt8 = 0
    var button = false

    static let zero = TouchpadReport()

    /// One frame as the reports that carry it, in sending order.
    static func reports(contacts: [TouchpadContact], scanTime: UInt16, button: Bool) -> [TouchpadReport] {
        let frame = Array(contacts.prefix(maximumContacts))
        let chunks = stride(from: 0, to: max(frame.count, 1), by: contactsPerReport).map { start in
            Array(frame[start ..< min(start + contactsPerReport, frame.count)])
        }
        return chunks.enumerated().map { index, slice in
            TouchpadReport(contacts: slice, scanTime: scanTime, contactCount: index == 0 ? UInt8(frame.count) : 0, button: button)
        }
    }

    var data: Data {
        var bytes = [UInt8](repeating: 0, count: Self.contactsPerReport * Self.contactBytes + 4)
        for (slot, contact) in contacts.prefix(Self.contactsPerReport).enumerated() {
            let base = slot * Self.contactBytes
            bytes[base] = (contact.confidence ? 1 : 0) | (contact.tip ? 2 : 0)
            bytes[base + 1] = contact.id
            bytes[base + 2] = UInt8(contact.x & 0xFF)
            bytes[base + 3] = UInt8(contact.x >> 8)
            bytes[base + 4] = UInt8(contact.y & 0xFF)
            bytes[base + 5] = UInt8(contact.y >> 8)
        }
        let tail = Self.contactsPerReport * Self.contactBytes
        bytes[tail] = UInt8(scanTime & 0xFF)
        bytes[tail + 1] = UInt8(scanTime >> 8)
        bytes[tail + 2] = contactCount
        bytes[tail + 3] = button ? 1 : 0
        return Data(bytes)
    }

    /// Scan time for a report sent now: 100 µs ticks of the uptime clock, wrapping at 16 bits.
    static func scanTime(uptime: TimeInterval) -> UInt16 {
        UInt16(truncatingIfNeeded: Int((uptime * 10000).rounded(.down)))
    }
}

/// Feature reports Windows reads and writes around the touchpad collection. The device
/// answers reads from these values and keeps what Windows writes.
extension TouchpadReport {
    /// Device Capabilities (Report ID 8): Contact Count Maximum in the low nibble and Pad Type
    /// in the high nibble, 0 meaning a depressible click-pad.
    static let capabilities = Data([UInt8(maximumContacts)])

    /// Input Mode (Report ID 10) values: the device starts in mouse mode and Windows writes
    /// 3 to select Windows Precision Touchpad reports.
    static let inputModeMouse: UInt8 = 0
    static let inputModePrecisionTouchpad: UInt8 = 3

    /// Selective Reporting (Report ID 11): bit 0 surface contacts, bit 1 button, both on.
    static let selectiveReportingDefault = Data([0x03])

    /// Device Certification Status (Report ID 9). Windows 10 and later accept any 256 bytes;
    /// these are the default blob Microsoft's Windows Precision Touchpad collection page lists.
    static let certification = Data([
        0xFC, 0x28, 0xFE, 0x84, 0x40, 0xCB, 0x9A, 0x87, 0x0D, 0xBE, 0x57, 0x3C, 0xB6, 0x70, 0x09, 0x88,
        0x07, 0x97, 0x2D, 0x2B, 0xE3, 0x38, 0x34, 0xB6, 0x6C, 0xED, 0xB0, 0xF7, 0xE5, 0x9C, 0xF6, 0xC2,
        0x2E, 0x84, 0x1B, 0xE8, 0xB4, 0x51, 0x78, 0x43, 0x1F, 0x28, 0x4B, 0x7C, 0x2D, 0x53, 0xAF, 0xFC,
        0x47, 0x70, 0x1B, 0x59, 0x6F, 0x74, 0x43, 0xC4, 0xF3, 0x47, 0x18, 0x53, 0x1A, 0xA2, 0xA1, 0x71,
        0xC7, 0x95, 0x0E, 0x31, 0x55, 0x21, 0xD3, 0xB5, 0x1E, 0xE9, 0x0C, 0xBA, 0xEC, 0xB8, 0x89, 0x19,
        0x3E, 0xB3, 0xAF, 0x75, 0x81, 0x9D, 0x53, 0xB9, 0x41, 0x57, 0xF4, 0x6D, 0x39, 0x25, 0x29, 0x7C,
        0x87, 0xD9, 0xB4, 0x98, 0x45, 0x7D, 0xA7, 0x26, 0x9C, 0x65, 0x3B, 0x85, 0x68, 0x89, 0xD7, 0x3B,
        0xBD, 0xFF, 0x14, 0x67, 0xF2, 0x2B, 0xF0, 0x2A, 0x41, 0x54, 0xF0, 0xFD, 0x2C, 0x66, 0x7C, 0xF8,
        0xC0, 0x8F, 0x33, 0x13, 0x03, 0xF1, 0xD3, 0xC1, 0x0B, 0x89, 0xD9, 0x1B, 0x62, 0xCD, 0x51, 0xB7,
        0x80, 0xB8, 0xAF, 0x3A, 0x10, 0xC1, 0x8A, 0x5B, 0xE8, 0x8A, 0x56, 0xF0, 0x8C, 0xAA, 0xFA, 0x35,
        0xE9, 0x42, 0xC4, 0xD8, 0x55, 0xC3, 0x38, 0xCC, 0x2B, 0x53, 0x5C, 0x69, 0x52, 0xD5, 0xC8, 0x73,
        0x02, 0x38, 0x7C, 0x73, 0xB6, 0x41, 0xE7, 0xFF, 0x05, 0xD8, 0x2B, 0x79, 0x9A, 0xE2, 0x34, 0x60,
        0x8F, 0xA3, 0x32, 0x1F, 0x09, 0x78, 0x62, 0xBC, 0x80, 0xE3, 0x0F, 0xBD, 0x65, 0x20, 0x08, 0x13,
        0xC1, 0xE2, 0xEE, 0x53, 0x2D, 0x86, 0x7E, 0xA7, 0x5A, 0xC5, 0xD3, 0x7D, 0x98, 0xBE, 0x31, 0x48,
        0x1F, 0xFB, 0xDA, 0xAF, 0xA2, 0xA8, 0x6A, 0x89, 0xD6, 0xBF, 0xF2, 0xD3, 0x32, 0x2A, 0x9A, 0xE4,
        0xCF, 0x17, 0xB7, 0xB8, 0xF4, 0xE1, 0x33, 0x08, 0x24, 0x8B, 0xC4, 0x43, 0xA5, 0xE5, 0x24, 0xC2
    ])

    static let featureDefaults: [UInt8: Data] = [
        ReportID.touchpadCapabilities.rawValue: capabilities,
        ReportID.touchpadCertification.rawValue: certification,
        ReportID.touchpadInputMode.rawValue: Data([inputModeMouse]),
        ReportID.touchpadSelectiveReporting.rawValue: selectiveReportingDefault
    ]

    /// Names for the diagnostics log; nil for reports that are not touchpad features.
    static func featureName(_ id: UInt8) -> String? {
        switch ReportID(rawValue: id) {
        case .touchpadCapabilities: "Device Capabilities"
        case .touchpadCertification: "Certification Status"
        case .touchpadInputMode: "Input Mode"
        case .touchpadSelectiveReporting: "Selective Reporting"
        default: nil
        }
    }
}
