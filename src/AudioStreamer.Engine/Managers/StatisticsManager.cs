using System.Collections.Immutable;
using System.Diagnostics;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Engine.Runtime;

namespace AudioStreamer.Engine.Managers;

internal enum StatisticCounter
{
    PacketsSent,
    PacketsReceived,
    FramesCaptured,
    FramesPlayed,
    BytesSent,
    BytesReceived,
    LatencyMicrosecondsTotal,
    LatencySamples,
    DroppedPackets,
    CaptureDrops,
    PlaybackUnderruns,
    QueueDepthFrames,
    QueueCapacityFrames,
    InvalidPackets,
    QueueDepthPackets,
    QueueDurationMilliseconds,
    PacketReordered,
    PacketMissing,
    PacketRecovered,
    JitterBufferMilliseconds
}

/// <summary>Owns bounded aggregate counters. Hot-path producers update fixed counters, never per-packet events.</summary>
internal sealed class StatisticsManager(EngineEventBus events)
{
    private readonly object _sync = new();
    private readonly long[] _counters = new long[Enum.GetValues<StatisticCounter>().Length];
    private long _lastRateSample = Stopwatch.GetTimestamp();
    private long _lastSentBytes;
    private long _lastReceivedBytes;
    private long _sendBytesPerSecond;
    private long _receivedBytesPerSecond;

    public StatisticsSnapshot Current => CreateSnapshot();

    public async ValueTask PublishCurrentAsync(
    EngineStatus status,
    CancellationToken cancellationToken)
    {
        await events.PublishAsync(
            "statistics",
            "StatisticsChanged",
            status,
            new StatisticsChangedPayload(Current),
            cancellationToken);
    }
    public async ValueTask RecordAsync(StatisticCounter counter, long amount, EngineStatus status,
        CancellationToken cancellationToken)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        StatisticsSnapshot snapshot;
        lock (_sync)
        {
            var index = (int)counter;
            _counters[index] = checked(_counters[index] + amount);
            snapshot = CreateSnapshotUnsafe();
        }
        await events.PublishAsync("statistics", "StatisticsChanged", status,
            new StatisticsChangedPayload(snapshot), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetGaugeAsync(StatisticCounter gauge, long value, EngineStatus status,
        CancellationToken cancellationToken)
    {
        if (gauge is not (StatisticCounter.QueueDepthFrames or StatisticCounter.QueueCapacityFrames or
            StatisticCounter.QueueDepthPackets or StatisticCounter.QueueDurationMilliseconds or StatisticCounter.JitterBufferMilliseconds))
            throw new ArgumentException("Only queue gauges can be assigned directly.", nameof(gauge));
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        StatisticsSnapshot snapshot;
        lock (_sync)
        {
            _counters[(int)gauge] = value;
            snapshot = CreateSnapshotUnsafe();
        }
        await events.PublishAsync("statistics", "StatisticsChanged", status,
            new StatisticsChangedPayload(snapshot), cancellationToken).ConfigureAwait(false);
    }

    private StatisticsSnapshot CreateSnapshot()
    {
        lock (_sync) return CreateSnapshotUnsafe();
    }

    private StatisticsSnapshot CreateSnapshotUnsafe()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastRateSample, now);
        if (elapsed >= TimeSpan.FromSeconds(1))
        {
            var sent = _counters[(int)StatisticCounter.BytesSent];
            var received = _counters[(int)StatisticCounter.BytesReceived];
            _sendBytesPerSecond = (long)((sent - _lastSentBytes) / elapsed.TotalSeconds);
            _receivedBytesPerSecond = (long)((received - _lastReceivedBytes) / elapsed.TotalSeconds);
            _lastSentBytes = sent;
            _lastReceivedBytes = received;
            _lastRateSample = now;
        }
        var builder = ImmutableDictionary.CreateBuilder<string, long>(StringComparer.Ordinal);
        foreach (var counter in Enum.GetValues<StatisticCounter>())
            builder.Add(ToMetricName(counter), _counters[(int)counter]);
        var latencyCount = _counters[(int)StatisticCounter.LatencySamples];
        builder["latency_average_microseconds"] = latencyCount == 0 ? 0 :
            _counters[(int)StatisticCounter.LatencyMicrosecondsTotal] / latencyCount;
        builder["throughput_sent_bytes_per_second"] = _sendBytesPerSecond;
        builder["throughput_received_bytes_per_second"] = _receivedBytesPerSecond;
        return new StatisticsSnapshot(DateTimeOffset.UtcNow, builder.ToImmutable());
    }

    private static string ToMetricName(StatisticCounter counter) => counter switch
    {
        StatisticCounter.PacketsSent => "packets_sent",
        StatisticCounter.PacketsReceived => "packets_received",
        StatisticCounter.FramesCaptured => "frames_captured",
        StatisticCounter.FramesPlayed => "frames_played",
        StatisticCounter.BytesSent => "bytes_sent",
        StatisticCounter.BytesReceived => "bytes_received",
        StatisticCounter.LatencyMicrosecondsTotal => "latency_microseconds_total",
        StatisticCounter.LatencySamples => "latency_samples",
        StatisticCounter.DroppedPackets => "dropped_packets",
        StatisticCounter.CaptureDrops => "capture_drops",
        StatisticCounter.PlaybackUnderruns => "playback_underruns",
        StatisticCounter.QueueDepthFrames => "queue_depth_frames",
        StatisticCounter.QueueCapacityFrames => "queue_capacity_frames",
        StatisticCounter.InvalidPackets => "invalid_packets",
        StatisticCounter.QueueDepthPackets => "queue_depth_packets",
        StatisticCounter.QueueDurationMilliseconds => "queue_duration_milliseconds",
        StatisticCounter.PacketReordered => "packet_reordered",
        StatisticCounter.PacketMissing => "packet_missing",
        StatisticCounter.PacketRecovered => "packet_recovered",
        StatisticCounter.JitterBufferMilliseconds => "jitter_buffer_milliseconds",
        _ => throw new ArgumentOutOfRangeException(nameof(counter))
    };
}
