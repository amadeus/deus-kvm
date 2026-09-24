namespace DeusKVM.Companion;

// Aggregate edge state only: no key contents, pointer coordinates, or device identifiers.
internal static class EdgeDiagnostics
{
    private static readonly object Gate = new();
    public static void Write(string value)
    {
        try
        {
            lock (Gate)
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeusKVM");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "edge-return.log");
                if (File.Exists(path) && new FileInfo(path).Length > 32768) RuntimeCompat.MoveReplace(path, path + ".previous");
                File.AppendAllText(path, $"{DateTime.UtcNow:O} {value}{Environment.NewLine}");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
