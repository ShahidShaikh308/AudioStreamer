using AudioStreamer.Engine.Configuration;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Engine.Managers;
using AudioStreamer.Engine.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.Engine;

/// <summary>Registers one Engine instance and its manager graph in the host's dependency injection container.</summary>
public static class EngineServiceCollectionExtensions
{
    public static IServiceCollection AddAudioStreamerEngine(this IServiceCollection services, EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton<TimeProvider>(_ => TimeProvider.System);
        services.TryAddSingleton<IAudioDeviceCatalog, UnavailableAudioDeviceCatalog>();
        services.TryAddSingleton<EngineInstanceIdentity>(_ => new EngineInstanceIdentity(Guid.NewGuid()));
        services.TryAddSingleton<EngineEventBus>(provider => new EngineEventBus(
            provider.GetRequiredService<EngineInstanceIdentity>().Value,
            provider.GetRequiredService<EngineOptions>()));
        services.TryAddSingleton<StateStore>(provider => new StateStore(
            provider.GetRequiredService<EngineEventBus>(), provider.GetRequiredService<EngineInstanceIdentity>().Value));
        services.TryAddSingleton<SettingsManager>(provider => new SettingsManager(
            provider.GetRequiredService<IConfigurationStore>(), provider.GetRequiredService<EngineOptions>().DefaultNodeName,
            provider.GetRequiredService<EngineEventBus>()));
        services.TryAddSingleton<NodeManager>();
        services.TryAddSingleton<IAudioPlatformEventPublisher, EngineAudioPlatformEventPublisher>();
        services.TryAddSingleton<PeerManager>();
        services.TryAddSingleton<SessionManager>();
        services.TryAddSingleton<IAudioSessionRuntime, UnavailableAudioSessionRuntime>();
        services.TryAddSingleton<StatisticsManager>();
        services.TryAddSingleton<DeviceManager>();
        services.TryAddSingleton<IDiagnosticsMetricsProvider, ProcessDiagnosticsMetricsProvider>();
        services.TryAddSingleton<DiagnosticsManager>(provider => new DiagnosticsManager(
            provider.GetRequiredService<EngineEventBus>(), provider.GetRequiredService<EngineOptions>(),
            EngineVersionProvider.Current, provider.GetRequiredService<IDiagnosticsMetricsProvider>(),
            provider.GetRequiredService<ILogger<DiagnosticsManager>>()));
        services.TryAddSingleton<Engine>(provider => new Engine(new EngineComponents(
            provider.GetRequiredService<EngineOptions>(),
            provider.GetRequiredService<EngineEventBus>(),
            provider.GetRequiredService<StateStore>(),
            provider.GetRequiredService<NodeManager>(),
            provider.GetRequiredService<SettingsManager>(),
            provider.GetRequiredService<DeviceManager>(),
            provider.GetRequiredService<PeerManager>(),
            provider.GetRequiredService<SessionManager>(),
            provider.GetRequiredService<StatisticsManager>(),
            provider.GetRequiredService<DiagnosticsManager>(),
            provider.GetService<IControlTransport>(),
            provider.GetService<ControlPlaneOptions>(),
            provider.GetRequiredService<ILoggerFactory>()), provider.GetRequiredService<ILogger<Engine>>()));
        services.TryAddSingleton<IEngine>(provider => provider.GetRequiredService<Engine>());
        return services;
    }
}
