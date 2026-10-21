import Foundation

/// Materialize a validated, cached screenshot only when the user pastes in Finder.
enum ClipboardImageFile {
    static func write(_ bytes: Data, to destination: URL, canAccess: () -> Bool) -> String? {
        guard canAccess(), !bytes.isEmpty, bytes.count <= ClipboardFile.maximumImageBytes else { return "Image paste canceled." }
        let temporary = destination.deletingLastPathComponent().appendingPathComponent(".deuskvm-\(UUID().uuidString).partial")
        let descriptor = open(temporary.path, O_CREAT | O_EXCL | O_WRONLY, 0o600)
        guard descriptor >= 0 else { return "Cannot create a file in this Finder folder." }
        let handle = FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
        defer { try? handle.close(); try? FileManager.default.removeItem(at: temporary) }
        do {
            try handle.write(contentsOf: bytes)
            try handle.synchronize(); try handle.close()
            guard canAccess() else { return "Image paste canceled." }
            guard renamex_np(temporary.path, destination.path, UInt32(RENAME_EXCL)) == 0 else {
                return "Cannot finish the paste. A file with that name may already exist, or the folder may be read-only."
            }
            return nil
        } catch { return "The screenshot could not be written. Paste again to retry." }
    }
}
