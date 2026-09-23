import CoreGraphics

enum FinderPasteGesture {
    static func matches(key: Int64, flags: CGEventFlags, local: Bool, revisions: (offer: Int, current: Int), bundle: String?) -> Bool {
        key == 9 && local && revisions.offer == revisions.current && bundle == "com.apple.finder" &&
            flags.intersection([.maskCommand, .maskControl, .maskAlternate, .maskShift]) == .maskCommand
    }
}
