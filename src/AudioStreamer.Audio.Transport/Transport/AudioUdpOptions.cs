using System.Net;

namespace AudioStreamer.Audio.Transport.Transport;

/// <summary>Local receive endpoint and hard queue limits. Send endpoints are resolved from discovered peers.</summary>
public sealed record AudioUdpOptions(IPEndPoint BindEndpoint,
    int SendQueueCapacity, int ReceiveQueueCapacity, TimeSpan ReceiveBufferDuration)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(BindEndpoint);
        if (BindEndpoint.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(BindEndpoint));
        if (SendQueueCapacity is < 1 or > 4096 || ReceiveQueueCapacity is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(SendQueueCapacity));
        if (ReceiveBufferDuration <= TimeSpan.Zero || ReceiveBufferDuration > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(ReceiveBufferDuration));
    }
}

public sealed record AudioTransportSnapshot(long PacketsSent, long BytesSent, long PacketsReceived,
    long BytesReceived, long InvalidPackets, long DroppedPackets, long SocketErrors, long Reconnects,
    TimeSpan BufferedDuration, int QueueDepth, DateTimeOffset? LastPacketReceivedAt);
