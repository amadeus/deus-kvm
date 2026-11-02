namespace DeusKVM.Companion.Core;

public static class ClipboardWake
{
    // The clipboard listener, posted commands and lock changes wake the clipboard thread. The
    // short retry timer runs only while work that can fail transiently remains: a change not yet
    // read (another app held the clipboard), a write, file or image still pending, or a yield.
    public static bool NeedsRetry(bool enabled, bool accessible, bool unreadChange, bool pendingText,
        bool pendingFile, bool pendingImage, bool pendingYield) =>
        enabled && accessible && (unreadChange || pendingText || pendingFile || pendingImage || pendingYield);
}
