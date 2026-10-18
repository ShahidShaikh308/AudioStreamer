using AudioStreamer.IPC.Transport;
using AudioStreamer.IPC.Contracts;

namespace AudioStreamer.Service;

/// <summary>Starts the IPC listener and keeps an Engine event subscription for the Service lifetime.</summary>
public sealed class IpcHostedService(PipeServer server, EngineIpcRequestHandler eventBridge,
    EventPublisher publisher, ILogger<IpcHostedService> logger) : IHostedService, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _eventPump;
    private Task? _heartbeatPump;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await server.StartAsync(CancellationToken.None).ConfigureAwait(false);
        _eventPump = RunEventPumpAsync(_lifetime.Token);
        _heartbeatPump = RunHeartbeatPumpAsync(_lifetime.Token);
        logger.LogInformation("IPC host started.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping IPC host.");
        await server.StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifetime.Cancel();
        var workers = new[] { _eventPump, _heartbeatPump }.Where(task => task is not null).Cast<Task>();
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        logger.LogInformation("IPC host stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifetime.Dispose();
        await server.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RunEventPumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await eventBridge.PublishEngineEventsAsync(publisher, cancellationToken).ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested)
                    logger.LogWarning("Engine event stream completed; reconnecting the IPC event bridge.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "IPC event bridge failed; Engine remains active and the bridge will retry.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private async Task RunHeartbeatPumpAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(IpcProtocol.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await publisher.PublishAsync("Heartbeat", new { Timestamp = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
