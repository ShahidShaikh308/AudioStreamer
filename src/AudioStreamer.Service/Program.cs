using AudioStreamer.Engine;
using AudioStreamer.AudioAgent.Transport;
using AudioStreamer.Engine.Configuration;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.IPC.Dispatch;
using AudioStreamer.IPC.Protocol;
using AudioStreamer.IPC.Transport;
using AudioStreamer.Platform.Windows;
using AudioStreamer.Platform.Windows.Audio;
using AudioStreamer.Service;
using AudioStreamer.Service.ControlPlane;
using Microsoft.Extensions.Hosting;
using System.Globalization;

var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AudioStreamer");
string migratedRuntimeSettings;
try
{
    migratedRuntimeSettings = WindowsRuntimeConfigurationMigration.Prepare(dataDirectory: dataDirectory);
}
catch (Exception exception)
{
    System.Diagnostics.Trace.TraceError("AudioStreamer configuration migration failed: {0}", exception);
    Environment.ExitCode = 1;
    return;
}
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile(migratedRuntimeSettings, optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables(prefix: "AUDIOSTREAMER_");
builder.Configuration.AddCommandLine(args);
builder.Services.AddWindowsService(options => options.ServiceName = "AudioStreamer");
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(30);
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
});

builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Logging.AddProvider(new RotatingJsonFileLoggerProvider(Path.Combine(dataDirectory, "Logs")));
builder.Services.AddSingleton<IConfigurationStore, WindowsConfigurationStore>();
builder.Services.AddSingleton<ILegacyNodeIdentityMapper, LegacyNodeIdentityMapper>();
var controlOptions = ControlRuntimeOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton<IControlTransport, WindowsControlTransport>();
builder.Services.AddSingleton(new ControlPlaneOptions(
    new ControlTransportOptions(controlOptions.BindAddress.ToString(), controlOptions.Port, controlOptions.OutboundQueueCapacity),
    controlOptions.AudioReceivePort, controlOptions.HeartbeatInterval,
    controlOptions.SessionTimeout, "windows", ControllerSecretHex: controlOptions.ControllerSecretHex));
builder.Services.AddAudioStreamerWindowsAudio(WindowsAudioStreamOptions.FromConfiguration(builder.Configuration));
var agentTransportOptions = new AgentTransportOptions(ServiceIdentity: "NT SERVICE\\AudioStreamer",
    HeartbeatInterval: TimeSpan.FromSeconds(1), HeartbeatTimeout: TimeSpan.FromSeconds(3),
    ReconnectDelay: TimeSpan.FromSeconds(1));
builder.Services.AddAudioAgentClient(agentTransportOptions, startBackgroundService: false);
builder.Services.AddSingleton<AudioAgentClient>();
builder.Services.AddSingleton<IAudioAgentClient>(services => services.GetRequiredService<AudioAgentClient>());
builder.Services.AddSingleton<IAgentProcessLauncher, WindowsAgentProcessLauncher>();
builder.Services.AddSingleton(AudioAgentSupervisorOptions.Default);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AudioAgentSupervisor>();
builder.Services.AddSingleton<IAudioAgentSupervisor>(services => services.GetRequiredService<AudioAgentSupervisor>());
builder.Services.AddHostedService(services => services.GetRequiredService<AudioAgentSupervisor>());
builder.Services.AddAudioStreamerEngine(new EngineOptions(DefaultNodeName: Environment.MachineName,
    MaximumPeers: controlOptions.MaximumPeers,
    PeerExpiration: controlOptions.PeerTimeout));
builder.Services.AddHostedService<NodeHost>();
builder.Services.AddHostedService<AudioAgentDeviceChangeService>();
var ipcSection = builder.Configuration.GetSection("Ipc");
int IpcInteger(string key, int fallback) => ipcSection[key] is { } configured
    ? int.Parse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture)
    : fallback;
var defaultIpcOptions = new IpcServerOptions();
var ipcOptions = new IpcServerOptions
{
    ReadRateLimitCapacity = IpcInteger("ReadRateLimitCapacity", defaultIpcOptions.ReadRateLimitCapacity),
    ReadRateLimitTokensPerSecond = IpcInteger("ReadRateLimitTokensPerSecond", defaultIpcOptions.ReadRateLimitTokensPerSecond),
    MutationRateLimitCapacity = IpcInteger("MutationRateLimitCapacity", defaultIpcOptions.MutationRateLimitCapacity),
    MutationRateLimitTokensPerSecond = IpcInteger("MutationRateLimitTokensPerSecond", defaultIpcOptions.MutationRateLimitTokensPerSecond)
};
ipcOptions.Validate();
builder.Services.AddSingleton(ipcOptions);
builder.Services.AddSingleton<IpcServerState>();
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<EventPublisher>();
builder.Services.AddSingleton<EngineIpcRequestHandler>();
builder.Services.AddSingleton<IIpcRequestHandler>(services => services.GetRequiredService<EngineIpcRequestHandler>());
builder.Services.AddSingleton<IIpcHandshakeInfoProvider>(services => services.GetRequiredService<EngineIpcRequestHandler>());
builder.Services.AddSingleton<RequestDispatcher>();
builder.Services.AddSingleton<VersionHandshake>();
builder.Services.AddSingleton<PipeServer>();
builder.Services.AddSingleton<IpcHostedService>();
builder.Services.AddHostedService<IpcHostedService>(services => services.GetRequiredService<IpcHostedService>());

var host = builder.Build();
using var crashBoundary = new ServiceCrashBoundary(host.Services.GetRequiredService<ILogger<ServiceCrashBoundary>>());
try
{
    await host.RunAsync();
}
catch (Exception exception)
{
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AudioStreamer.Fatal")
        .LogCritical(exception,
            "AudioStreamer host terminated. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} HResult={HResult} StackTrace={StackTrace} InnerException={InnerException} InnerStackTrace={InnerStackTrace}",
            exception.GetType().FullName, exception.Message, exception.HResult, exception.StackTrace,
            exception.InnerException?.ToString(), exception.InnerException?.StackTrace);
    Environment.ExitCode = 1;
}
