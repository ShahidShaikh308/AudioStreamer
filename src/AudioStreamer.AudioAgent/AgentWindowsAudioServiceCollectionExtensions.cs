using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.AudioAgent.Contracts.Audio;
using AudioStreamer.AudioAgent.Audio.Buffers;
using AudioStreamer.AudioAgent.Audio.Capture;
using AudioStreamer.AudioAgent.Audio.Clock;
using AudioStreamer.AudioAgent.Audio.Diagnostics;
using AudioStreamer.AudioAgent.Audio.Devices;
using AudioStreamer.AudioAgent.Audio.Internal;
using AudioStreamer.AudioAgent.Audio.Playback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.AudioAgent;

internal static class AgentWindowsAudioServiceCollectionExtensions
{
    public static IServiceCollection AddAgentWindowsAudio(this IServiceCollection services)
    {
        services.TryAddScoped<AudioDiagnostics>();
        services.TryAddScoped<IAudioDiagnostics>(provider => provider.GetRequiredService<AudioDiagnostics>());
        services.TryAddScoped<IAudioLatencyProvider>(provider => provider.GetRequiredService<AudioDiagnostics>());
        services.TryAddScoped<IAudioBufferPool, ArrayAudioBufferPool>();
        services.TryAddSingleton<IAudioClock, StopwatchAudioClock>();
        services.TryAddSingleton<IAgentAudioDeviceManager, WindowsAudioDeviceManager>();
        services.TryAddTransient<IWasapiCaptureFactory, NAudioWasapiCaptureFactory>();
        services.TryAddTransient<IWasapiPlaybackFactory>(_ => new NAudioWasapiPlaybackFactory());
        services.TryAddTransient<IAudioCapture>(provider => new WasapiAudioCapture(
            provider.GetRequiredService<IAudioBufferPool>(), provider.GetRequiredService<AudioDiagnostics>(),
            provider.GetRequiredService<IAudioPlatformEventPublisher>(), provider.GetRequiredService<IWasapiCaptureFactory>(),
            provider.GetRequiredService<ILogger<WasapiAudioCapture>>()));
        services.TryAddTransient<IAudioPlayback>(provider => new WasapiAudioPlayback(
            provider.GetRequiredService<AudioDiagnostics>(), provider.GetRequiredService<IAudioPlatformEventPublisher>(),
            provider.GetRequiredService<IAudioClock>(), provider.GetRequiredService<IWasapiPlaybackFactory>(),
            provider.GetRequiredService<ILogger<WasapiAudioPlayback>>()));
        return services;
    }
}
