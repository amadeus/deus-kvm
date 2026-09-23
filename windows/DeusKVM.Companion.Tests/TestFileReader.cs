using DeusKVM.Companion.Core;
namespace DeusKVM.Companion.Tests;
internal sealed class TestFileReader(Func<uint, byte[]> read, Action? cancel = null) : IFileBlockReader
{
    public byte[] Read(uint offset) => read(offset);
    public void Dispose() => cancel?.Invoke();
}
