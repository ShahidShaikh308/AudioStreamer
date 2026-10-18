namespace AudioStreamer.Audio.Transport.Protocol;

/// <summary>Validated Audio Protocol v3 datagram metadata and its immutable PCM payload.</summary>
public sealed record AudioProtocolV3Packet(uint SequenceNumber, ulong StreamId, int SampleRate,
    byte Channels, byte SampleFormat, ReadOnlyMemory<byte> Payload);

public sealed class AudioProtocolException(string message) : FormatException(message);
