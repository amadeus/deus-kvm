using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeusKVM.Bootstrap;

if (args.Length != 3) throw new ArgumentException("Usage: VerifyBundle <launcher.exe> <payload.zip> <payload-directory>");
using var input = File.OpenRead(args[0]);
using var pe = new PEReader(input);
if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64) throw new InvalidDataException("Launcher is not Windows x64");
var metadata = pe.GetMetadataReader();
var allowed = new HashSet<string> { "mscorlib", "System", "System.Core", "System.Windows.Forms", "System.Drawing", "System.IO.Compression" };
var references = metadata.AssemblyReferences.Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name)).ToArray();
if (references.Any(name => !allowed.Contains(name)))
    throw new InvalidDataException("Launcher requires a non-OS assembly: " + string.Join(", ", references));
var resources = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress).GetContent();
byte[] Resource(string name)
{
    var resource = metadata.ManifestResources.Select(metadata.GetManifestResource).Single(r => metadata.GetString(r.Name) == name);
    if (!resource.Implementation.IsNil) throw new InvalidDataException("Payload is not embedded");
    var offset = checked((int)resource.Offset);
    var length = BinaryPrimitives.ReadInt32LittleEndian(resources.AsSpan(offset, 4));
    return resources.AsSpan(offset + 4, length).ToArray();
}
var zip = Resource("DeusKVM.Payload.zip");
if (!zip.AsSpan().SequenceEqual(File.ReadAllBytes(args[1]))) throw new InvalidDataException("Embedded payload differs from packaged build");
var digest = Encoding.UTF8.GetString(Resource("DeusKVM.Payload.sha256")).Trim();
var directory = Path.Combine(Path.GetTempPath(), "deuskvm-verify-bundle-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    // Execute the exact extraction code compiled into the Windows launcher on this host.
    using var archive = new MemoryStream(zip);
    BundlePayload.Extract(archive, digest, directory);
    var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(directory, "package.json")))!;
    foreach (var (name, hash) in manifest)
    {
        var bytes = File.ReadAllBytes(Path.Combine(directory, name));
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), hash, StringComparison.OrdinalIgnoreCase) ||
            !bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(args[2], name))))
            throw new InvalidDataException("Extracted runtime differs: " + name);
    }
    Console.WriteLine($"Verified Windows x64 launcher, {manifest.Count} extracted runtime files, embedded ZIP/hash, OS-only references.");
    Console.WriteLine($"References: {string.Join(", ", references)}");
    Console.WriteLine($"EXE: {input.Length:N0} bytes; SHA-256: {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))).ToLowerInvariant()}");
}
finally { Directory.Delete(directory, true); }
