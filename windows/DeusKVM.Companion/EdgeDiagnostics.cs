namespace DeusKVM.Companion;

// Aggregate edge state, layout changes and rejected returns (screen sizes and cursor position as a
// fraction of the screen). No key contents, absolute pointer coordinates, or device identifiers.
// Written by the session agent and the service (both SYSTEM) next to service.log.
internal static class EdgeDiagnostics
{
    private static readonly object Gate = new();
    public static void Write(string value)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.DataDirectory);
                var path = Path.Combine(Paths.DataDirectory, "edge-return.log");
                if (File.Exists(path) && new FileInfo(path).Length > 32768) RuntimeCompat.MoveReplace(path, path + ".previous");
                File.AppendAllText(path, $"{DateTime.UtcNow:O} {value}{Environment.NewLine}");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
