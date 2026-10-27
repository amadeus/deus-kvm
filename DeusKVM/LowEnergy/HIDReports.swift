import Foundation

struct MouseReport: Sendable, Equatable {
    var buttons: MouseButtons = []
    var dX: Int8 = 0
    var dY: Int8 = 0
    var wheel: Int8 = 0
    var pan: Int8 = 0

    static let zero = MouseReport()

    var data: Data {
        Data([
            buttons.rawValue,
            UInt8(bitPattern: dX),
            UInt8(bitPattern: dY),
            UInt8(bitPattern: wheel),
            UInt8(bitPattern: pan)
        ])
    }

    /// Boot protocol carries only buttons and X/Y, without either scroll axis.
    var bootData: Data {
        Data([buttons.rawValue, UInt8(bitPattern: dX), UInt8(bitPattern: dY)])
    }

    /// What a read or new subscription may see: X/Y, wheel and pan are relative and
    /// must not be applied twice, so only the held buttons persist.
    var held: MouseReport {
        MouseReport(buttons: buttons)
    }
}

struct MouseButtons: OptionSet, Sendable, Equatable {
    let rawValue: UInt8
    static let left = MouseButtons(rawValue: 1 << 0)
    static let right = MouseButtons(rawValue: 1 << 1)
    static let middle = MouseButtons(rawValue: 1 << 2)
}

struct KeyboardReport: Sendable, Equatable {
    var modifiers: KeyboardModifiers = []
    var keys: [Keycode] = []

    static let zero = KeyboardReport()

    var data: Data {
        var bytes = [UInt8](repeating: 0, count: 8)
        bytes[0] = modifiers.rawValue
        // bytes[1] reserved
        if keys.count > 6 {
            // Standard six-key rollover report: never pretend an arbitrary subset is held.
            for i in 2 ..< 8 {
                bytes[i] = 0x01
            }
        } else {
            for (i, key) in keys.enumerated() {
                bytes[2 + i] = key.rawValue
            }
        }
        return Data(bytes)
    }
}

struct KeyboardModifiers: OptionSet, Sendable, Equatable {
    let rawValue: UInt8
    static let leftCtrl = KeyboardModifiers(rawValue: 1 << 0)
    static let leftShift = KeyboardModifiers(rawValue: 1 << 1)
    static let leftAlt = KeyboardModifiers(rawValue: 1 << 2)
    static let leftGUI = KeyboardModifiers(rawValue: 1 << 3)
    static let rightCtrl = KeyboardModifiers(rawValue: 1 << 4)
    static let rightShift = KeyboardModifiers(rawValue: 1 << 5)
    static let rightAlt = KeyboardModifiers(rawValue: 1 << 6)
    static let rightGUI = KeyboardModifiers(rawValue: 1 << 7)
}

struct KeyboardLEDs: OptionSet, Sendable, Equatable {
    let rawValue: UInt8
    static let numLock = KeyboardLEDs(rawValue: 1 << 0)
    static let capsLock = KeyboardLEDs(rawValue: 1 << 1)
    static let scrollLock = KeyboardLEDs(rawValue: 1 << 2)
    static let compose = KeyboardLEDs(rawValue: 1 << 3)
    static let kana = KeyboardLEDs(rawValue: 1 << 4)

    init(rawValue: UInt8) {
        self.rawValue = rawValue
    }

    init(byte: UInt8) {
        rawValue = byte & 0x1F
    }
}

/// USB HID usage page 0x07
struct Keycode: RawRepresentable, Sendable, Equatable, Hashable {
    let rawValue: UInt8

    static let a = Keycode(rawValue: 0x04)
    static let b = Keycode(rawValue: 0x05)
    static let c = Keycode(rawValue: 0x06)
    static let d = Keycode(rawValue: 0x07)
    static let e = Keycode(rawValue: 0x08)
    static let f = Keycode(rawValue: 0x09)
    static let g = Keycode(rawValue: 0x0A)
    static let h = Keycode(rawValue: 0x0B)
    static let i = Keycode(rawValue: 0x0C)
    static let j = Keycode(rawValue: 0x0D)
    static let k = Keycode(rawValue: 0x0E)
    static let l = Keycode(rawValue: 0x0F)
    static let m = Keycode(rawValue: 0x10)
    static let n = Keycode(rawValue: 0x11)
    static let o = Keycode(rawValue: 0x12)
    static let p = Keycode(rawValue: 0x13)
    static let q = Keycode(rawValue: 0x14)
    static let r = Keycode(rawValue: 0x15)
    static let s = Keycode(rawValue: 0x16)
    static let t = Keycode(rawValue: 0x17)
    static let u = Keycode(rawValue: 0x18)
    static let v = Keycode(rawValue: 0x19)
    static let w = Keycode(rawValue: 0x1A)
    static let x = Keycode(rawValue: 0x1B)
    static let y = Keycode(rawValue: 0x1C)
    static let z = Keycode(rawValue: 0x1D)
    static let digit1 = Keycode(rawValue: 0x1E)
    static let digit2 = Keycode(rawValue: 0x1F)
    static let digit3 = Keycode(rawValue: 0x20)
    static let digit4 = Keycode(rawValue: 0x21)
    static let digit5 = Keycode(rawValue: 0x22)
    static let digit6 = Keycode(rawValue: 0x23)
    static let digit7 = Keycode(rawValue: 0x24)
    static let digit8 = Keycode(rawValue: 0x25)
    static let digit9 = Keycode(rawValue: 0x26)
    static let digit0 = Keycode(rawValue: 0x27)
    static let `return` = Keycode(rawValue: 0x28)
    static let escape = Keycode(rawValue: 0x29)
    static let backspace = Keycode(rawValue: 0x2A)
    static let tab = Keycode(rawValue: 0x2B)
    static let space = Keycode(rawValue: 0x2C)
    static let minus = Keycode(rawValue: 0x2D)
    static let equal = Keycode(rawValue: 0x2E)
    static let leftBracket = Keycode(rawValue: 0x2F)
    static let rightBracket = Keycode(rawValue: 0x30)
    static let nonUSHash = Keycode(rawValue: 0x32)
    static let help = Keycode(rawValue: 0x75)
    static let menu = Keycode(rawValue: 0x76)
    static let backslash = Keycode(rawValue: 0x31)
    static let semicolon = Keycode(rawValue: 0x33)
    static let quote = Keycode(rawValue: 0x34)
    static let grave = Keycode(rawValue: 0x35)
    static let comma = Keycode(rawValue: 0x36)
    static let period = Keycode(rawValue: 0x37)
    static let slash = Keycode(rawValue: 0x38)
    static let capsLock = Keycode(rawValue: 0x39)
    static let f1 = Keycode(rawValue: 0x3A)
    static let f2 = Keycode(rawValue: 0x3B)
    static let f3 = Keycode(rawValue: 0x3C)
    static let f4 = Keycode(rawValue: 0x3D)
    static let f5 = Keycode(rawValue: 0x3E)
    static let f6 = Keycode(rawValue: 0x3F)
    static let f7 = Keycode(rawValue: 0x40)
    static let f8 = Keycode(rawValue: 0x41)
    static let f9 = Keycode(rawValue: 0x42)
    static let f10 = Keycode(rawValue: 0x43)
    static let f11 = Keycode(rawValue: 0x44)
    static let f12 = Keycode(rawValue: 0x45)
    static let printScreen = Keycode(rawValue: 0x46)
    static let scrollLock = Keycode(rawValue: 0x47)
    static let pause = Keycode(rawValue: 0x48)
    static let insert = Keycode(rawValue: 0x49)
    static let home = Keycode(rawValue: 0x4A)
    static let pageUp = Keycode(rawValue: 0x4B)
    static let deleteForward = Keycode(rawValue: 0x4C)
    static let end = Keycode(rawValue: 0x4D)
    static let pageDown = Keycode(rawValue: 0x4E)
    static let rightArrow = Keycode(rawValue: 0x4F)
    static let leftArrow = Keycode(rawValue: 0x50)
    static let downArrow = Keycode(rawValue: 0x51)
    static let upArrow = Keycode(rawValue: 0x52)
    static let numLock = Keycode(rawValue: 0x53)
    static let keypadDivide = Keycode(rawValue: 0x54)
    static let keypadMultiply = Keycode(rawValue: 0x55)
    static let keypadMinus = Keycode(rawValue: 0x56)
    static let keypadPlus = Keycode(rawValue: 0x57)
    static let keypadEnter = Keycode(rawValue: 0x58)
    static let keypad1 = Keycode(rawValue: 0x59)
    static let keypad2 = Keycode(rawValue: 0x5A)
    static let keypad3 = Keycode(rawValue: 0x5B)
    static let keypad4 = Keycode(rawValue: 0x5C)
    static let keypad5 = Keycode(rawValue: 0x5D)
    static let keypad6 = Keycode(rawValue: 0x5E)
    static let keypad7 = Keycode(rawValue: 0x5F)
    static let keypad8 = Keycode(rawValue: 0x60)
    static let keypad9 = Keycode(rawValue: 0x61)
    static let keypad0 = Keycode(rawValue: 0x62)
    static let keypadDecimal = Keycode(rawValue: 0x63)
    static let nonUSBackslash = Keycode(rawValue: 0x64)
    static let application = Keycode(rawValue: 0x65)
    static let power = Keycode(rawValue: 0x66)
    static let keypadEqual = Keycode(rawValue: 0x67)
    static let f13 = Keycode(rawValue: 0x68)
    static let f14 = Keycode(rawValue: 0x69)
    static let f15 = Keycode(rawValue: 0x6A)
    static let f16 = Keycode(rawValue: 0x6B)
    static let f17 = Keycode(rawValue: 0x6C)
    static let f18 = Keycode(rawValue: 0x6D)
    static let f19 = Keycode(rawValue: 0x6E)
    static let f20 = Keycode(rawValue: 0x6F)
    static let f21 = Keycode(rawValue: 0x70)
    static let f22 = Keycode(rawValue: 0x71)
    static let f23 = Keycode(rawValue: 0x72)
    static let f24 = Keycode(rawValue: 0x73)
    static let mute = Keycode(rawValue: 0x7F)
    static let volumeUp = Keycode(rawValue: 0x80)
    static let volumeDown = Keycode(rawValue: 0x81)
    static let keypadComma = Keycode(rawValue: 0x85)
    static let international1 = Keycode(rawValue: 0x87)
    static let international3 = Keycode(rawValue: 0x89)
    static let lang1 = Keycode(rawValue: 0x90)
    static let lang2 = Keycode(rawValue: 0x91)
}

struct SystemControlReport: Sendable, Equatable {
    var actions: SystemActions = []

    static let zero = SystemControlReport()

    var data: Data {
        Data([actions.rawValue])
    }
}

struct SystemActions: OptionSet, Sendable, Equatable {
    let rawValue: UInt8
    static let powerDown = SystemActions(rawValue: 1 << 0)
    static let sleep = SystemActions(rawValue: 1 << 1)
    static let coldRestart = SystemActions(rawValue: 1 << 2)
    static let displayToggle = SystemActions(rawValue: 1 << 3)
    static let displayLCDAutoscale = SystemActions(rawValue: 1 << 4)
    static let mainMenu = SystemActions(rawValue: 1 << 5)
    static let appMenu = SystemActions(rawValue: 1 << 6)
    static let displayBrightnessDecrement = SystemActions(rawValue: 1 << 7)
}

/// consumer report (5 bytes)
struct ConsumerReport: Sendable, Equatable {
    var key: ConsumerKey = .none
    var acUsageA: UInt8 = 0
    var acUsageB: UInt8 = 0
    var sub: UInt8 = 0

    static let zero = ConsumerReport()

    var data: Data {
        let raw = key.rawValue
        return Data([
            UInt8(raw & 0xFF),
            UInt8(raw >> 8),
            acUsageA,
            acUsageB,
            sub
        ])
    }
}

/// USB HID usage page 0x0C
enum ConsumerKey: UInt16, Sendable, Equatable {
    case none = 0x0000
    case playPause = 0x00CD
    case scanNext = 0x00B5
    case scanPrev = 0x00B6
    case stop = 0x00B7
    case rewind = 0x00B4
    case fastForward = 0x00B3
    case mute = 0x00E2
    case volumeUp = 0x00E9
    case volumeDown = 0x00EA
    case channelUp = 0x009C
    case channelDown = 0x009D
    case closedCaption = 0x0061
    case menu = 0x0040
    case menuPick = 0x0041
    case menuUp = 0x0042
    case menuDown = 0x0043
    case menuLeft = 0x0044
    case menuRight = 0x0045
    case power = 0x0030
    case brightnessUp = 0x006F, brightnessDown = 0x0070
    case illuminationUp = 0x0079, illuminationDown = 0x007A, illuminationToggle = 0x007C
    case eject = 0x00B8
}

extension ConsumerReport {
    static let acHome = ConsumerReport(acUsageB: 0x23)
    static let acBack = ConsumerReport(acUsageB: 0x24)
}
