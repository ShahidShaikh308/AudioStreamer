using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using AudioStreamer.AudioAgent;
using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.AudioAgent.Contracts.Audio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AudioStreamer.AudioAgent.Tests;

public sealed class AgentAudioCommandHandlerTests
{
    [Fact]
    public async Task CaptureCommandStreamsPcmAndRestartsAfterDeviceNotification()
    {
        var capture = new FakeCapture();
        var devices = new FakeDevices();
        await using var provider = BuildProvider(capture, new FakePlayback());
        var agentEvents = new AgentEventPublisher(NullLogger<AgentEventPublisher>.Instance);
        await using var handler = new AgentAudioCommandHandler(provider.GetRequiredService<IServiceScopeFactory>(), devices,
            agentEvents,
            NullLogger<AgentAudioCommandHandler>.Instance);
        await using var captureConnection = new FakeConnection(AgentChannel.CapturePcm);
        await using var controlConnection = new FakeConnection(AgentChannel.Control);
        using var lifetime = new CancellationTokenSource();
        var captureChannel = handler.RunCaptureChannelAsync(captureConnection, lifetime.Token);
        handler.AttachControlConnection(controlConnection, lifetime.Token);
        await agentEvents.PublishAsync(new AudioPlatformEvent(AudioPlatformEventKind.CaptureStarted, DateTimeOffset.UtcNow, "mic"));
        var platformEvent = await controlConnection.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AgentMessageType.PlatformEvent, platformEvent.MessageType);
        var streamId = Guid.NewGuid();
        var start = Command(AgentMessageType.CaptureStart,
            new AgentOperationRequest(streamId, null, 48000, 2, "Int16", "Microphone"));
        var started = await handler.HandleControlAsync(start, CancellationToken.None);
        Assert.True(JsonSerializer.Deserialize<AgentOperationResponse>(started!.Payload.Span)!.Succeeded);
        Assert.Equal(1, await capture.StartCounts.Reader.ReadAsync());

        var pcm = await captureConnection.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AgentMessageType.CapturePcm, pcm.MessageType);
        Assert.Equal(streamId, pcm.PcmMetadata!.StreamId);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, pcm.Payload.ToArray());

        devices.Notify(new AudioDeviceChange(AudioDeviceChangeKind.DefaultChanged, null, DateTimeOffset.UtcNow, []));
        var notification = await controlConnection.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AgentMessageType.DeviceChanged, notification.MessageType);
        var secondCaptureFrame = await captureConnection.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AgentMessageType.CapturePcm, secondCaptureFrame.MessageType);
        Assert.Equal(2, await capture.StartCounts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

        await handler.HandleControlAsync(Command(AgentMessageType.CaptureStop, new AgentOperationRequest(streamId, null)), CancellationToken.None);
        await lifetime.CancelAsync();
        await captureChannel.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, capture.StopCount);
    }

    [Fact]
    public async Task PlaybackPcmIsDeliveredWithoutPerFrameAcknowledgement()
    {
        var playback = new FakePlayback();
        var devices = new FakeDevices();
        await using var provider = BuildProvider(new FakeCapture(), playback);
        await using var handler = new AgentAudioCommandHandler(provider.GetRequiredService<IServiceScopeFactory>(), devices,
            new AgentEventPublisher(NullLogger<AgentEventPublisher>.Instance),
            NullLogger<AgentAudioCommandHandler>.Instance);
        await using var playbackConnection = new FakeConnection(AgentChannel.PlaybackPcm);
        await using var controlConnection = new FakeConnection(AgentChannel.Control);
        using var lifetime = new CancellationTokenSource();
        var playbackChannel = handler.RunPlaybackChannelAsync(playbackConnection, lifetime.Token);
        handler.AttachControlConnection(controlConnection, lifetime.Token);
        var streamId = Guid.NewGuid();
        var format = new AgentPcmMetadata(streamId, 4, 789, 44100, 1, "Int16", 2);
        var start = Command(AgentMessageType.PlaybackStart,
            new AgentOperationRequest(streamId, null, 44100, 1, "Int16"));
        var started = await handler.HandleControlAsync(start, CancellationToken.None);
        Assert.True(JsonSerializer.Deserialize<AgentOperationResponse>(started!.Payload.Span)!.Succeeded);
        Assert.Equal(1, await playback.StartCounts.Reader.ReadAsync());
        var data = new byte[] { 10, 20, 30, 40 };
        await playbackConnection.Incoming.Writer.WriteAsync(new AgentFrame(AgentProtocol.CurrentVersion,
            AgentChannel.PlaybackPcm, AgentMessageType.PlaybackPcm, format.SequenceNumber, Guid.Empty,
            AgentProtocolCodec.EncodePcm(format, data)));

        await playback.FrameDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(playbackConnection.Outgoing.Reader.TryRead(out _));
        Assert.Equal(data, playback.LastPcm.ToArray());
        Assert.Equal(format.TimestampTicks, playback.LastTimestampTicks);

        devices.Notify(new AudioDeviceChange(AudioDeviceChangeKind.DefaultChanged, null, DateTimeOffset.UtcNow, []));
        var notification = await controlConnection.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AgentMessageType.DeviceChanged, notification.MessageType);
        Assert.Equal(2, await playback.StartCounts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

        await handler.HandleControlAsync(Command(AgentMessageType.PlaybackStop, new AgentOperationRequest(streamId, null)), CancellationToken.None);
        await lifetime.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playbackChannel);
    }

    private static ServiceProvider BuildProvider(FakeCapture capture, FakePlayback playback) => new ServiceCollection()
        .AddSingleton<IAudioCapture>(capture).AddSingleton<IAudioPlayback>(playback).BuildServiceProvider();

    private static AgentFrame Command<T>(AgentMessageType type, T payload) => new(AgentProtocol.CurrentVersion,
        AgentChannel.Control, type, 0, Guid.NewGuid(), JsonSerializer.SerializeToUtf8Bytes(payload));

    private sealed class FakeConnection(AgentChannel channelType) : IAgentConnection
    {
        public Channel<AgentFrame> Incoming { get; } = System.Threading.Channels.Channel.CreateUnbounded<AgentFrame>();
        public Channel<AgentFrame> Outgoing { get; } = System.Threading.Channels.Channel.CreateUnbounded<AgentFrame>();
        public AgentChannel Channel { get; } = channelType;
        public bool IsConnected => true;
        public ValueTask SendAsync(AgentFrame frame, CancellationToken cancellationToken) => Outgoing.Writer.WriteAsync(frame, cancellationToken);
        public ValueTask<AgentFrame> ReceiveAsync(CancellationToken cancellationToken) => Incoming.Reader.ReadAsync(cancellationToken);
        public ValueTask DisposeAsync() { Incoming.Writer.TryComplete(); Outgoing.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    private sealed class FakeDevices : IAgentAudioDeviceManager
    {
        private readonly Channel<AudioDeviceChange> _changes = Channel.CreateUnbounded<AudioDeviceChange>();
        public void Notify(AudioDeviceChange change) => _changes.Writer.TryWrite(change);
        public ValueTask<ImmutableArray<AudioEndpointSnapshot>> GetEndpointsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(ImmutableArray<AudioEndpointSnapshot>.Empty);
        public ValueTask<AgentOperationResponse> SelectAsync(AgentSelectionRequest selection, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AgentOperationResponse(true));
        public async IAsyncEnumerable<AudioDeviceChange> WatchChangesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        { await foreach (var change in _changes.Reader.ReadAllAsync(cancellationToken)) yield return change; }
        public ValueTask DisposeAsync() { _changes.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    private sealed class FakeCapture : IAudioCapture
    {
        private Channel<AudioFrame>? _frames;
        private int _startCount;
        public Channel<int> StartCounts { get; } = Channel.CreateUnbounded<int>();
        public string State { get; private set; } = "Stopped";
        public int StopCount { get; private set; }
        public ValueTask StartAsync(AudioCaptureOptions options, CancellationToken cancellationToken)
        {
            _frames = Channel.CreateUnbounded<AudioFrame>();
            State = "Running";
            StartCounts.Writer.TryWrite(Interlocked.Increment(ref _startCount));
            var owner = new MemoryOwner(new byte[] { 1, 2, 3, 4 });
            _frames.Writer.TryWrite(new AudioFrame(owner, 4, options.Format, 1, 111));
            return ValueTask.CompletedTask;
        }
        public ValueTask PauseAsync(CancellationToken cancellationToken) { State = "Paused"; return ValueTask.CompletedTask; }
        public ValueTask ResumeAsync(CancellationToken cancellationToken) { State = "Running"; return ValueTask.CompletedTask; }
        public ValueTask StopAsync(CancellationToken cancellationToken) { State = "Stopped"; StopCount++; _frames?.Writer.TryComplete(); return ValueTask.CompletedTask; }
        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        { var frames = _frames ?? throw new InvalidOperationException(); await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken)) yield return frame; }
        public ValueTask DisposeAsync() => StopAsync(CancellationToken.None);
    }

    private sealed class FakePlayback : IAudioPlayback
    {
        private int _startCount;
        public Channel<int> StartCounts { get; } = Channel.CreateUnbounded<int>();
        public string State { get; private set; } = "Stopped";
        public ReadOnlyMemory<byte> LastPcm { get; private set; }
        public long LastTimestampTicks { get; private set; }
        public TaskCompletionSource FrameDelivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask StartAsync(AudioPlaybackOptions options, CancellationToken cancellationToken)
        { State = "Running"; StartCounts.Writer.TryWrite(Interlocked.Increment(ref _startCount)); return ValueTask.CompletedTask; }
        public ValueTask PauseAsync(CancellationToken cancellationToken) { State = "Paused"; return ValueTask.CompletedTask; }
        public ValueTask ResumeAsync(CancellationToken cancellationToken) { State = "Running"; return ValueTask.CompletedTask; }
        public ValueTask StopAsync(CancellationToken cancellationToken) { State = "Stopped"; return ValueTask.CompletedTask; }
        public ValueTask<bool> WriteAsync(AudioFrame frame, CancellationToken cancellationToken)
        { LastPcm = frame.Data.ToArray(); LastTimestampTicks = frame.TimestampTicks; FrameDelivered.TrySetResult(); return ValueTask.FromResult(true); }
        public AudioLatencySnapshot GetLatency() => new(TimeSpan.Zero, TimeSpan.Zero, 0);
        public ValueTask DisposeAsync() => StopAsync(CancellationToken.None);
    }

    private sealed class MemoryOwner(byte[] bytes) : System.Buffers.IMemoryOwner<byte>
    {
        private byte[]? _bytes = bytes;
        public Memory<byte> Memory => _bytes ?? throw new ObjectDisposedException(nameof(MemoryOwner));
        public void Dispose() => _bytes = null;
    }
}
