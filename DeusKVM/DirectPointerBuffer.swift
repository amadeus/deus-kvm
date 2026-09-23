import Foundation

/// Cumulative fixed-point travel lets unsent motion coalesce without losing distance.
struct DirectPointerBuffer {
    struct Sample {
        var x: Int32
        var y: Int32
        var buttons: UInt8
        var wheel: Int8
        var pan: Int8
        var barrier: Bool

        func payload(id: UInt8, serial: UInt8) -> Data {
            var data = Data([id])
            data.append(CompanionProtocol.u32(UInt32(bitPattern: x)))
            data.append(CompanionProtocol.u32(UInt32(bitPattern: y)))
            data.append(contentsOf: [buttons, UInt8(bitPattern: wheel), UInt8(bitPattern: pan), serial])
            return data
        }
    }

    private var x: Int32 = 0
    private var y: Int32 = 0
    private var buttons: UInt8 = 0
    private(set) var pending: [Sample] = []

    mutating func append(dx: Double, dy: Double, report: MouseReport) -> Bool {
        guard dx.isFinite, dy.isFinite else { return false }
        x &+= Int32((min(1_000_000, max(-1_000_000, dx)) * 256).rounded())
        y &+= Int32((min(1_000_000, max(-1_000_000, dy)) * 256).rounded())
        let barrier = buttons != report.buttons.rawValue || report.wheel != 0 || report.pan != 0
        buttons = report.buttons.rawValue
        let sample = Sample(x: x, y: y, buttons: buttons, wheel: report.wheel, pan: report.pan, barrier: barrier)
        if !barrier, pending.last?.barrier == false { pending[pending.count - 1] = sample } else { pending.append(sample) }
        return pending.count <= 64
    }

    mutating func take() -> Sample? {
        pending.isEmpty ? nil : pending.removeFirst()
    }
}
