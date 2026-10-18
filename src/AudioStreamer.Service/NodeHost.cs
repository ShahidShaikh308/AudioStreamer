using AudioStreamer.Engine.Contracts;

namespace AudioStreamer.Service;

/// <summary>Owns the Engine lifetime inside the Service host. UI/client lifetimes are intentionally unrelated.</summary>
public sealed class NodeHost(IEngine engine, ILogger<NodeHost> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var result = await engine.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            var error = result.Error!;
            logger.LogCritical("Engine startup failed with {ErrorCode} in {Subsystem}: {Message}",
                error.Code, error.Subsystem, error.Message);
            throw new InvalidOperationException($"Engine startup failed: {error.Code}.");
        }
        logger.LogInformation("NodeHost started; Engine state is {State}.", result.Value!.State);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var result = await engine.StopAsync(CancellationToken.None).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            var error = result.Error!;
            logger.LogError("Engine shutdown reported {ErrorCode}: {Message}", error.Code, error.Message);
        }
        else
            logger.LogInformation("NodeHost stopped; Engine state is {State}.", result.Value!.State);
    }
}
