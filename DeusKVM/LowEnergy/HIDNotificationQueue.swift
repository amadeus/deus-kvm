import Foundation

/// Preserve key/button/consumer transitions under Bluetooth backpressure. Adjacent
/// pure-motion reports merge into one pending report whose deltas are summed, so
/// waiting for the link loses no travel within the report's signed 8-bit range.
struct HIDNotificationQueue<Target: Equatable> {
    struct Entry {
        let data: Data
        let target: Target
        let coalesceMotion: Bool
    }

    private var entries: [Entry] = []
    private var head = 0

    var first: Entry? {
        head < entries.count ? entries[head] : nil
    }

    var count: Int {
        entries.count - head
    }

    /// Returns true when the report merged into the pending motion report.
    @discardableResult
    mutating func append(_ data: Data, target: Target, coalesceMotion: Bool = false) -> Bool {
        if head < entries.count, coalesceMotion, let last = entries.last, last.coalesceMotion, last.target == target {
            entries[entries.count - 1] = Entry(data: Self.accumulate(last.data, data), target: target, coalesceMotion: true)
            return true
        }
        entries.append(Entry(data: data, target: target, coalesceMotion: coalesceMotion))
        return false
    }

    /// Mouse report layout: buttons, X, Y, wheel, pan. Callers only coalesce reports
    /// with unchanged buttons and no scroll, so only the relative X/Y bytes add up.
    static func accumulate(_ pending: Data, _ next: Data) -> Data {
        guard pending.count >= 3, next.count >= 3 else { return next }
        var merged = Data(next)
        for axis in 1 ... 2 {
            let sum = Int(Int8(bitPattern: pending[pending.startIndex + axis])) + Int(Int8(bitPattern: merged[axis]))
            merged[axis] = UInt8(bitPattern: Int8(max(-127, min(127, sum))))
        }
        return merged
    }

    mutating func removeFirst() {
        guard head < entries.count else { return }
        head += 1
        if head == entries.count {
            removeAll()
        } else if head >= 64, head >= entries.count / 2 {
            entries.removeFirst(head)
            head = 0
        }
    }

    mutating func removeAll() {
        entries.removeAll(keepingCapacity: true)
        head = 0
    }
}
