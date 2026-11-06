import CoreBluetooth
import Foundation

/// HID profile constants shared between low energy (HOGP) and classic modes
enum HIDProfile {
    // use full UUID strings: CBPeripheralManager rejects short-form service UUIDs

    // services
    nonisolated(unsafe) static let batteryService = CBUUID(string: "0000180F-0000-1000-8000-00805F9B34FB")
    nonisolated(unsafe) static let deviceInformationService = CBUUID(string: "0000180A-0000-1000-8000-00805F9B34FB")
    nonisolated(unsafe) static let hidService = CBUUID(string: "00001812-0000-1000-8000-00805F9B34FB")

    // descriptors
    nonisolated(unsafe) static let cccd = CBUUID(string: "00002902-0000-1000-8000-00805F9B34FB")
    nonisolated(unsafe) static let externalReportReference = CBUUID(string: "00002907-0000-1000-8000-00805F9B34FB")
    nonisolated(unsafe) static let reportReference = CBUUID(string: "00002908-0000-1000-8000-00805F9B34FB")

    // characteristics
    nonisolated(unsafe) static let batteryLevel = CBUUID(string: "00002A19-0000-1000-8000-00805F9B34FB")
    nonisolated(unsafe) static let bootKeyboardInputReport = CBUUID(string: "00002A22-0000-1000-8000-00805F9B34FB")
    nonisolated(unsafe) static let bootKeyboardOutputReport = CBUUID(string: "00002A32-0000-1000-8000-00805F9B34FB")
    nonisolated(unsafe) static let bootMouseInputReport = CBUUID(string: "00002A33-0000-1000-8000-00805F9B34FB")
    nonisolated(unsafe) static let hidInformation = CBUUID(string: "00002A4A-0000-1000-8000-00805F9B34FB")
    nonisolated(unsafe) static let reportMap = CBUUID(string: "00002A4B-0000-1000-8000-00805F9B34FB")
    nonisolated(unsafe) static let hidControlPoint = CBUUID(string: "00002A4C-0000-1000-8000-00805F9B34FB")
    nonisolated(unsafe) static let report = CBUUID(string: "00002A4D-0000-1000-8000-00805F9B34FB")
    nonisolated(unsafe) static let protocolMode = CBUUID(string: "00002A4E-0000-1000-8000-00805F9B34FB")

    // device information service characteristics
    nonisolated(unsafe) static let manufacturerName = CBUUID(string: "00002A29-0000-1000-8000-00805F9B34FB")
    nonisolated(unsafe) static let modelNumber = CBUUID(string: "00002A24-0000-1000-8000-00805F9B34FB")
    nonisolated(unsafe) static let pnpID = CBUUID(string: "00002A50-0000-1000-8000-00805F9B34FB")

    /// bcdHID 0x0111, country 0, RemoteWake | NormallyConnectable
    static let hidInformationValue = Data([0x11, 0x01, 0x00, 0x03])

    /// battery level (0x2A19), little-endian
    static let externalReportReferenceValue = Data([0x19, 0x2A])

    static let manufacturerNameValue = Data("DeusKVM".utf8)
    static let modelNumberValue = Data("DeusKVM-1.0".utf8)

    /// PnP ID: VendorIDSource(BTSIG=1), VendorID 0xFFFF (test), ProductID 0x0001, Version 0x0100
    static let pnpIDValue = Data([0x01, 0xFF, 0xFF, 0x01, 0x00, 0x00, 0x01])
}

enum ReportID: UInt8 {
    case mouse = 1
    case keyboard = 2
    case keyboardLEDs = 3
    case battery = 4
    case systemControl = 5
    case consumerControl = 6
    /// Precision touchpad experiment (PRECISION_TOUCHPAD_PLAN.md): contact frames and the
    /// feature reports Windows reads and writes around them.
    case touchpad = 7
    case touchpadCapabilities = 8
    case touchpadCertification = 9
    case touchpadInputMode = 10
    case touchpadSelectiveReporting = 11
}

enum ReportType: UInt8 {
    case input = 1
    case output = 2
    case feature = 3
}

extension ReportID {
    func descriptor(_ type: ReportType) -> Data {
        Data([rawValue, type.rawValue])
    }
}

/// HID report map, including vertical and horizontal mouse scrolling.
extension HIDProfile {
    static let reportMapData = Data([
        // mouse, Report ID 1
        0x05, 0x01, // Usage Page (Generic Desktop)
        0x09, 0x02, // Usage (Mouse)
        0xA1, 0x01, // Collection (Application)
        0x85, 0x01, //   Report ID (1)
        0x09, 0x01, //   Usage (Pointer)
        0xA1, 0x00, //   Collection (Physical)
        0x05, 0x09, //     Usage Page (Button)
        0x19, 0x01, //     Usage Min (1)
        0x29, 0x03, //     Usage Max (3)
        0x75, 0x01, //     Report Size (1)
        0x95, 0x03, //     Report Count (3)
        0x15, 0x00, //     Logical Min (0)
        0x25, 0x01, //     Logical Max (1)
        0x81, 0x02, //     Input (Data,Var,Abs)
        0x95, 0x05, //     Report Count (5)
        0x81, 0x03, //     Input (Const,Var,Abs) padding
        0x05, 0x01, //     Usage Page (Generic Desktop)
        0x09, 0x30, //     Usage (X)
        0x09, 0x31, //     Usage (Y)
        0x09, 0x38, //     Usage (Wheel)
        0x75, 0x08, //     Report Size (8)
        0x95, 0x03, //     Report Count (3)
        0x15, 0x81, //     Logical Min (-127)
        0x25, 0x7F, //     Logical Max (127)
        0x81, 0x06, //     Input (Data,Var,Rel)
        0x05, 0x0C, //     Usage Page (Consumer)
        0x0A, 0x38, 0x02, // Usage (AC Pan: horizontal wheel)
        0x95, 0x01, //     Report Count (1); inherits signed 8-bit range
        0x81, 0x06, //     Input (Data,Var,Rel)
        0xC0, //   End Collection
        0xC0, // End Collection

        // keyboard input + LED output, Report IDs 2 and 3 (61 bytes)
        0x05, 0x01, // Usage Page (Generic Desktop)
        0x09, 0x06, // Usage (Keyboard)
        0xA1, 0x01, // Collection (Application)
        0x85, 0x02, //   Report ID (2)
        0x05, 0x07, //   Usage Page (Keyboard/Keypad)
        0x19, 0xE0, //   Usage Min (LeftControl)
        0x29, 0xE7, //   Usage Max (RightGUI)
        0x75, 0x01, //   Report Size (1)
        0x95, 0x08, //   Report Count (8)
        0x15, 0x00, //   Logical Min (0)
        0x25, 0x01, //   Logical Max (1)
        0x81, 0x02, //   Input (Data,Var,Abs): 8 modifier bits
        0x95, 0x01, //   Report Count (1)
        0x75, 0x08, //   Report Size (8)
        0x81, 0x01, //   Input (Const,Array,Abs): reserved byte
        0x19, 0x00, //   Usage Min (0)
        0x29, 0xDD, //   Usage Max (221)
        0x95, 0x06, //   Report Count (6)
        0x25, 0xDD, //   Logical Max (221)
        0x81, 0x00, //   Input (Data,Array,Abs): 6 keys
        0x85, 0x03, //   Report ID (3)
        0x05, 0x08, //   Usage Page (LEDs)
        0x19, 0x01, //   Usage Min (NumLock)
        0x29, 0x05, //   Usage Max (Kana)
        0x95, 0x05, //   Report Count (5)
        0x75, 0x01, //   Report Size (1)
        0x25, 0x01, //   Logical Max (1)
        0x91, 0x02, //   Output (Data,Var,Abs): 5 LED bits
        0x95, 0x03, //   Report Count (3)
        0x91, 0x03, //   Output (Const,Var,Abs): LED padding
        0xC0, // End Collection

        // battery via HID, Report ID 4 (23 bytes)
        0x05, 0x0C, // Usage Page (Consumer)
        0x09, 0x01, // Usage (Consumer Control)
        0xA1, 0x01, // Collection (Application)
        0x85, 0x04, //   Report ID (4)
        0x05, 0x06, //   Usage Page (Generic Device Controls)
        0x09, 0x20, //   Usage (Battery Strength)
        0x75, 0x08, //   Report Size (8)
        0x95, 0x01, //   Report Count (1)
        0x15, 0x00, //   Logical Min (0)
        0x25, 0x64, //   Logical Max (100)
        0x81, 0x02, //   Input (Data,Var,Abs)
        0xC0, // End Collection

        // system control, Report ID 5 (35 bytes)
        0x05, 0x01, // Usage Page (Generic Desktop)
        0x09, 0x80, // Usage (System Control)
        0xA1, 0x01, // Collection (Application)
        0x85, 0x05, //   Report ID (5)
        0x09, 0x81, //   Usage (System Power Down)
        0x09, 0x82, //   Usage (System Sleep)
        0x09, 0x8E, //   Usage (System Cold Restart)
        0x09, 0xA8, //   Usage (System Display Toggle Int/Ext)
        0x09, 0x8F, //   Usage (System Display LCD Autoscale)
        0x09, 0x85, //   Usage (System Main Menu)
        0x09, 0x86, //   Usage (System App Menu)
        0x09, 0xA7, //   Usage (System Display Brightness Decrement)
        0x75, 0x01, //   Report Size (1)
        0x95, 0x08, //   Report Count (8)
        0x15, 0x00, //   Logical Min (0)
        0x25, 0x01, //   Logical Max (1)
        0x81, 0x06, //   Input (Data,Var,Rel)
        0xC0, // End Collection

        // consumer control, Report ID 6 (68 bytes)
        0x05, 0x0C, // Usage Page (Consumer)
        0x09, 0x01, // Usage (Consumer Control)
        0xA1, 0x01, // Collection (Application)
        0x85, 0x06, //   Report ID (6)
        0x19, 0x00, //   Usage Min (0)
        0x2A, 0x74, 0x01, //   Usage Max (0x0174)
        0x75, 0x10, //   Report Size (16)
        0x95, 0x01, //   Report Count (1)
        0x15, 0x00, //   Logical Min (0)
        0x26, 0x74, 0x01, //   Logical Max (0x0174)
        0x81, 0x00, //   Input (Data,Array,Abs): 16-bit consumer
        0x1A, 0x81, 0x01, //   Usage Min (0x0181)
        0x2A, 0xCB, 0x01, //   Usage Max (0x01CB)
        0x95, 0x01, //   Report Count (1)
        0x75, 0x08, //   Report Size (8)
        0x15, 0x01, //   Logical Min (1)
        0x25, 0x4B, //   Logical Max (75)
        0x81, 0x00, //   Input (Data,Array,Abs)
        0x1A, 0x01, 0x02, //   Usage Min (0x0201)
        0x2A, 0xB0, 0x02, //   Usage Max (0x02B0)
        0x25, 0xB0, //   Logical Max (0xB0)
        0x81, 0x00, //   Input (Data,Array,Abs)
        0xA1, 0x03, //   Collection (Report)
        0x19, 0x00, //     Usage Min (0)
        0x29, 0xFF, //     Usage Max (0xFF)
        0x95, 0x01, //     Report Count (1)
        0x75, 0x08, //     Report Size (8)
        0x15, 0x00, //     Logical Min (0)
        0x25, 0xFF, //     Logical Max (0xFF)
        0x81, 0x00, //     Input (Data,Array,Abs)
        0xC0, //   End Collection
        0xC0 // End Collection
    ])
}

/// Windows Precision Touchpad collection and its Configuration collection, appended to the
/// report map when the experiment is on. Layout follows Microsoft's sample descriptor: five
/// finger collections (confidence, tip switch, identifier, X, Y), then scan time, contact
/// count and the button; Device Capabilities and Certification Status as features; Input
/// Mode and Selective Reporting in the Configuration collection.
extension HIDProfile {
    static func reportMap(touchpad: Bool) -> Data {
        touchpad ? reportMapData + touchpadReportMapData : reportMapData
    }

    /// Physical size in 0.01 inch: the Bluetooth Magic Trackpad surface, 156.0 × 110.4 mm.
    static let touchpadPhysicalWidth = 614
    static let touchpadPhysicalHeight = 435

    static let touchpadReportMapData: Data = {
        var map: [UInt8] = [
            0x05, 0x0D, // Usage Page (Digitizer)
            0x09, 0x05, // Usage (Touch Pad)
            0xA1, 0x01, // Collection (Application)
            0x85, ReportID.touchpad.rawValue // Report ID (7)
        ]
        for _ in 0 ..< TouchpadReport.maximumContacts {
            map += touchpadFinger
        }
        map += [
            0x05, 0x0D, //   Usage Page (Digitizer)
            0x55, 0x0C, //   Unit Exponent (-4)
            0x66, 0x01, 0x10, // Unit (Seconds)
            0x47, 0xFF, 0xFF, 0x00, 0x00, // Physical Max (65535)
            0x27, 0xFF, 0xFF, 0x00, 0x00, // Logical Max (65535)
            0x75, 0x10, //   Report Size (16)
            0x95, 0x01, //   Report Count (1)
            0x09, 0x56, //   Usage (Scan Time)
            0x81, 0x02, //   Input (Data,Var,Abs)
            0x55, 0x00, //   Unit Exponent (0)
            0x65, 0x00, //   Unit (None)
            0x45, 0x00, //   Physical Max (0: same as logical)
            0x09, 0x54, //   Usage (Contact Count)
            0x25, 0x7F, //   Logical Max (127)
            0x75, 0x08, //   Report Size (8)
            0x81, 0x02, //   Input (Data,Var,Abs)
            0x05, 0x09, //   Usage Page (Button)
            0x09, 0x01, //   Usage (Button 1)
            0x25, 0x01, //   Logical Max (1)
            0x75, 0x01, //   Report Size (1)
            0x81, 0x02, //   Input (Data,Var,Abs)
            0x95, 0x07, //   Report Count (7)
            0x81, 0x03, //   Input (Const,Var,Abs) padding

            0x05, 0x0D, //   Usage Page (Digitizer)
            0x85, ReportID.touchpadCapabilities.rawValue, // Report ID (8)
            0x09, 0x55, //   Usage (Contact Count Maximum)
            0x09, 0x59, //   Usage (Pad Type)
            0x75, 0x04, //   Report Size (4)
            0x95, 0x02, //   Report Count (2)
            0x25, 0x0F, //   Logical Max (15)
            0xB1, 0x02, //   Feature (Data,Var,Abs)

            0x06, 0x00, 0xFF, // Usage Page (Vendor 0xFF00)
            0x85, ReportID.touchpadCertification.rawValue, // Report ID (9)
            0x09, 0xC5, //   Usage (Certification Status)
            0x15, 0x00, //   Logical Min (0)
            0x26, 0xFF, 0x00, // Logical Max (255)
            0x75, 0x08, //   Report Size (8)
            0x96, 0x00, 0x01, // Report Count (256)
            0xB1, 0x02, //   Feature (Data,Var,Abs)
            0xC0, // End Collection

            0x05, 0x0D, // Usage Page (Digitizer)
            0x09, 0x0E, // Usage (Device Configuration)
            0xA1, 0x01, // Collection (Application)
            0x85, ReportID.touchpadInputMode.rawValue, // Report ID (10)
            0x09, 0x22, //   Usage (Finger)
            0xA1, 0x02, //   Collection (Logical)
            0x09, 0x52, //     Usage (Input Mode)
            0x15, 0x00, //     Logical Min (0)
            0x25, 0x0A, //     Logical Max (10)
            0x75, 0x08, //     Report Size (8)
            0x95, 0x01, //     Report Count (1)
            0xB1, 0x02, //     Feature (Data,Var,Abs)
            0xC0, //   End Collection
            0x09, 0x22, //   Usage (Finger)
            0xA1, 0x00, //   Collection (Physical)
            0x85, ReportID.touchpadSelectiveReporting.rawValue, // Report ID (11)
            0x09, 0x57, //     Usage (Surface Switch)
            0x09, 0x58, //     Usage (Button Switch)
            0x75, 0x01, //     Report Size (1)
            0x95, 0x02, //     Report Count (2)
            0x25, 0x01, //     Logical Max (1)
            0xB1, 0x02, //     Feature (Data,Var,Abs)
            0x95, 0x06, //     Report Count (6)
            0xB1, 0x03, //     Feature (Const,Var,Abs) padding
            0xC0, //   End Collection
            0xC0 // End Collection
        ]
        return Data(map)
    }()

    /// One finger: 6 bytes. The usage page, units and physical range are reset first so the
    /// bit fields do not inherit the previous finger's Generic Desktop page and inch scale.
    private static let touchpadFinger: [UInt8] = [
        0x05, 0x0D, //   Usage Page (Digitizer)
        0x09, 0x22, //   Usage (Finger)
        0xA1, 0x02, //   Collection (Logical)
        0x55, 0x00, //     Unit Exponent (0)
        0x65, 0x00, //     Unit (None)
        0x45, 0x00, //     Physical Max (0)
        0x15, 0x00, //     Logical Min (0)
        0x25, 0x01, //     Logical Max (1)
        0x09, 0x47, //     Usage (Confidence)
        0x09, 0x42, //     Usage (Tip Switch)
        0x75, 0x01, //     Report Size (1)
        0x95, 0x02, //     Report Count (2)
        0x81, 0x02, //     Input (Data,Var,Abs)
        0x95, 0x06, //     Report Count (6)
        0x81, 0x03, //     Input (Const,Var,Abs) padding
        0x09, 0x51, //     Usage (Contact Identifier)
        0x25, 0x7F, //     Logical Max (127)
        0x75, 0x08, //     Report Size (8)
        0x95, 0x01, //     Report Count (1)
        0x81, 0x02, //     Input (Data,Var,Abs)
        0x05, 0x01, //     Usage Page (Generic Desktop)
        0x26, 0xFF, 0x0F, // Logical Max (4095)
        0x75, 0x10, //     Report Size (16)
        0x55, 0x0E, //     Unit Exponent (-2)
        0x65, 0x13, //     Unit (Inch, English Linear)
        0x09, 0x30, //     Usage (X)
        0x35, 0x00, //     Physical Min (0)
        0x46, UInt8(touchpadPhysicalWidth & 0xFF), UInt8(touchpadPhysicalWidth >> 8), // Physical Max (614)
        0x81, 0x02, //     Input (Data,Var,Abs)
        0x46, UInt8(touchpadPhysicalHeight & 0xFF), UInt8(touchpadPhysicalHeight >> 8), // Physical Max (435)
        0x09, 0x31, //     Usage (Y)
        0x81, 0x02, //     Input (Data,Var,Abs)
        0xC0 //   End Collection
    ]
}
