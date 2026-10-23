import AppKit
import CoreGraphics

/// Names the process macOS attributes Secure Input to (a browser with a focused password field, say).
enum SecureInputHolder {
    static func name() -> String? {
        guard let session = CGSessionCopyCurrentDictionary() as? [String: Any],
              let pid = (session["kCGSSessionSecureInputPID"] as? NSNumber)?.int32Value, pid > 0
        else { return nil }
        if let app = NSRunningApplication(processIdentifier: pid), let name = app.localizedName { return name }
        var buffer = [CChar](repeating: 0, count: 256)
        guard proc_name(pid, &buffer, UInt32(buffer.count)) > 0 else { return "process \(pid)" }
        return String(cString: buffer)
    }
}
