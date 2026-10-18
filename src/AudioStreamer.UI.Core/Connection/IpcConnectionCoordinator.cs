using System.Collections.Immutable;
using AudioStreamer.IPC.Contracts;
using AudioStreamer.IPC.Protocol;
using AudioStreamer.IPC.Transport;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.UI.Core.Connection;

/// <summary>Owns the UI's pipe-client generations and restores state after every connection epoch.</summary>
public sealed class IpcConnectionCoordinator(ILoggerFactory loggerFactory, ILogger<IpcConnectionCoordinator> logger,
    AppStateStore appState)
    : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _retry = new(0, 1);
    private Task? _worker;
    private int _started;

    public event Action<ServiceConnectionStatus>? ConnectionChanged;
    public ServiceConnectionStatus Status { get; private set; } =
        new(ServiceConnectionState.Connecting, "Connecting to AudioStreamer");
    public ServiceSnapshot Snapshot => appState.Snapshot;
    public HelloResult? Handshake { get; private set; }
    public PipeClient? Client { get; private set; }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _worker = RunAsync(_lifetime.Token);
    }

    public void Retry()
    {
        if (_retry.CurrentCount == 0) _retry.Release();
    }

    public async Task<IpcEnvelope> RequestAsync<TPayload>(string messageType, TPayload payload,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = Client;
            if (Status.State != ServiceConnectionState.Connected || client is null)
                throw new IOException("AudioStreamer is not connected.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            return await client.RequestAsync(messageType, payload, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "IPC request failed. MessageType={MessageType}", messageType);
            throw;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var client = Client ?? throw new IOException("AudioStreamer is not connected.");
        var snapshot = await ReadSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        appState.Synchronize(snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        Retry();
        if (_worker is not null)
        {
            try { await _worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (Client is not null) await Client.DisposeAsync().ConfigureAwait(false);
        _retry.Dispose();
        _lifetime.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        var everConnected = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            PipeClient? client = null;
            try
            {
                SetStatus(new(attempt == 0 ? ServiceConnectionState.Connecting : ServiceConnectionState.Reconnecting,
                    attempt == 0 ? "Connecting to AudioStreamer" : "Reconnecting to AudioStreamer"));
                client = new PipeClient(loggerFactory.CreateLogger<PipeClient>());
                Client = client;
                Handshake = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                appState.BeginConnectionEpoch();
                SetStatus(new(ServiceConnectionState.Synchronizing, "Restoring your AudioStreamer state"));

                var subscribed = await RequestResultAsync<SubscriptionResult>(client, "Subscribe", new SubscribeRequest(), cancellationToken).ConfigureAwait(false);
                if (!subscribed.IsSubscribed) throw new IpcClientException(IpcErrors.InvalidRequest, "AudioStreamer did not accept the event subscription.");

                var snapshot = await ReadSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
                appState.Synchronize(snapshot);
                SetStatus(new(ServiceConnectionState.Connected, "Connected"));
                everConnected = true;
                attempt = 0;

                await foreach (var message in client.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
                {
                    appState.Apply(message);
                    if (message.MessageType == "SettingsChanged")
                    {
                        try
                        {
                            var settings = await RequestResultAsync<SettingsSnapshot>(client, "GetSettings",
                                new EmptyPayload(), cancellationToken).ConfigureAwait(false);
                            appState.ApplySettings(settings);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception exception)
                        {
                            logger.LogDebug(exception, "Settings changed but could not be refreshed; keeping the last known settings.");
                        }
                    }
                }
                throw new IOException("AudioStreamer closed the app connection.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogInformation("Service connection ended. ErrorType={ErrorType}", exception.GetType().Name);
                SetStatus(new(everConnected ? ServiceConnectionState.Reconnecting : ServiceConnectionState.ServiceUnavailable,
                    everConnected ? "Connection lost · retrying automatically" : "Could not connect · retrying automatically",
                    exception is IpcClientException ipc ? ipc.Code : "ServiceUnavailable"));
                attempt++;
            }
            finally
            {
                if (client is not null)
                {
                    if (ReferenceEquals(Client, client)) Client = null;
                    try { await client.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception exception) { logger.LogDebug("Pipe client cleanup failed. ErrorType={ErrorType}", exception.GetType().Name); }
                }
                Handshake = null;
            }

            var exponentialMilliseconds = Math.Min(30_000, 500 * Math.Pow(2, Math.Min(attempt, 6)));
            var jitterMilliseconds = Random.Shared.Next(0, 251);
            using var delay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timer = Task.Delay(TimeSpan.FromMilliseconds(exponentialMilliseconds + jitterMilliseconds), delay.Token);
            var retry = _retry.WaitAsync(delay.Token);
            await Task.WhenAny(timer, retry).ConfigureAwait(false);
            delay.Cancel();
            try { await Task.WhenAll(timer, retry).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
    }

    private async Task<ServiceSnapshot> ReadSnapshotAsync(PipeClient client, CancellationToken cancellationToken)
    {
        var nodeTask = RequestResultAsync<NodeSnapshot>(client, "GetSnapshot", new EmptyPayload(), cancellationToken);
        var settingsTask = RequestResultAsync<SettingsSnapshot>(client, "GetSettings", new EmptyPayload(), cancellationToken);
        var peersTask = RequestResultAsync<ImmutableArray<IpcPeerSnapshot>>(client, "GetPeers", new EmptyPayload(), cancellationToken);
        var streamsTask = RequestResultAsync<ImmutableArray<IpcStreamSnapshot>>(client, "GetStreams", new EmptyPayload(), cancellationToken);
        var statisticsTask = RequestStatisticsOrFallbackAsync(client, cancellationToken);
        var diagnosticsTask = RequestDiagnosticsOrFallbackAsync(client, cancellationToken);
        var versionsTask = RequestResultAsync<VersionSnapshot>(client, "GetVersion", new EmptyPayload(), cancellationToken);
        var controlTask = RequestControlStatisticsOrFallbackAsync(client, cancellationToken);
        await Task.WhenAll(nodeTask, settingsTask, peersTask, streamsTask, statisticsTask,
            diagnosticsTask, versionsTask, controlTask).ConfigureAwait(false);
        var node = await nodeTask.ConfigureAwait(false);
        var settings = await settingsTask.ConfigureAwait(false);
        var peers = await peersTask.ConfigureAwait(false);
        var streams = await streamsTask.ConfigureAwait(false);
        var statistics = await statisticsTask.ConfigureAwait(false);
        var diagnostics = await diagnosticsTask.ConfigureAwait(false);
        var versions = await versionsTask.ConfigureAwait(false);
        var controlStatistics = await controlTask.ConfigureAwait(false);
        return new ServiceSnapshot(node, settings, peers, streams,
            statistics.Counters ?? ImmutableDictionary<string, long>.Empty,
            diagnostics, versions, controlStatistics, DateTimeOffset.UtcNow);
    }

    private async Task<StatisticsPayload> RequestStatisticsOrFallbackAsync(PipeClient client,
        CancellationToken cancellationToken) =>
        await RequestOptionalMetricAsync(
            () => RequestResultAsync<StatisticsPayload>(client, "GetStatistics", new EmptyPayload(), cancellationToken),
            new StatisticsPayload(DateTimeOffset.UtcNow, ImmutableDictionary<string, long>.Empty),
            "audio statistics", cancellationToken, logger).ConfigureAwait(false)
        ?? new StatisticsPayload(DateTimeOffset.UtcNow, ImmutableDictionary<string, long>.Empty);

    private async Task<ControlStatisticsSnapshot?> RequestControlStatisticsOrFallbackAsync(PipeClient client,
        CancellationToken cancellationToken) =>
        await RequestOptionalMetricAsync(
            () => RequestResultAsync<ControlStatisticsSnapshot>(client, "GetControlStatistics", new EmptyPayload(), cancellationToken),
            null, "network statistics", cancellationToken, logger).ConfigureAwait(false);

    internal static async Task<T?> RequestOptionalMetricAsync<T>(Func<Task<T>> request, T? fallback,
        string metricName, CancellationToken cancellationToken, ILogger logger) where T : class
    {
        try { return await request().ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is OperationCanceledException or IpcClientException or
            System.Text.Json.JsonException or ObjectDisposedException or IOException or TimeoutException)
        {
            logger.LogDebug(exception, "Optional {MetricName} are unavailable; continuing snapshot synchronization.", metricName);
            return fallback;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Optional {MetricName} failed; continuing snapshot synchronization. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} InnerException={InnerException}",
                metricName, exception.GetType().FullName, exception.Message, exception.InnerException?.ToString());
            return fallback;
        }
    }

    private async Task<DiagnosticsSnapshot> RequestDiagnosticsOrFallbackAsync(PipeClient client,
        CancellationToken cancellationToken)
    {
        return await GetDiagnosticsOrFallbackAsync(
            token => RequestResultAsync<DiagnosticsSnapshot>(client, "GetDiagnostics", new EmptyPayload(), token),
            cancellationToken, logger).ConfigureAwait(false);
    }

    internal static async Task<DiagnosticsSnapshot> GetDiagnosticsOrFallbackAsync(
        Func<CancellationToken, Task<DiagnosticsSnapshot>> request, CancellationToken cancellationToken,
        ILogger logger)
    {
        try { return await request(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException exception)
        {
            logger.LogDebug(exception, "Diagnostics request timed out; continuing snapshot synchronization.");
        }
        catch (Exception exception) when (exception is IpcClientException or System.Text.Json.JsonException or
            ObjectDisposedException or IOException or TimeoutException)
        {
            logger.LogDebug(exception,
                "Service diagnostics are unavailable; continuing snapshot synchronization. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage}",
                exception.GetType().FullName, exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Diagnostics could not be loaded; continuing snapshot synchronization. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} InnerException={InnerException}",
                exception.GetType().FullName, exception.Message, exception.InnerException?.ToString());
        }

        return new DiagnosticsSnapshot("Unavailable", ImmutableArray<string>.Empty,
            WorkingSetBytes: GetCurrentWorkingSet(), Statistics: ImmutableDictionary<string, long>.Empty);
    }

    private static long? GetCurrentWorkingSet()
    {
        try { return Environment.WorkingSet; }
        catch (Exception) { return null; }
    }

    private static async Task<T> RequestResultAsync<T>(PipeClient client, string messageType, object payload,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var envelope = await client.RequestAsync(messageType, payload, timeout.Token).ConfigureAwait(false);
        var result = IpcSerializer.DeserializePayload<IpcOperationResult<T>>(envelope.Payload);
        if (result.Result != "ok" || result.Data is null)
            throw new IpcClientException(result.Error?.Code ?? IpcErrors.InternalError,
                result.Error?.Message ?? $"Service could not provide {messageType}.");
        return result.Data!;
    }

    private void SetStatus(ServiceConnectionStatus status)
    {
        Status = status with { ChangedAt = DateTimeOffset.UtcNow };
        ConnectionChanged?.Invoke(Status);
    }
}
