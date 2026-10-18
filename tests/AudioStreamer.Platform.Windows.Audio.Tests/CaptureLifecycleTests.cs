using System.Collections.Concurrent;
using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.AudioAgent.Contracts.Audio;
using AudioStreamer.AudioAgent.Audio.Buffers;
using AudioStreamer.AudioAgent.Audio.Capture;
using AudioStreamer.AudioAgent.Audio.Diagnostics;
using AudioStreamer.AudioAgent.Audio.Internal;
using Xunit;

namespace AudioStreamer.Platform.Windows.Audio.Tests;

public sealed class CaptureLifecycleTests
{
    [Fact]
    public async Task Capture_TransitionsThroughStartPauseResumeStopAndOwnsFrames()
    {
        var factory = new FakeCaptureFactory();
        var sink = new RecordingAudioEventPublisher();
        var diagnostics = new AudioDiagnostics();
        var pool = new ArrayAudioBufferPool();
        await using var capture = new WasapiAudioCapture(pool, diagnostics, sink, factory);
        var format = new AudioFormat(48000, 1, PcmSampleFormat.Float32);
        await capture.StartAsync(new AudioCaptureOptions("test-endpoint", AudioCaptureSource.Microphone, format), CancellationToken.None);
        Assert.Equal("Running", capture.State);
        Assert.Equal(1, factory.Session.StartCount);

        await using var enumerator = capture.ReadFramesAsync(CancellationToken.None).GetAsyncEnumerator();
        factory.Session.Emit(new byte[8], timestamp: 4321);
        Assert.True(await enumerator.MoveNextAsync());
        var frame = enumerator.Current;
        Assert.Equal(format, frame.Format);
        Assert.Equal(2, frame.FrameCount);
        Assert.Equal(4321, frame.TimestampTicks);
        frame.Dispose();

        await capture.PauseAsync(CancellationToken.None);
        Assert.Equal("Paused", capture.State);
        await capture.ResumeAsync(CancellationToken.None);
        Assert.Equal("Running", capture.State);
        await capture.StopAsync(CancellationToken.None);
        Assert.Equal("Stopped", capture.State);
        Assert.Equal(0, pool.GetSnapshot().Outstanding);
        Assert.Contains(sink.Events, value => value.Kind == AudioPlatformEventKind.CaptureStarted);
        Assert.Contains(sink.Events, value => value.Kind == AudioPlatformEventKind.CaptureStopped);
        Assert.Equal(2, factory.Session.StartCount);
        Assert.Equal(2, factory.Session.PauseCount); // Explicit pause plus final recorder drain.
    }

    [Fact]
    public async Task Capture_DropsAtConfiguredBoundAndPublishesAggregatedOverrun()
    {
        var factory = new FakeCaptureFactory();
        var sink = new RecordingAudioEventPublisher();
        var diagnostics = new AudioDiagnostics();
        await using var capture = new WasapiAudioCapture(new ArrayAudioBufferPool(), diagnostics, sink, factory);
        var format = new AudioFormat(44100, 1, PcmSampleFormat.Int16);
        await capture.StartAsync(new AudioCaptureOptions(null, AudioCaptureSource.Microphone, format, QueueCapacity: 2), CancellationToken.None);

        factory.Session.Emit(new byte[2]);
        factory.Session.Emit(new byte[2]);
        factory.Session.Emit(new byte[2]);
        await sink.Overrun.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0L, diagnostics.GetSnapshot().Underruns);
        Assert.True(diagnostics.GetSnapshot().Overruns >= 1);
        Assert.Contains(sink.Events, value => value.Kind == AudioPlatformEventKind.BufferOverrun && value.Count >= 1);
    }

    [Fact]
    public async Task Capture_ObservesCancellationBeforeOpeningBackend()
    {
        var factory = new FakeCaptureFactory();
        await using var capture = new WasapiAudioCapture(new ArrayAudioBufferPool(), new AudioDiagnostics(),
            new RecordingAudioEventPublisher(), factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.StartAsync(
            new AudioCaptureOptions(null, AudioCaptureSource.Microphone, new AudioFormat(48000, 1, PcmSampleFormat.Float32)),
            cancellation.Token).AsTask());
        Assert.Equal(0, factory.CreateCount);
    }

    private sealed class FakeCaptureFactory : IWasapiCaptureFactory
    {
        public FakeCaptureSession Session { get; } = new();
        public int CreateCount { get; private set; }
        public IWasapiCaptureSession Create(AudioCaptureOptions options) { CreateCount++; return Session; }
    }

    private sealed class FakeCaptureSession : IWasapiCaptureSession
    {
        public string? DeviceId => "test-endpoint";
        public int StartCount { get; private set; }
        public int PauseCount { get; private set; }
        public event CapturePacketHandler? PacketAvailable;
        public void Start() => StartCount++;
        public void Pause() => PauseCount++;
        public void Emit(byte[] data, long timestamp = 7) => PacketAvailable?.Invoke(data, false, timestamp);
        public ValueTask DisposeAsync() { PauseCount++; return ValueTask.CompletedTask; }
    }

    private sealed class RecordingAudioEventPublisher : IAudioPlatformEventPublisher
    {
        private readonly ConcurrentQueue<AudioPlatformEvent> _events = new();
        public IReadOnlyCollection<AudioPlatformEvent> Events => _events.ToArray();
        public TaskCompletionSource<AudioPlatformEvent> Overrun { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask PublishAsync(AudioPlatformEvent audioEvent, CancellationToken cancellationToken = default)
        {
            _events.Enqueue(audioEvent);
            if (audioEvent.Kind == AudioPlatformEventKind.BufferOverrun) Overrun.TrySetResult(audioEvent);
            return ValueTask.CompletedTask;
        }
    }
}
