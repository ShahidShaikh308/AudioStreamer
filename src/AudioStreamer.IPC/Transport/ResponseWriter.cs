using System.Buffers;
using System.Threading.Channels;
using AudioStreamer.IPC.Contracts;
using AudioStreamer.IPC.Protocol;

namespace AudioStreamer.IPC.Transport;

public sealed class ResponseWriter(Stream pipe)
{
    private readonly ArrayBufferWriter<byte> _buffer = new(512);

    public async Task RunAsync(ChannelReader<IpcEnvelope> messages, CancellationToken cancellationToken)
    {
        await foreach (var message in messages.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            await IpcFrameCodec.WriteAsync(pipe, message, cancellationToken, _buffer).ConfigureAwait(false);
    }

    public ValueTask WriteHandshakeAsync(IpcEnvelope message, CancellationToken cancellationToken) =>
        IpcFrameCodec.WriteAsync(pipe, message, cancellationToken, _buffer);
}
