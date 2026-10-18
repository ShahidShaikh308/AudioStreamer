using AudioStreamer.Engine.Contracts;
using AudioStreamer.Platform.Windows.Audio.Buffers;
using AudioStreamer.Platform.Windows.Audio.Clock;
using AudioStreamer.Platform.Windows.Audio.Diagnostics;
using AudioStreamer.Platform.Windows.Audio.Formats;
using AudioStreamer.Audio.Transport.Transport;
using AudioStreamer.Engine.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.Platform.Windows.Audio;

/// <summary>Registers Windows audio capabilities without adding audio/network policy to the platform adapter.</summary>
public static class WindowsAudioServiceCollectionExtensions
{
    public static IServiceCollection AddAudioStreamerWindowsAudio(this IServiceCollection services,
        WindowsAudioStreamOptions? streamOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<AudioDiagnostics>();
        services.TryAddScoped<IAudioDiagnostics>(provider => provider.GetRequiredService<AudioDiagnostics>());
        services.TryAddScoped<IAudioLatencyProvider>(provider => provider.GetRequiredService<AudioDiagnostics>());
        services.TryAddScoped<IAudioBufferPool, ArrayAudioBufferPool>();
        services.TryAddScoped<IAudioClock, StopwatchAudioClock>();
        services.TryAddSingleton<IAudioFormatConverter, PcmFormatConverter>();
        services.TryAddSingleton<IAudioMixer, PcmMixer>();
        services.TryAddTransient<IAudioDeviceCatalog, AgentAudioDeviceCatalog>();
        services.TryAddTransient<IAudioCapture, AgentAudioCapture>();
        services.TryAddTransient<IAudioPlayback, AgentAudioPlayback>();
        if (streamOptions is not null)
        {
            streamOptions.Validate();
            services.TryAddSingleton(streamOptions);
            services.TryAddSingleton<IAudioSessionRuntime, WindowsAudioSessionRuntime>();
        }
        return services;
    }
}
