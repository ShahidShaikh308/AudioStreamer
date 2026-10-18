using AudioStreamer.Engine.Contracts;
using AudioStreamer.Engine.Managers;

namespace AudioStreamer.Engine.Runtime;

/// <summary>Adapts audio-platform metadata events into the existing immutable Engine event stream.</summary>
internal sealed class EngineAudioPlatformEventPublisher(EngineEventBus events, NodeManager nodeManager,
    StatisticsManager statisticsManager) : IAudioPlatformEventPublisher
{
    public async ValueTask PublishAsync(AudioPlatformEvent audioEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioEvent);
        var counter = audioEvent.Kind switch
        {
            AudioPlatformEventKind.TransportPacketsSent => StatisticCounter.PacketsSent,
            AudioPlatformEventKind.TransportPacketsReceived => StatisticCounter.PacketsReceived,
            AudioPlatformEventKind.TransportBytesSent => StatisticCounter.BytesSent,
            AudioPlatformEventKind.TransportBytesReceived => StatisticCounter.BytesReceived,
            AudioPlatformEventKind.TransportDroppedPackets => StatisticCounter.DroppedPackets,
            AudioPlatformEventKind.TransportInvalidPackets => StatisticCounter.InvalidPackets,
            AudioPlatformEventKind.PacketReordered => StatisticCounter.PacketReordered,
            AudioPlatformEventKind.PacketMissing => StatisticCounter.PacketMissing,
            AudioPlatformEventKind.PacketRecovered => StatisticCounter.PacketRecovered,
            AudioPlatformEventKind.JitterUnderrun => StatisticCounter.PlaybackUnderruns,
            AudioPlatformEventKind.BufferOverrun => StatisticCounter.CaptureDrops,
            AudioPlatformEventKind.BufferUnderrun => StatisticCounter.PlaybackUnderruns,
            _ => (StatisticCounter?)null
        };
        if (audioEvent.Kind is AudioPlatformEventKind.TransportQueueDepthPackets or AudioPlatformEventKind.TransportQueueDurationMilliseconds or AudioPlatformEventKind.JitterBufferMilliseconds)
        {
            var gauge = audioEvent.Kind switch
            {
                AudioPlatformEventKind.TransportQueueDepthPackets => StatisticCounter.QueueDepthPackets,
                AudioPlatformEventKind.JitterBufferMilliseconds => StatisticCounter.JitterBufferMilliseconds,
                _ => StatisticCounter.QueueDurationMilliseconds
            };
            await statisticsManager.SetGaugeAsync(gauge, audioEvent.Count, nodeManager.Status, cancellationToken).ConfigureAwait(false);
        }
        else if (counter is { } selected && audioEvent.Count > 0)
            await statisticsManager.RecordAsync(selected, audioEvent.Count, nodeManager.Status, cancellationToken).ConfigureAwait(false);
        await events.PublishAsync("audio-platform", audioEvent.Kind.ToString(), nodeManager.Status,
            new AudioPlatformChangedPayload(audioEvent), cancellationToken).ConfigureAwait(false);
    }
}
