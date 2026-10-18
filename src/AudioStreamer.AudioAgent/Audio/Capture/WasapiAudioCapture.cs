using System.Threading.Channels;
using System.Buffers;
using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.AudioAgent.Contracts.Audio;
using AudioStreamer.AudioAgent.Audio.Diagnostics;
using AudioStreamer.AudioAgent.Audio.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;

namespace AudioStreamer.AudioAgent.Audio.Capture;

/// <summary>Bounded, non-blocking PCM capture handoff around a mockable shared-mode WASAPI session.</summary>
public sealed class WasapiAudioCapture : IAudioCapture
{
    private readonly IAudioBufferPool _buffers;
    private readonly AudioDiagnostics _diagnostics;
    private readonly IAudioPlatformEventPublisher _events;
    private readonly IWasapiCaptureFactory _factory;
    private readonly ILogger<WasapiAudioCapture> _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Channel<byte> _overrunSignals = Channel.CreateBounded<byte>(new BoundedChannelOptions(64)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly Channel<Exception> _callbackErrors = Channel.CreateBounded<Exception>(new BoundedChannelOptions(16)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly Task _eventWorker;
    private Channel<AudioFrame>? _frames;
    private IWasapiCaptureSession? _session;
    private AudioCaptureOptions? _options;
    private string _state = "Stopped";
    private bool _disposed;

    internal WasapiAudioCapture(IAudioBufferPool buffers, AudioDiagnostics diagnostics,
        IAudioPlatformEventPublisher events, IWasapiCaptureFactory factory, ILogger<WasapiAudioCapture>? logger = null)
    {
        _buffers = buffers;
        _diagnostics = diagnostics;
        _events = events;
        _factory = factory;
        _logger = logger ?? NullLogger<WasapiAudioCapture>.Instance;
        _eventWorker = Task.WhenAll(ProcessOverrunEventsAsync(), ProcessCallbackErrorsAsync());
    }

    public string State => Volatile.Read(ref _state);

    public async ValueTask StartAsync(AudioCaptureOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Format.EnsureSupported();
        if (options.QueueCapacity is < 2 or > 128) throw new ArgumentOutOfRangeException(nameof(options.QueueCapacity));
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State == "Running") return;
            await StopCoreAsync().ConfigureAwait(false);
            _options = options;
            _frames = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(options.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
            _session = _factory.Create(options);
            _session.PacketAvailable += OnDataAvailable;
            Volatile.Write(ref _state, "Running");
            _diagnostics.SetCaptureState("Running");
            _session.Start();
            await PublishAsync(AudioPlatformEventKind.CaptureStarted, _session.DeviceId).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception,
                "WASAPI capture initialization failed; cleanup will run. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} HResult={HResult} StackTrace={StackTrace} InnerException={InnerException} InnerStackTrace={InnerStackTrace}",
                exception.GetType().FullName, exception.Message, exception.HResult, exception.StackTrace,
                exception.InnerException?.ToString(), exception.InnerException?.StackTrace);
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
            _diagnostics.SetCaptureState("Paused");
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
            _diagnostics.SetCaptureState("Running");
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Volatile.Read(ref _frames) ?? throw new InvalidOperationException("Capture has not been started.");
        await foreach (var frame in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return frame;
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
            _overrunSignals.Writer.TryComplete();
            _callbackErrors.Writer.TryComplete();
            await _eventWorker.ConfigureAwait(false);
            _lifecycle.Dispose();
        }
        finally { if (!_disposed) _lifecycle.Release(); }
    }

    private void OnDataAvailable(ReadOnlySpan<byte> data, bool isSilent, long timestampTicks)
    {
        var options = _options;
        var writer = _frames?.Writer;
        if (options is null || writer is null || State != "Running") return;
        var bytes = data.Length - data.Length % options.Format.BytesPerFrame;
        if (bytes == 0) return;
        IMemoryOwner<byte>? owner = null;
        try
        {
            owner = _buffers.Rent(bytes);
            if (isSilent) owner.Memory.Span[..bytes].Clear();
            else data[..bytes].CopyTo(owner.Memory.Span);
            var frameCount = bytes / options.Format.BytesPerFrame;
            var frame = new AudioFrame(owner, bytes, options.Format, frameCount, timestampTicks);
            owner = null; // Ownership has moved to the frame, including the bounded queue rejection path.
            if (!writer.TryWrite(frame))
            {
                frame.Dispose();
                _diagnostics.AddDropped(frameCount);
                _diagnostics.AddOverrun();
                _overrunSignals.Writer.TryWrite(0);
            }
            else _diagnostics.AddCaptured(frameCount);
        }
        catch (Exception exception)
        {
            owner?.Dispose();
            _diagnostics.AddDropped(bytes / options.Format.BytesPerFrame);
            _diagnostics.AddSafeError("capture-buffer-failure");
            _callbackErrors.Writer.TryWrite(exception);
            _overrunSignals.Writer.TryWrite(0);
        }
    }

    private async ValueTask StopCoreAsync()
    {
        var session = _session;
        _session = null;
        if (session is not null)
        {
            session.PacketAvailable -= OnDataAvailable;
            await session.DisposeAsync().ConfigureAwait(false);
        }
        var frames = Interlocked.Exchange(ref _frames, null);
        frames?.Writer.TryComplete();
        if (frames is not null)
            while (frames.Reader.TryRead(out var frame)) frame.Dispose();
        var wasActive = State is "Running" or "Paused";
        Volatile.Write(ref _state, "Stopped");
        _diagnostics.SetCaptureState("Stopped");
        if (wasActive) await PublishAsync(AudioPlatformEventKind.CaptureStopped, session?.DeviceId).ConfigureAwait(false);
    }

    private ValueTask PublishAsync(AudioPlatformEventKind kind, string? deviceId) =>
        _events.PublishAsync(new AudioPlatformEvent(kind, DateTimeOffset.UtcNow, deviceId));

    private async Task ProcessOverrunEventsAsync()
    {
        await foreach (var signal in _overrunSignals.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            _ = signal;
            long count = 1;
            while (_overrunSignals.Reader.TryRead(out _)) count++;
            try
            {
                await _events.PublishAsync(new AudioPlatformEvent(AudioPlatformEventKind.BufferOverrun,
                DateTimeOffset.UtcNow, _session?.DeviceId, count)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception,
                    "Capture overrun event publication failed. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} HResult={HResult} StackTrace={StackTrace} InnerException={InnerException} InnerStackTrace={InnerStackTrace}",
                    exception.GetType().FullName, exception.Message, exception.HResult, exception.StackTrace,
                    exception.InnerException?.ToString(), exception.InnerException?.StackTrace);
                _diagnostics.AddSafeError("overrun-event-failure");
            }
        }
    }

    private async Task ProcessCallbackErrorsAsync()
    {
        await foreach (var exception in _callbackErrors.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            _logger.LogError(exception,
                "WASAPI capture callback failed. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} HResult={HResult} StackTrace={StackTrace}",
                exception.GetType().FullName, exception.Message, exception.HResult, exception.StackTrace);
        }
    }

    internal static WaveFormat ToWaveFormat(AudioFormat format) => format.SampleFormat == PcmSampleFormat.Float32
        ? WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels)
        : new WaveFormat(format.SampleRate, 16, format.Channels);
}
