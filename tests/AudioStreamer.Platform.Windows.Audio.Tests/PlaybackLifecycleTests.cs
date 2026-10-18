using System.Collections.Concurrent;
using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.AudioAgent.Contracts.Audio;
using AudioStreamer.AudioAgent.Audio.Buffers;
using AudioStreamer.AudioAgent.Audio.Diagnostics;
using AudioStreamer.AudioAgent.Audio.Internal;
using AudioStreamer.AudioAgent.Audio.Playback;
using Xunit;

namespace AudioStreamer.Platform.Windows.Audio.Tests;

public sealed class PlaybackLifecycleTests
{
    [Fact]
    public async Task Playback_WritesPcmAndZeroFillsUnderrunAcrossLifecycle()
    {
        var factory = new FakePlaybackFactory();
        var events = new RecordingPublisher();
        var diagnostics = new AudioDiagnostics();
        var clock = new AudioStreamer.AudioAgent.Audio.Clock.StopwatchAudioClock();
        await using var playback = new WasapiAudioPlayback(diagnostics, events, clock, factory);
        var format = new AudioFormat(48000, 2, PcmSampleFormat.Float32);
        await playback.StartAsync(new AudioPlaybackOptions("render-endpoint", format), CancellationToken.None);
        Assert.Equal("Running", playback.State);

        var buffers = new ArrayAudioBufferPool();
        var owner = buffers.Rent(format.BytesPerFrame);
        var pcm = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        pcm.AsSpan().CopyTo(owner.Memory.Span);
        using var frame = new AudioFrame(owner, pcm.Length, format, 1, 10);
        Assert.True(await playback.WriteAsync(frame, CancellationToken.None));

        var output = new byte[16];
        Assert.Equal(output.Length, factory.Session.Source!.Read(output));
        Assert.Equal(pcm, output[..pcm.Length]);
        Assert.All(output[pcm.Length..], value => Assert.Equal((byte)0, value));
        await events.Underrun.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, diagnostics.GetSnapshot().Underruns);

        await playback.PauseAsync(CancellationToken.None);
        Assert.Equal("Paused", playback.State);
        await playback.ResumeAsync(CancellationToken.None);
        Assert.Equal("Running", playback.State);
        await playback.StopAsync(CancellationToken.None);
        Assert.Equal("Stopped", playback.State);
        Assert.Contains(events.Items, item => item.Kind == AudioPlatformEventKind.PlaybackStarted);
        Assert.Contains(events.Items, item => item.Kind == AudioPlatformEventKind.PlaybackStopped);
    }

    private sealed class FakePlaybackFactory : IWasapiPlaybackFactory
    {
        public FakePlaybackSession Session { get; } = new();
        public IWasapiPlaybackSession Create(AudioPlaybackOptions options, IPcmAudioSource source)
        {
            Session.Source = source;
            return Session;
        }
    }

    private sealed class FakePlaybackSession : IWasapiPlaybackSession
    {
        public IPcmAudioSource? Source { get; set; }
        public TimeSpan CurrentLatency => TimeSpan.FromMilliseconds(4);
        public void Start() { }
        public void Pause() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingPublisher : IAudioPlatformEventPublisher
    {
        private readonly ConcurrentQueue<AudioPlatformEvent> _items = new();
        public IReadOnlyCollection<AudioPlatformEvent> Items => _items.ToArray();
        public TaskCompletionSource<AudioPlatformEvent> Underrun { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask PublishAsync(AudioPlatformEvent audioEvent, CancellationToken cancellationToken = default)
        {
            _items.Enqueue(audioEvent);
            if (audioEvent.Kind == AudioPlatformEventKind.BufferUnderrun) Underrun.TrySetResult(audioEvent);
            return ValueTask.CompletedTask;
        }
    }
}
