using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Platform.Windows.Audio;
using Xunit;

namespace AudioStreamer.Platform.Windows.Audio.Tests;

public sealed class AgentAudioAdapterTests
{
    [Fact]
    public async Task CaptureAdapter_StartsRemoteCaptureAndPreservesPcmMetadataAndOwnership()
    {
        var client = new FakeAgentClient();
        await using var capture = new AgentAudioCapture(client, new AudioStreamer.Platform.Windows.Audio.Buffers.ArrayAudioBufferPool());
        var format = new AudioFormat(48000, 2, PcmSampleFormat.Int16);
        await capture.StartAsync(new AudioCaptureOptions("capture-id", AudioCaptureSource.Microphone, format), CancellationToken.None);
        var start = Assert.IsType<AgentOperationRequest>(client.LastCaptureStart);
        Assert.Equal("Microphone", start.CaptureSource);
        Assert.Equal("capture-id", start.DeviceId);

        await foreach (var frame in capture.ReadFramesAsync(CancellationToken.None))
        {
            Assert.Equal(format, frame.Format);
            Assert.Equal(2, frame.FrameCount);
            Assert.Equal(19, frame.TimestampTicks);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, frame.Data.ToArray());
            frame.Dispose();
            Assert.Throws<ObjectDisposedException>(() => frame.Data);
            break;
        }
        await capture.PauseAsync(CancellationToken.None);
        await capture.ResumeAsync(CancellationToken.None);
        await capture.StopAsync(CancellationToken.None);
        Assert.Equal(1, client.CaptureStops);
        Assert.Equal(1, client.CapturePauses);
        Assert.Equal(1, client.CaptureResumes);
    }

    [Fact]
    public async Task PlaybackAdapter_EncodesExactPcmWithSequenceAndFormat()
    {
        var client = new FakeAgentClient();
        await using var playback = new AgentAudioPlayback(client);
        var format = new AudioFormat(44100, 1, PcmSampleFormat.Float32);
        await playback.StartAsync(new AudioPlaybackOptions("render-id", format), CancellationToken.None);
        var pool = new AudioStreamer.Platform.Windows.Audio.Buffers.ArrayAudioBufferPool();
        var owner = pool.Rent(8);
        new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 }.CopyTo(owner.Memory);
        using var audioFrame = new AudioFrame(owner, 8, format, 2, 123);
        Assert.True(await playback.WriteAsync(audioFrame, CancellationToken.None));
        Assert.Equal("render-id", client.LastPlaybackStart?.DeviceId);
        Assert.Equal(new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 }, client.LastPlaybackPcm.ToArray());
        Assert.Equal(1U, client.LastPlaybackMetadata?.SequenceNumber);
        Assert.Equal(format.SampleRate, client.LastPlaybackMetadata?.SampleRate);
        await playback.PauseAsync(CancellationToken.None);
        await playback.ResumeAsync(CancellationToken.None);
        await playback.StopAsync(CancellationToken.None);
        Assert.Equal(1, client.PlaybackStops);
    }

    [Fact]
    public async Task DeviceCatalog_MapsAgentEndpointsAndSelectionResult()
    {
        var client = new FakeAgentClient
        {
            Devices = [new AgentDevice("mic", "Microphone", "capture", false, true),
                new AgentDevice("off", "Offline", "playback", false, false)]
        };
        var catalog = new AgentAudioDeviceCatalog(client);
        var devices = await catalog.EnumerateAsync(CancellationToken.None);
        Assert.True(devices.IsSuccess);
        Assert.Equal("mic", Assert.Single(devices.Value!).Id);
        var selected = await catalog.SelectAsync(new AudioDeviceSelection("mic", null), CancellationToken.None);
        Assert.True(selected.IsSuccess);
        Assert.Equal("mic", client.LastSelection?.CaptureDeviceId);
    }

    private sealed class FakeAgentClient : IAgentAudioClient
    {
        public bool IsConnected => true;
        public DateTimeOffset? LastHeartbeat => DateTimeOffset.UtcNow;
        public event Action<DeviceListResponse>? DevicesChanged { add { } remove { } }
        public event Action<AgentPlatformEvent>? PlatformEventReceived { add { } remove { } }
        public AgentOperationRequest? LastCaptureStart { get; private set; }
        public AgentOperationRequest? LastPlaybackStart { get; private set; }
        public AgentSelectionRequest? LastSelection { get; private set; }
        public AgentPcmMetadata? LastPlaybackMetadata { get; private set; }
        public ReadOnlyMemory<byte> LastPlaybackPcm { get; private set; }
        public IReadOnlyList<AgentDevice> Devices { get; set; } = [];
        public int CaptureStops { get; private set; }
        public int CapturePauses { get; private set; }
        public int CaptureResumes { get; private set; }
        public int PlaybackStops { get; private set; }
        public ValueTask ConnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<AgentReady> HandshakeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new AgentReady(1, Guid.NewGuid(), "test"));
        public ValueTask<DeviceListResponse> EnumerateDevicesAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new DeviceListResponse(Devices));
        public ValueTask<AgentOperationResponse> SelectDevicesAsync(AgentSelectionRequest request, CancellationToken cancellationToken)
        { LastSelection = request; return ValueTask.FromResult(new AgentOperationResponse(true)); }
        public ValueTask<AgentOperationResponse> StartCaptureAsync(AgentOperationRequest request, CancellationToken cancellationToken)
        { LastCaptureStart = request; return ValueTask.FromResult(new AgentOperationResponse(true)); }
        public ValueTask<AgentOperationResponse> StopCaptureAsync(Guid streamId, CancellationToken cancellationToken)
        { CaptureStops++; return ValueTask.FromResult(new AgentOperationResponse(true)); }
        public ValueTask<AgentOperationResponse> PauseCaptureAsync(Guid streamId, bool pause, CancellationToken cancellationToken)
        { if (pause) CapturePauses++; else CaptureResumes++; return ValueTask.FromResult(new AgentOperationResponse(true)); }
        public ValueTask<AgentOperationResponse> StartPlaybackAsync(AgentOperationRequest request, CancellationToken cancellationToken)
        { LastPlaybackStart = request; return ValueTask.FromResult(new AgentOperationResponse(true)); }
        public ValueTask<AgentOperationResponse> StopPlaybackAsync(Guid streamId, CancellationToken cancellationToken)
        { PlaybackStops++; return ValueTask.FromResult(new AgentOperationResponse(true)); }
        public ValueTask<AgentOperationResponse> PausePlaybackAsync(Guid streamId, bool pause, CancellationToken cancellationToken) => ValueTask.FromResult(new AgentOperationResponse(true));
        public async IAsyncEnumerable<(AgentPcmMetadata Metadata, ReadOnlyMemory<byte> Pcm)> ReadCaptureFramesAsync(Guid streamId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return (new AgentPcmMetadata(streamId, 5, 19, 48000, 2, "Int16", 2), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            await Task.CompletedTask;
        }
        public ValueTask WritePlaybackFrameAsync(AgentPcmMetadata metadata, ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken)
        { LastPlaybackMetadata = metadata; LastPlaybackPcm = pcm.ToArray(); return ValueTask.CompletedTask; }
        public ValueTask ShutdownAgentAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public Task StartMonitoringAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopMonitoringAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
