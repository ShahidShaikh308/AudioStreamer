using AudioStreamer.Engine.Managers;
using AudioStreamer.Engine.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioStreamer.Engine.Runtime;

internal sealed record EngineComponents(
    EngineOptions Options,
    EngineEventBus Events,
    StateStore StateStore,
    NodeManager NodeManager,
    SettingsManager SettingsManager,
    DeviceManager DeviceManager,
    PeerManager PeerManager,
    SessionManager SessionManager,
    StatisticsManager StatisticsManager,
    DiagnosticsManager DiagnosticsManager,
    IControlTransport? ControlTransport = null,
    AudioStreamer.Engine.Configuration.ControlPlaneOptions? ControlPlaneOptions = null,
    ILoggerFactory? LoggerFactory = null);

internal sealed record EngineInstanceIdentity(Guid Value);

internal static class EngineComposition
{
    public static EngineComponents Create(AudioStreamer.Engine.Configuration.IConfigurationStore store,
        Microsoft.Extensions.Logging.ILogger<AudioStreamer.Engine.Engine> logger, EngineOptions options,
        AudioStreamer.Engine.Contracts.IAudioDeviceCatalog catalog, TimeProvider clock,
        IAudioSessionRuntime? sessionRuntime = null, IControlTransport? controlTransport = null,
        AudioStreamer.Engine.Configuration.ControlPlaneOptions? controlPlaneOptions = null,
        ILoggerFactory? loggerFactory = null, IDiagnosticsMetricsProvider? diagnosticsMetrics = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(clock);
        options.Validate();
        var identity = new EngineInstanceIdentity(Guid.NewGuid());
        var events = new EngineEventBus(identity.Value, options);
        var stateStore = new StateStore(events, identity.Value);
        var version = EngineVersionProvider.Current;
        return new EngineComponents(options, events, stateStore,
            new NodeManager(events),
            new SettingsManager(store, options.DefaultNodeName, events),
            new DeviceManager(catalog, events),
            new PeerManager(events, options, clock),
            new SessionManager(events, sessionRuntime,
                (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SessionManager>()),
            new StatisticsManager(events),
            new DiagnosticsManager(events, options, version,
                diagnosticsMetrics ?? new ProcessDiagnosticsMetricsProvider(),
                (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<DiagnosticsManager>()),
            controlTransport, controlPlaneOptions, loggerFactory);
    }
}
