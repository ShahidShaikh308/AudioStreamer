using System.Buffers.Binary;

namespace AudioStreamer.Audio.Transport.Protocol;

/// <summary>Wire-compatible Audio v3 codec; field offsets and validation mirror docs/PROTOCOL.md and Python oracle.</summary>
public static class AudioProtocolV3Codec
{
    public const int HeaderSize = 25;
    public const int MaximumDatagramSize = 1200;
    public const int MaximumPayloadSize = MaximumDatagramSize - HeaderSize;
    public const byte StereoChannelCount = 2;
    public const byte Float32LittleEndianSampleFormat = 1;
    public const int BytesPerSample = sizeof(float);
    public const int StereoFloat32FrameSize = StereoChannelCount * BytesPerSample;
    private const uint Magic = 0x41534155;
    private const byte Version = 3;

    public static int Serialize(AudioProtocolV3Packet packet, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(packet);
        Validate(packet, packet.Payload.Length);
        var required = HeaderSize + packet.Payload.Length;
        if (destination.Length < required) throw new ArgumentException("Destination is too small for the packet.", nameof(destination));
        BinaryPrimitives.WriteUInt32BigEndian(destination, Magic);
        destination[4] = Version;
        BinaryPrimitives.WriteUInt32BigEndian(destination[5..], packet.SequenceNumber);
        BinaryPrimitives.WriteUInt64BigEndian(destination[9..], packet.StreamId);
        BinaryPrimitives.WriteUInt32BigEndian(destination[17..], checked((uint)packet.SampleRate));
        destination[21] = packet.Channels;
        destination[22] = packet.SampleFormat;
        BinaryPrimitives.WriteUInt16BigEndian(destination[23..], checked((ushort)packet.Payload.Length));
        packet.Payload.Span.CopyTo(destination[HeaderSize..required]);
        return required;
    }

    public static AudioProtocolV3Packet Parse(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length > MaximumDatagramSize) throw new AudioProtocolException("Datagram exceeds the 1200-byte maximum.");
        if (datagram.Length < HeaderSize) throw new AudioProtocolException("Datagram is shorter than the 25-byte header.");
        if (BinaryPrimitives.ReadUInt32BigEndian(datagram) != Magic) throw new AudioProtocolException("Invalid protocol magic.");
        if (datagram[4] != Version) throw new AudioProtocolException($"Unsupported protocol version: {datagram[4]}.");
        var sequence = BinaryPrimitives.ReadUInt32BigEndian(datagram[5..]);
        var streamId = BinaryPrimitives.ReadUInt64BigEndian(datagram[9..]);
        var sampleRate = BinaryPrimitives.ReadUInt32BigEndian(datagram[17..]);
        if (sampleRate is < 1 or > int.MaxValue) throw new AudioProtocolException("Sample rate must fit a positive signed 32-bit value.");
        var channels = datagram[21];
        var sampleFormat = datagram[22];
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(datagram[23..]);
        var payload = datagram[HeaderSize..];
        var packet = new AudioProtocolV3Packet(sequence, streamId, (int)sampleRate, channels, sampleFormat, payload.ToArray());
        Validate(packet, payloadLength);
        return packet;
    }

    private static void Validate(AudioProtocolV3Packet packet, int declaredPayloadLength)
    {
        if (packet.StreamId == 0) throw new AudioProtocolException("Stream ID zero is reserved.");
        if (packet.SampleRate is < 1 or > int.MaxValue) throw new AudioProtocolException("Sample rate must fit a positive signed 32-bit value.");
        if (packet.Channels != StereoChannelCount) throw new AudioProtocolException("Protocol v3 currently supports stereo only.");
        if (packet.SampleFormat != Float32LittleEndianSampleFormat) throw new AudioProtocolException("Protocol v3 supports FLOAT32_LE only.");
        if (declaredPayloadLength != packet.Payload.Length) throw new AudioProtocolException("Payload length does not match datagram size.");
        if (declaredPayloadLength is < 1 or > MaximumPayloadSize) throw new AudioProtocolException("Payload length is outside v3 limits.");
        if (declaredPayloadLength % StereoFloat32FrameSize != 0) throw new AudioProtocolException("Payload does not contain complete interleaved frames.");
        if (HeaderSize + declaredPayloadLength > MaximumDatagramSize) throw new AudioProtocolException("Datagram exceeds the 1200-byte maximum.");
    }
}
