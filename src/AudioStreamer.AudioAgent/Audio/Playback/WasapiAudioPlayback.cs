using System.Threading.Channels;
using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.AudioAgent.Contracts.Audio;
using AudioStreamer.AudioAgent.Audio.Buffers;
using AudioStreamer.AudioAgent.Audio.Diagnostics;
using AudioStreamer.AudioAgent.Audio.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioStreamer.AudioAgent.Audio.Playback;

/// <summary>Shared-mode WASAPI PCM playback backed by a bounded SPSC ring and a non-blocking provider.</summary>
public sealed class WasapiAudioPlayback : IAudioPlayback
{
    private readonly AudioDiagnostics _diagnostics;
    private readonly IAudioPlatformEventPublisher _events;
    private readonly IAudioClock _clock;
    private readonly IWasapiPlaybackFactory _factory;
    private readonly ILogger<WasapiAudioPlayback> _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _producer = new(1, 1);
    private readonly Channel<byte> _underrunSignals = Channel.CreateBounded<byte>(new BoundedChannelOptions(64)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource _eventLifetime = new();
    private readonly Task _eventWorker;
    private IWasapiPlaybackSession? _session;
    private PcmRingWaveProvider? _provider;
    private AudioPlaybackOptions? _options;
    private string _state = "Stopped";
    private bool _disposed;

    internal WasapiAudioPlayback(AudioDiagnostics diagnostics, IAudioPlatformEventPublisher events, IAudioClock clock,
        IWasapiPlaybackFactory factory, ILogger<WasapiAudioPlayback>? logger = null)
    {
        _diagnostics = diagnostics;
        _events = events;
        _clock = clock;
        _factory = factory;
        _logger = logger ?? NullLogger<WasapiAudioPlayback>.Instance;
        _eventWorker = ProcessUnderrunEventsAsync(_eventLifetime.Token);
    }

    public string State => Volatile.Read(ref _state);

    public async ValueTask StartAsync(AudioPlaybackOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Format.EnsureSupported();
        if (options.BufferDurationMilliseconds is < 20 or > 2000 || options.RequestedLatencyMilliseconds is < 10 or > 500)
            throw new ArgumentOutOfRangeException(nameof(options));
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State == "Running") return;
            await StopCoreAsync().ConfigureAwait(false);
            _options = options;
            var capacity = checked(options.Format.BytesPerFrame * options.Format.SampleRate * options.BufferDurationMilliseconds / 1000);
            var ring = new SpscByteRingBuffer(capacity);
            _provider = new PcmRingWaveProvider(ring, options.Format, _clock, _diagnostics, QueueUnderrunEvent);
            _session = _factory.Create(options, _provider);
            Volatile.Write(ref _state, "Running");
            _diagnostics.SetPlaybackState("Running");
            _session.Start();
            await PublishAsync(AudioPlatformEventKind.PlaybackStarted, options.DeviceId).ConfigureAwait(false);
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask PauseAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != "Running" || _session is null) return;
            _session.Pause();
            Volatile.Write(ref _state, "Paused");
            _diagnostics.SetPlaybackState("Paused");
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask ResumeAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State != "Paused" || _session is null) return;
            _session.Start();
            Volatile.Write(ref _state, "Running");
            _diagnostics.SetPlaybackState("Running");
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask<bool> WriteAsync(AudioFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = _options;
        var provider = _provider;
        if (State != "Running" || options is null || provider is null)
            throw new InvalidOperationException("Playback is not running.");
        if (frame.Format != options.Format) throw new ArgumentException("PCM frame format does not match the active playback format.", nameof(frame));
        await _producer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!provider.TryWrite(frame.Data.Span))
            {
                _diagnostics.AddOverrun();
                _diagnostics.AddDropped(frame.FrameCount);
                await PublishAsync(AudioPlatformEventKind.BufferOverrun, options.DeviceId, frame.FrameCount).ConfigureAwait(false);
                return false;
            }
            return true;
        }
        finally { _producer.Release(); }
    }

    public AudioLatencySnapshot GetLatency()
    {
        var deviceLatency = _session?.CurrentLatency ??
            TimeSpan.FromMilliseconds(_options?.RequestedLatencyMilliseconds ?? 0);
        var latency = _provider?.GetLatency(deviceLatency) ?? new AudioLatencySnapshot(TimeSpan.Zero, deviceLatency, _clock.GetTimestampTicks());
        _diagnostics.SetLatency(latency);
        return latency;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            await StopCoreAsync().ConfigureAwait(false);
            _disposed = true;
            _underrunSignals.Writer.TryComplete();
            await _eventWorker.ConfigureAwait(false);
            _eventLifetime.Cancel();
            _eventLifetime.Dispose();
            _lifecycle.Dispose();
            _producer.Dispose();
        }
        finally { if (!_disposed) _lifecycle.Release(); }
    }

    private async ValueTask StopCoreAsync()
    {
        var deviceId = _options?.DeviceId;
        var session = _session;
        _session = null;
        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        _provider?.Clear();
        _provider = null;
        var wasRunning = State is "Running" or "Paused";
        Volatile.Write(ref _state, "Stopped");
        _diagnostics.SetPlaybackState("Stopped");
        if (wasRunning) await PublishAsync(AudioPlatformEventKind.PlaybackStopped, deviceId).ConfigureAwait(false);
    }

    private ValueTask PublishAsync(AudioPlatformEventKind kind, string? deviceId, long count = 0) =>
        _events.PublishAsync(new AudioPlatformEvent(kind, DateTimeOffset.UtcNow, deviceId, count));

    private void QueueUnderrunEvent() => _underrunSignals.Writer.TryWrite(0);

    private async Task ProcessUnderrunEventsAsync(CancellationToken cancellationToken)
    {
        await foreach (var signal in _underrunSignals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = signal;
            long count = 1;
            while (_underrunSignals.Reader.TryRead(out _)) count++;
            try { await PublishAsync(AudioPlatformEventKind.BufferUnderrun, _options?.DeviceId, count).ConfigureAwait(false); }
            catch (Exception exception)
            {
                _logger.LogError(exception,
                    "Playback underrun event publication failed. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} HResult={HResult} StackTrace={StackTrace} InnerException={InnerException} InnerStackTrace={InnerStackTrace}",
                    exception.GetType().FullName, exception.Message, exception.HResult, exception.StackTrace,
                    exception.InnerException?.ToString(), exception.InnerException?.StackTrace);
                _diagnostics.AddSafeError("underrun-event-failure");
            }
        }
    }

    private sealed class PcmRingWaveProvider(SpscByteRingBuffer ring, AudioFormat format,
        IAudioClock clock, AudioDiagnostics diagnostics, Action queueUnderrunEvent) : IPcmAudioSource
    {
        public AudioFormat Format => format;
        public bool TryWrite(ReadOnlySpan<byte> source) => ring.TryWrite(source);
        public void Clear() => ring.Clear();
        public int Read(Span<byte> destination)
        {
            var received = ring.Read(destination);
            if (received < destination.Length)
            {
                destination[received..].Clear();
                diagnostics.AddUnderrun();
                queueUnderrunEvent();
            }
            var frames = received / format.BytesPerFrame;
            diagnostics.AddPlayed(frames);
            return destination.Length;
        }
        public AudioLatencySnapshot GetLatency(TimeSpan deviceLatency) => new(
            TimeSpan.FromSeconds((double)ring.BufferedBytes / format.BytesPerFrame / format.SampleRate),
            deviceLatency, clock.GetTimestampTicks());
    }
}
