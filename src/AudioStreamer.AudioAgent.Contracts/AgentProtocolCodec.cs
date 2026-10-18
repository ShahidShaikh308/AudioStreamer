using System.Buffers.Binary;

namespace AudioStreamer.AudioAgent.Contracts;

public static class AgentProtocolCodec
{
    private const int PcmHeaderLength = 40;

    public static byte[] EncodePcm(AgentPcmMetadata metadata, ReadOnlySpan<byte> pcm)
    {
        var result = new byte[checked(PcmHeaderLength + pcm.Length)];
        WritePcmHeader(result, metadata, pcm.Length);
        pcm.CopyTo(result.AsSpan(PcmHeaderLength));
        return result;
    }

    public static (AgentPcmMetadata Metadata, ReadOnlyMemory<byte> Pcm) DecodePcm(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < PcmHeaderLength || payload.Length > PcmHeaderLength + AgentProtocol.MaximumPcmPayloadLength)
            throw new InvalidDataException("Invalid PCM frame size.");
        var header = payload.Span[..PcmHeaderLength];
        var streamId = new Guid(header[..16]);
        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        var timestamp = BinaryPrimitives.ReadInt64LittleEndian(header[20..]);
        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(header[28..]);
        var channels = header[32];
        var sampleFormat = header[33] switch { 1 => "Float32", 2 => "Int16", _ => throw new InvalidDataException("Invalid PCM sample format.") };
        var frameCount = BinaryPrimitives.ReadInt32LittleEndian(header[34..]);
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[38..]) != 0 || streamId == Guid.Empty ||
            timestamp < 0 || sampleRate is not (44100 or 48000) || channels is not (1 or 2) || frameCount < 1)
            throw new InvalidDataException("Invalid PCM metadata.");
        var expected = checked(frameCount * channels * (sampleFormat == "Float32" ? 4 : 2));
        if (expected != payload.Length - PcmHeaderLength || expected > AgentProtocol.MaximumPcmPayloadLength)
            throw new InvalidDataException("PCM payload does not match frame metadata.");
        return (new AgentPcmMetadata(streamId, sequence, timestamp, sampleRate, channels, sampleFormat, frameCount),
            payload[PcmHeaderLength..]);
    }

    public static byte[] Encode(AgentFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var isPcm = frame.PcmMetadata is not null;
        var payloadLength = isPcm ? checked(PcmHeaderLength + frame.Payload.Length) : frame.Payload.Length;
        if (payloadLength > AgentProtocol.MaximumPayloadLength) throw new InvalidDataException("Agent payload exceeds the protocol limit.");
        var result = new byte[AgentProtocol.HeaderLength + payloadLength];
        var header = result.AsSpan(0, AgentProtocol.HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header, AgentProtocol.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], frame.Version);
        header[6] = (byte)frame.Channel;
        BinaryPrimitives.WriteUInt16LittleEndian(header[7..], (ushort)frame.MessageType);
        BinaryPrimitives.WriteUInt32LittleEndian(header[9..], frame.SequenceNumber);
        frame.CorrelationId.TryWriteBytes(header[13..29]);
        BinaryPrimitives.WriteInt32LittleEndian(header[29..], payloadLength);
        header[33..36].Clear();
        if (frame.PcmMetadata is { } metadata)
        {
            if ((frame.Channel, frame.MessageType) is not ((AgentChannel.CapturePcm, AgentMessageType.CapturePcm) or
                (AgentChannel.PlaybackPcm, AgentMessageType.PlaybackPcm)))
                throw new InvalidDataException("PCM metadata is only valid on PCM channel frames.");
            var pcmPayload = result.AsSpan(AgentProtocol.HeaderLength, payloadLength);
            WritePcmHeader(pcmPayload, metadata, frame.Payload.Length);
            frame.Payload.Span.CopyTo(pcmPayload[PcmHeaderLength..]);
        }
        else frame.Payload.Span.CopyTo(result.AsSpan(AgentProtocol.HeaderLength));
        return result;
    }

    private static void WritePcmHeader(Span<byte> header, AgentPcmMetadata metadata, int pcmLength)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.StreamId == Guid.Empty || metadata.TimestampTicks < 0 || metadata.SampleRate is not (44100 or 48000) ||
            metadata.Channels is not (1 or 2) || metadata.FrameCount < 1 ||
            metadata.SampleFormat is not ("Float32" or "Int16"))
            throw new InvalidDataException("Invalid PCM metadata.");
        var bytesPerSample = metadata.SampleFormat == "Float32" ? 4 : 2;
        var expected = checked(metadata.FrameCount * metadata.Channels * bytesPerSample);
        if (pcmLength != expected || pcmLength > AgentProtocol.MaximumPcmPayloadLength)
            throw new InvalidDataException("PCM payload length does not match its format metadata or exceeds the protocol limit.");
        var pcmHeader = header[..PcmHeaderLength];
        metadata.StreamId.TryWriteBytes(pcmHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(pcmHeader[16..], metadata.SequenceNumber);
        BinaryPrimitives.WriteInt64LittleEndian(pcmHeader[20..], metadata.TimestampTicks);
        BinaryPrimitives.WriteInt32LittleEndian(pcmHeader[28..], metadata.SampleRate);
        pcmHeader[32] = checked((byte)metadata.Channels);
        pcmHeader[33] = metadata.SampleFormat == "Float32" ? (byte)1 : (byte)2;
        BinaryPrimitives.WriteInt32LittleEndian(pcmHeader[34..], metadata.FrameCount);
        BinaryPrimitives.WriteUInt16LittleEndian(pcmHeader[38..], 0);
    }

    public static async ValueTask<AgentFrame> ReadAsync(Stream stream, AgentChannel expectedChannel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[AgentProtocol.HeaderLength];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var span = header.AsSpan();
        if (BinaryPrimitives.ReadUInt32LittleEndian(span) != AgentProtocol.Magic) throw new InvalidDataException("Invalid agent protocol magic.");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
        var channel = (AgentChannel)span[6];
        var type = (AgentMessageType)BinaryPrimitives.ReadUInt16LittleEndian(span[7..]);
        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(span[9..]);
        var correlationId = new Guid(span[13..29]);
        var length = BinaryPrimitives.ReadInt32LittleEndian(span[29..]);
        if (!Enum.IsDefined(channel) || channel != expectedChannel || !Enum.IsDefined(type)) throw new InvalidDataException("Invalid agent channel or message type.");
        if (length < 0 || length > AgentProtocol.MaximumPayloadLength) throw new InvalidDataException("Invalid agent payload length.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return new AgentFrame(version, channel, type, sequence, correlationId, payload);
    }

    public static ValueTask WriteAsync(Stream stream, AgentFrame frame, CancellationToken cancellationToken) =>
        stream.WriteAsync(Encode(frame), cancellationToken);
}
