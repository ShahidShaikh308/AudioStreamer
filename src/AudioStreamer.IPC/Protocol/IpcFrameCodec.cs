using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using AudioStreamer.IPC.Contracts;

namespace AudioStreamer.IPC.Protocol;

public sealed class IpcProtocolException(string code, string safeMessage, bool shouldCloseConnection = true) : Exception(safeMessage)
{
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
    public bool ShouldCloseConnection { get; } = shouldCloseConnection;
}

/// <summary>Length-prefixed JSON framing from IPC_PROTOCOL.md. One reader and one writer may use a stream concurrently.</summary>
public static class IpcFrameCodec
{
    public static async ValueTask<IpcEnvelope?> ReadAsync(Stream stream, CancellationToken cancellationToken,
        TimeSpan incompleteFrameTimeout)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(uint)];
        var firstByte = new byte[1];
        var firstCount = await stream.ReadAsync(firstByte, cancellationToken).ConfigureAwait(false);
        if (firstCount == 0) return null;

        using var frameTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        frameTimeout.CancelAfter(incompleteFrameTimeout);
        header[0] = firstByte[0];
        try
        {
            await ReadExactlyAsync(stream, header.AsMemory(1), frameTimeout.Token).ConfigureAwait(false);

            var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (length is < 2 or > IpcProtocol.MaximumFrameBytes)
                throw new IpcProtocolException(IpcErrors.InvalidRequest, "Frame length is outside the supported bounds.");

            var body = ArrayPool<byte>.Shared.Rent(checked((int)length));
            try
            {
                await ReadExactlyAsync(stream, body.AsMemory(0, (int)length), frameTimeout.Token).ConfigureAwait(false);
                try
                {
                    return JsonSerializer.Deserialize<IpcEnvelope>(body.AsSpan(0, (int)length), IpcProtocol.JsonOptions)
                           ?? throw new JsonException("Envelope was null.");
                }
                catch (JsonException)
                {
                    throw new IpcProtocolException(IpcErrors.InvalidRequest, "JSON envelope is malformed.");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(body, clearArray: true);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IpcProtocolException(IpcErrors.InvalidRequest, "Frame was incomplete beyond the configured deadline.");
        }
    }

    public static async ValueTask WriteAsync(Stream stream, IpcEnvelope envelope, CancellationToken cancellationToken,
        ArrayBufferWriter<byte>? reusableBuffer = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(envelope);
        var buffer = reusableBuffer ?? new ArrayBufferWriter<byte>(512);
        buffer.Clear();
        using (var writer = new Utf8JsonWriter(buffer))
            JsonSerializer.Serialize(writer, envelope, IpcProtocol.JsonOptions);

        if (buffer.WrittenCount is < 2 or > IpcProtocol.MaximumFrameBytes)
            throw new IpcProtocolException(IpcErrors.InvalidRequest, "Serialized message exceeds the frame limit.");

        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)buffer.WrittenCount);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Pipe closed inside a frame.");
            offset += read;
        }
    }
}
