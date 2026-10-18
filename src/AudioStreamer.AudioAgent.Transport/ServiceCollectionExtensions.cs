using AudioStreamer.AudioAgent.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AudioStreamer.AudioAgent.Transport;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAudioAgentClient(this IServiceCollection services, AgentTransportOptions? options = null, bool startBackgroundService = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new AgentTransportOptions();
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton<IAgentTransport, NamedPipeAgentTransport>();
        services.TryAddSingleton<AgentClient>();
        services.TryAddSingleton<IAgentClient>(provider => provider.GetRequiredService<AgentClient>());
        services.TryAddSingleton<IAgentAudioClient>(provider => provider.GetRequiredService<AgentClient>());
        if (startBackgroundService) services.AddHostedService(provider => provider.GetRequiredService<AgentClient>());
        return services;
    }

    public static IServiceCollection AddAudioAgentServer(this IServiceCollection services, AgentTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new AgentTransportOptions();
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton<IAgentServer, NamedPipeAgentServer>();
        services.AddHostedService<AgentServerWorker>();
        return services;
    }
}
