using System.Text.Json;

namespace DeusKVM.Companion.Core;

public static class DesktopPipe
{
    public const int MaximumMessageBytes = 131072; // 64 KiB clipboard as base64 plus metadata.
    public static async Task Pump(Stream pipe, System.Threading.Channels.ChannelReader<DesktopMessage> outgoing,
        Action<DesktopMessage> dispatch, CancellationToken stop)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var read = ReadMessages();
        var write = WriteMessages();
        try
        {
            var completed = await Task.WhenAny(read, write).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        finally
        {
            lifetime.Cancel();
            pipe.Dispose();
            try { await Task.WhenAll(read, write).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        }

        async Task ReadMessages()
        {
            while (!lifetime.IsCancellationRequested)
                dispatch(await Read(pipe, lifetime.Token).ConfigureAwait(false));
        }
        async Task WriteMessages()
        {
            await foreach (var message in outgoing.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
                await Write(pipe, message, lifetime.Token).ConfigureAwait(false);
        }
    }

    public static async Task<DesktopMessage> Read(Stream pipe, CancellationToken stop)
    {
        var header = new byte[4];
        await pipe.ReadExactlyAsync(header, stop);
        var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 1 or > MaximumMessageBytes) throw new InvalidDataException("Invalid desktop message size");
        var data = new byte[size];
        await pipe.ReadExactlyAsync(data, stop);
        return JsonSerializer.Deserialize<DesktopMessage>(data) ?? throw new InvalidDataException("Empty desktop message");
    }
    public static async Task Write(Stream pipe, DesktopMessage message, CancellationToken stop)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(message);
        if (data.Length > MaximumMessageBytes) throw new InvalidDataException("Desktop message too large");
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await pipe.WriteAsync(header, stop);
        await pipe.WriteAsync(data, stop);
        await pipe.FlushAsync(stop);
    }
}
