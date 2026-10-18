using System.Buffers;
using System.Collections.Immutable;

namespace AudioStreamer.Engine.Contracts;

public enum PcmSampleFormat { Float32, Int16 }
public enum AudioCaptureSource { Microphone, Loopback }
public enum AudioDeviceChangeKind { Added, Removed, StateChanged, DefaultChanged, FormatChanged }
public enum AudioPlatformEventKind { DeviceAdded, DeviceRemoved, DefaultChanged, CaptureStarted, CaptureStopped, PlaybackStarted, PlaybackStopped, BufferUnderrun, BufferOverrun, DeviceLost, FormatChanged, Error, TransportPacketsSent, TransportPacketsReceived, TransportBytesSent, TransportBytesReceived, TransportDroppedPackets, TransportInvalidPackets, TransportQueueDepthPackets, TransportQueueDurationMilliseconds, JitterBufferMilliseconds, PacketReordered, PacketMissing, PacketRecovered, JitterUnderrun }

/// <summary>Validated interleaved PCM format shared by all platform adapters.</summary>
public readonly record struct AudioFormat(int SampleRate, int Channels, PcmSampleFormat SampleFormat, bool Interleaved = true)
{
    public int BytesPerSample => SampleFormat == PcmSampleFormat.Float32 ? sizeof(float) : sizeof(short);
    public int BytesPerFrame => checked(Channels * BytesPerSample);
    public bool IsSupported => SampleRate is 44100 or 48000 && Channels is 1 or 2 && Interleaved;
    public void EnsureSupported()
    {
        if (!IsSupported) throw new ArgumentOutOfRangeException(nameof(AudioFormat), "PCM format must be interleaved mono/stereo at 44.1 or 48 kHz using Float32 or Int16.");
    }
}

/// <summary>Owns one bounded pooled PCM frame. Consumers must dispose each frame exactly once.</summary>
public readonly struct AudioFrame : IDisposable
{
    private readonly IMemoryOwner<byte>? _owner;
    public AudioFrame(IMemoryOwner<byte> owner, int byteLength, AudioFormat format, int frameCount, long timestampTicks)
    {
        ArgumentNullException.ThrowIfNull(owner);
        format.EnsureSupported();
        if (frameCount < 1 || byteLength != checked(frameCount * format.BytesPerFrame) || byteLength > owner.Memory.Length)
            throw new ArgumentOutOfRangeException(nameof(byteLength));
        _owner = owner;
        ByteLength = byteLength;
        Format = format;
        FrameCount = frameCount;
        TimestampTicks = timestampTicks;
    }
    public AudioFormat Format { get; }
    public int FrameCount { get; }
    public int ByteLength { get; }
    public long TimestampTicks { get; }
    public TimeSpan Duration => TimeSpan.FromSeconds((double)FrameCount / Format.SampleRate);
    public ReadOnlyMemory<byte> Data => (_owner ?? throw new ObjectDisposedException(nameof(AudioFrame))).Memory[..ByteLength];
    internal Memory<byte> WritableData => (_owner ?? throw new ObjectDisposedException(nameof(AudioFrame))).Memory[..ByteLength];
    public void Dispose() => _owner?.Dispose();
}

public sealed record AudioCaptureOptions(string? DeviceId, AudioCaptureSource Source, AudioFormat Format, int QueueCapacity = 8);
public sealed record AudioPlaybackOptions(string? DeviceId, AudioFormat Format, int BufferDurationMilliseconds = 100, int RequestedLatencyMilliseconds = 50);
public sealed record AudioBufferPoolSnapshot(long Rents, long Returns, long Outstanding, long BytesRented);
public sealed record AudioLatencySnapshot(TimeSpan BufferedAudio, TimeSpan DeviceLatencyEstimate, long TimestampTicks);
public sealed record AudioDiagnosticsSnapshot(string CaptureState, string PlaybackState, long CapturedFrames,
    long PlayedFrames, long DroppedFrames, long Underruns, long Overruns, AudioLatencySnapshot Latency,
    ImmutableArray<string> RecentSafeErrors);

/// <summary>One optional, immutable metadata event. Never includes PCM or native handles.</summary>
public sealed record AudioPlatformEvent(AudioPlatformEventKind Kind, DateTimeOffset OccurredAt,
    string? DeviceId = null, long Count = 0, string? SafeErrorCode = null);

public interface IAudioCapture : IAsyncDisposable
{
    string State { get; }
    ValueTask StartAsync(AudioCaptureOptions options, CancellationToken cancellationToken);
    ValueTask PauseAsync(CancellationToken cancellationToken);
    ValueTask ResumeAsync(CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<AudioFrame> ReadFramesAsync(CancellationToken cancellationToken);
}

public interface IAudioPlayback : IAsyncDisposable
{
    string State { get; }
    ValueTask StartAsync(AudioPlaybackOptions options, CancellationToken cancellationToken);
    ValueTask PauseAsync(CancellationToken cancellationToken);
    ValueTask ResumeAsync(CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
    ValueTask<bool> WriteAsync(AudioFrame frame, CancellationToken cancellationToken);
    AudioLatencySnapshot GetLatency();
}

public interface IAudioDeviceManager : IAudioDeviceCatalog, IAsyncDisposable
{
    ValueTask<ImmutableArray<AudioEndpointSnapshot>> GetEndpointsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<AudioDeviceChange> WatchChangesAsync(CancellationToken cancellationToken);
}

public sealed record AudioEndpointSnapshot(string Id, string FriendlyName, string Direction, string State,
    bool IsDefaultConsole, bool IsDefaultMultimedia, bool IsDefaultCommunications, bool LoopbackCapable,
    AudioFormat? MixFormat, ImmutableArray<AudioFormat> SupportedFormats);
public sealed record AudioDeviceChange(AudioDeviceChangeKind Kind, string? DeviceId,
    DateTimeOffset OccurredAt, ImmutableArray<AudioEndpointSnapshot> CurrentEndpoints);

public interface IAudioClock
{
    long GetTimestampTicks();
    TimeSpan GetElapsed(long earlierTimestampTicks, long laterTimestampTicks);
    double CalculateDriftPartsPerMillion(long sourceFrames, long targetFrames, int sampleRate);
}

public interface IAudioBufferPool
{
    IMemoryOwner<byte> Rent(int minimumBytes);
    AudioBufferPoolSnapshot GetSnapshot();
}

public interface IAudioFormatConverter
{
    int Convert(ReadOnlySpan<byte> source, AudioFormat sourceFormat, Span<byte> destination, AudioFormat destinationFormat);
}

public interface IAudioMixer
{
    int Mix(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<float> destination, float leftGain = 1, float rightGain = 1);
}

public interface IAudioLatencyProvider { AudioLatencySnapshot GetLatency(); }
public interface IAudioDiagnostics { AudioDiagnosticsSnapshot GetSnapshot(); }

/// <summary>Engine-owned bridge to its event bus. Implementations must enqueue before returning when called from native callbacks.</summary>
public interface IAudioPlatformEventPublisher { ValueTask PublishAsync(AudioPlatformEvent audioEvent, CancellationToken cancellationToken = default); }

/// <summary>Compatibility metadata contract for the existing Engine device manager.</summary>
public sealed record AudioDeviceInfo(string Id, string Name, string Direction, bool IsDefault, bool IsAvailable);

/// <summary>Platform adapter port for endpoint enumeration and selection.</summary>
public interface IAudioDeviceCatalog
{
    ValueTask<EngineResult<ImmutableArray<AudioDeviceInfo>>> EnumerateAsync(CancellationToken cancellationToken);
    ValueTask<EngineResult<bool>> SelectAsync(AudioDeviceSelection selection, CancellationToken cancellationToken);
}
