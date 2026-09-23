using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DeusKVM.Companion.Core;

var folder = args[0];
if (args.Length > 1) { await ReverseScenario.Run(folder, args[1]); return; }
var offer = FileClipboardOffer.Parse(File.ReadAllBytes(Path.Combine(folder, "offer.json")));
void Check(bool value, string label) { if (!value) throw new Exception(label); }
using var session = new FileClipboardSession(offer);
var remote = new RemoteFileStream(session, () => { });
remote.Stat(out var stat, 0);
Check(stat.cbSize == offer.Size && !File.Exists(Path.Combine(folder, "read-started")), "Metadata fetched contents");
using (var wrong = new NetworkFileReader(offer with { Network = offer.Network! with { Key = Convert.ToBase64String(new byte[32]) } }))
{
    try { wrong.Read(0); throw new Exception("Wrong key accepted"); }
    catch (NetworkUnavailableException) { }
}
Check(!File.Exists(Path.Combine(folder, "read-started")), "Unauthenticated request read source");
var bytes = new byte[offer.Size]; var watch = Stopwatch.StartNew();
remote.Read(bytes, bytes.Length, IntPtr.Zero);
Check(bytes.Select((b, i) => b == i % 251).All(x => x), "Corrupt multi-block transfer");
Console.WriteLine($"Swift -> C# native stream: {bytes.Length} verified bytes in {watch.ElapsedMilliseconds} ms (same-machine socket, not hardware throughput)");
remote.Seek(0, 0, IntPtr.Zero); var again = new byte[4096]; remote.Read(again, again.Length, IntPtr.Zero);
Check(again.SequenceEqual(bytes.Take(again.Length)), "Seek failed");
using var revoked = new NetworkFileReader(offer);
Check(revoked.Read(0).SequenceEqual(bytes.Take(FileNetworkCrypto.MaximumBlock)), "Second connection failed");
File.AppendAllText(Path.Combine(folder, "source.bin"), "changed");
try { session.Read(0); throw new Exception("Stale source accepted"); }
catch (NetworkUnavailableException) { throw new Exception("Authenticated source failure downgraded"); }
catch (IOException) { }
File.WriteAllText(Path.Combine(folder, "revoke"), "");
for (var i = 0; i < 100 && !File.Exists(Path.Combine(folder, "revoked")); i++) await Task.Delay(20);
Check(File.Exists(Path.Combine(folder, "revoked")), "Server failed to revoke");
try { revoked.Read(0); throw new Exception("Revoked connection accepted"); }
catch (NetworkUnavailableException) { throw new Exception("Active transfer silently downgraded"); }
catch (IOException) { }
var listener = new TcpListener(IPAddress.Any, 0); listener.Start();
var endpoint = offer.Network! with { Port = ((IPEndPoint)listener.LocalEndpoint).Port };
using (var canceled = new FileClipboardSession(offer with { Network = endpoint }))
{
    var read = Task.Run(() => canceled.Read(0));
    using var accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
    var hello = new byte[32]; await accepted.GetStream().ReadExactlyAsync(hello);
    canceled.Dispose();
    try { await read.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("Cancellation succeeded unexpectedly"); }
    catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
}
listener.Stop();
using (var unavailable = new FileClipboardSession(offer with { Network = endpoint }))
{
    try { unavailable.Read(0); throw new Exception("Unavailable network succeeded"); }
    catch (NetworkUnavailableException) { }
}
Console.WriteLine("PASS: lazy content, wrong-key rejection, multi-block bytes, seek, changed source, revocation, cancellation, network-only failure");

var reverse = FileClipboardOffer.Parse(File.ReadAllBytes(Path.Combine(folder, "reverse-request.json")));
var sourcePath = Path.Combine(folder, "windows-source.bin"); File.WriteAllBytes(sourcePath, bytes);
var source = ClipboardFileSource.Capture(sourcePath)!;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
await NetworkFileSender.Send(reverse, source.Read, timeout.Token);
for (var i = 0; i < 100 && !File.Exists(Path.Combine(folder, "reverse-result")); i++) await Task.Delay(20);
Check(File.ReadAllText(Path.Combine(folder, "reverse-result")) == "OK", "Reverse receiver failed");
Check(File.ReadAllBytes(Path.Combine(folder, "reverse.bin")).SequenceEqual(bytes), "Reverse bytes differ");
Check(!Directory.EnumerateFiles(folder, "*.partial").Any(), "Partial output leaked");
Console.WriteLine("PASS: C# source -> Swift receiver, authenticated multi-block binary equality and partial-file cleanup");
