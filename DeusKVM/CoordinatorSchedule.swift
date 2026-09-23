import Foundation

/// Ready local capture reacts to events; only blocked recovery or remote safety needs polling.
enum CoordinatorSchedule {
    static func pollInterval(enabled: Bool, remote: Bool, permission: Bool, secure: Bool, tapReady: Bool) -> TimeInterval? {
        guard enabled else { return nil }
        if remote { return 0.5 }
        return !permission || secure || !tapReady ? 1 : nil
    }

    static func linkDeadline(lastSeen: TimeInterval, now: TimeInterval) -> TimeInterval? {
        let deadline = lastSeen + 10
        return deadline > now ? deadline : nil
    }
}
