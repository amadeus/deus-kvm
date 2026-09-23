using System.Diagnostics;
using System.Security.Cryptography;
using DeusKVM.Companion.Core;
static class ReverseScenario
{
    public static async Task Run(string folder, string scenario)
    {
        if (scenario == "forward-large") { Forward(folder); return; }
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var request = FileClipboardOffer.Parse(File.ReadAllBytes(Path.Combine(folder, "request.json")));
        var sourcePath = Path.Combine(folder, "source.bin");
        using (var source = File.Create(sourcePath)) {
            source.SetLength(request.Size);
            if (request.Size > 0) { source.Position = request.Size - 3; source.Write([7, 8, 9]); }
        }
        var snapshot = ClipboardFileSource.Capture(sourcePath)!;
        if (scenario == "stale") using (var source = File.OpenWrite(sourcePath)) source.SetLength(request.Size + 1L);
        if (scenario == "wrong-key") request = request with { Network = request.Network! with { Key = Convert.ToBase64String(new byte[32]) } };
        var watch = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await NetworkFileSender.Send(request, snapshot.Read, timeout.Token); }
        catch (Exception error) when (scenario is "cancel" or "wrong-key" && error is IOException or CryptographicException) { }
        for (var i = 0; i < 100 && !File.Exists(Path.Combine(folder, "result")); i++) await Task.Delay(20);
        var result = File.ReadAllText(Path.Combine(folder, "result"));
        var output = Path.Combine(folder, "received.bin");
        if (scenario is "large" or "empty") {
            Check(result == "OK", result);
            Check(new FileInfo(output).Length == request.Size, "Wrong destination length");
            using var input = File.OpenRead(sourcePath); using var written = File.OpenRead(output);
            Check(SHA256.HashData(input).SequenceEqual(SHA256.HashData(written)), "SHA-256 mismatch");
        } else {
            Check(result != "OK", "Expected rejection");
            if (scenario == "collision") Check(File.ReadAllText(output) == "existing", "Existing destination overwritten");
            else Check(!File.Exists(output), "Failed transfer left destination");
        }
        Check(!Directory.EnumerateFiles(folder, "*.partial").Any(), "Partial file leaked");
        Console.WriteLine($"PASS reverse {scenario}: {request.Size} bytes, {watch.ElapsedMilliseconds} ms, working set {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024} MiB");
    }
    private static void Forward(string folder)
    {
        var offer = FileClipboardOffer.Parse(File.ReadAllBytes(Path.Combine(folder, "request.json")));
        using var session = new FileClipboardSession(offer);
        var stream = new RemoteFileStream(session, () => { });
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024]; var watch = Stopwatch.StartNew();
        for (var position = 0; position < offer.Size;) {
            var count = Math.Min(buffer.Length, offer.Size - position);
            stream.Read(buffer, count, IntPtr.Zero); hash.AppendData(buffer, 0, count); position += count;
        }
        using var source = File.OpenRead(Path.Combine(folder, "source.bin"));
        if (!hash.GetHashAndReset().SequenceEqual(SHA256.HashData(source))) throw new Exception("Forward 2 GB hash mismatch");
        Console.WriteLine($"PASS forward large: {offer.Size} bytes, {watch.ElapsedMilliseconds} ms, working set {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024} MiB");
    }

}
