using System.Net;
using System.Runtime.CompilerServices;
using AudioStreamer.Audio.Transport.Transport;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Platform.Windows.Audio;
using AudioStreamer.Platform.Windows.Audio.Formats;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AudioStreamer.Platform.Windows.Audio.Tests;

public sealed class WindowsAudioSessionRuntimeTests
{
    [Fact]
    public async Task AudioTransportInitializationFailureIsReturnedWithoutStartingCapture()
    {
        var capture = new FailingCapture("capture should not start");
        var services = new ServiceCollection();
        services.AddSingleton<IAudioCapture>(capture);
        services.AddSingleton<IAudioFormatConverter, PcmFormatConverter>();
        await using var provider = services.BuildServiceProvider();
        var options = new WindowsAudioStreamOptions(
            new AudioUdpOptions(new IPEndPoint(IPAddress.Loopback, 5007), 16, 16, TimeSpan.FromMilliseconds(250)),
            CaptureDeviceId: null, PlaybackDeviceId: null, new AudioFormat(48000, 2, PcmSampleFormat.Float32));
        await using var runtime = new WindowsAudioSessionRuntime(provider.GetRequiredService<IServiceScopeFactory>(),
            options, new NoopAudioEventPublisher(), NullLogger<WindowsAudioSessionRuntime>.Instance);
        var senderId = Guid.NewGuid();

        var result = await runtime.StartAsync(new StreamRequest(senderId, Guid.NewGuid()), senderId,
            new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(EngineErrorCode.TransportUnavailable, result.Error!.Code);
        Assert.Equal(0, capture.StartCount);
    }

    [Theory]
    [InlineData("missing-default-device")]
    [InlineData("capture-initialization-failed")]
    public async Task CaptureInitializationFailureIsReturnedAsRetryableAudioTransportFailure(string message)
    {
        var capture = new FailingCapture(message);
        var services = new ServiceCollection();
        services.AddSingleton<IAudioCapture>(capture);
        services.AddSingleton<IAudioFormatConverter, PcmFormatConverter>();
        await using var provider = services.BuildServiceProvider();
        var options = new WindowsAudioStreamOptions(
            new AudioUdpOptions(new IPEndPoint(IPAddress.Loopback, 5007), 16, 16, TimeSpan.FromMilliseconds(250)),
            CaptureDeviceId: null, PlaybackDeviceId: null, new AudioFormat(48000, 2, PcmSampleFormat.Float32));
        await using var runtime = new WindowsAudioSessionRuntime(provider.GetRequiredService<IServiceScopeFactory>(),
            options, new NoopAudioEventPublisher(), NullLogger<WindowsAudioSessionRuntime>.Instance);

        var senderId = Guid.NewGuid();
        var result = await runtime.StartAsync(new StreamRequest(senderId, Guid.NewGuid()),
            localNodeId: senderId, audioEndpoint: new IPEndPoint(IPAddress.Loopback, 5008), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(EngineErrorCode.TransportUnavailable, result.Error!.Code);
        Assert.Equal("audio-transport", result.Error.Subsystem);
        Assert.True(result.Error.IsRetryable);
        Assert.Equal(1, capture.StartCount);
    }

    private sealed class FailingCapture(string message) : IAudioCapture
    {
        public int StartCount { get; private set; }
        public string State => "Stopped";
        public ValueTask StartAsync(AudioCaptureOptions options, CancellationToken cancellationToken)
        {
            StartCount++;
            throw new InvalidOperationException(message);
        }
        public ValueTask PauseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ResumeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NoopAudioEventPublisher : IAudioPlatformEventPublisher
    {
        public ValueTask PublishAsync(AudioPlatformEvent audioEvent, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
