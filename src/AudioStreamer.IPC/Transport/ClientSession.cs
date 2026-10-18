using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Threading.Channels;
using AudioStreamer.IPC.Contracts;
using AudioStreamer.IPC.Dispatch;
using AudioStreamer.IPC.Protocol;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.IPC.Transport;

public sealed class ClientSession : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly RequestDispatcher _dispatcher;
    private readonly VersionHandshake _handshake;
    private readonly IpcServerOptions _options;
    private readonly ILogger<ClientSession> _logger;
    private readonly Action<ClientSession> _onCompleted;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RequestIdTracker _requestIds = new(1024);
    private readonly IpcClientRequestRateLimiter _rateLimiter;
    private Channel<IpcEnvelope>? _outbound;
    private Task? _runTask;
    private IpcClientIdentity? _identity;
    private int _terminationStarted;

    public ClientSession(Guid connectionId, NamedPipeServerStream pipe, RequestDispatcher dispatcher,
        VersionHandshake handshake, IpcServerOptions options, ILogger<ClientSession> logger,
        Action<ClientSession> onCompleted)
    {
        ConnectionId = connectionId;
        _pipe = pipe;
        _dispatcher = dispatcher;
        _handshake = handshake;
        _options = options;
        _rateLimiter = new IpcClientRequestRateLimiter(options);
        _logger = logger;
        _onCompleted = onCompleted;
    }

    public Guid ConnectionId { get; }
    public Guid ConnectionEpoch { get; private set; }
    public IpcClientSubscription Subscription { get; } = new();
    public Task Completion => _runTask ?? Task.CompletedTask;

    public void Start() => _runTask = RunAsync(_lifetime.Token);

    public bool TryQueueEvent(IpcEnvelope envelope) =>
        _outbound is { } outbound && !_lifetime.IsCancellationRequested && outbound.Writer.TryWrite(envelope);

    public void Abort(string reason)
    {
        if (Interlocked.Exchange(ref _terminationStarted, 1) != 0) return;
        _logger.LogWarning("Closing IPC connection {ConnectionId}: {Reason}", ConnectionId, reason);
        _lifetime.Cancel();
        _pipe.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Abort("Session disposed.");
        if (_runTask is not null)
        {
            try { await _runTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var identity = WindowsPipeFactory.AuthenticateClient(_pipe);
            _identity = identity;
            var reader = new ResponseReader(_pipe);
            using var handshakeToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeToken.CancelAfter(_options.HandshakeTimeout);
            var hello = await reader.ReadAsync(handshakeToken.Token, _options.IncompleteFrameTimeout).ConfigureAwait(false);
            if (hello is null) return;

            var negotiated = await _handshake.NegotiateAsync(hello, identity, handshakeToken.Token).ConfigureAwait(false);
            ConnectionEpoch = negotiated.ConnectionEpoch;
            var writer = new ResponseWriter(_pipe);
            await writer.WriteHandshakeAsync(negotiated.Response, cancellationToken).ConfigureAwait(false);
            if (!negotiated.Accepted) return;

            _outbound = Channel.CreateBounded<IpcEnvelope>(new BoundedChannelOptions(_options.OutboundQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            var queuedReads = Channel.CreateBounded<IpcEnvelope>(new BoundedChannelOptions(_options.RequestQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
            var queuedMutations = Channel.CreateBounded<IpcEnvelope>(new BoundedChannelOptions(_options.RequestQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
            var requestQueueSlots = new SemaphoreSlim(_options.RequestQueueCapacity, _options.RequestQueueCapacity);

            _logger.LogInformation("IPC client connected. ConnectionId={ConnectionId} Role={Role}",
                ConnectionId, _identity.IsAdministrator ? "administrator" : "user");

            var writerTask = writer.RunAsync(_outbound.Reader, cancellationToken);
            var workerTasks = Enumerable.Range(0, _options.RequestWorkersPerClient)
                .Select(_ => RequestWorkerAsync(queuedReads.Reader, requestQueueSlots, cancellationToken)).ToArray();
            var mutationWorker = MutationWorkerAsync(queuedMutations.Reader, requestQueueSlots, cancellationToken);
            var readerTask = ReadRequestsAsync(reader, queuedReads.Writer, queuedMutations.Writer,
                requestQueueSlots, cancellationToken);
            var heartbeatTask = MonitorHeartbeatAsync(cancellationToken);

            try { await readerTask.ConfigureAwait(false); }
            finally
            {
                queuedReads.Writer.TryComplete();
                queuedMutations.Writer.TryComplete();
                _lifetime.Cancel();
                _outbound.Writer.TryComplete();
                _pipe.Dispose();
                try
                {
                    await Task.WhenAll(workerTasks.Append(mutationWorker).Append(writerTask).Append(heartbeatTask))
                        .WaitAsync(_options.ShutdownDrainTimeout).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (TimeoutException)
                {
                    _logger.LogWarning("IPC session workers exceeded the shutdown deadline. ConnectionId={ConnectionId}", ConnectionId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("IPC client handshake deadline elapsed. ConnectionId={ConnectionId}", ConnectionId);
        }
        catch (EndOfStreamException) { }
        catch (InvalidDataException exception)
        {
            _logger.LogWarning("Invalid IPC data; closing client. ConnectionId={ConnectionId} Detail={Detail}",
                ConnectionId, exception.Message);
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException exception)
        {
            _logger.LogInformation("IPC client disconnected. ConnectionId={ConnectionId} Detail={Detail}", ConnectionId, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning("IPC client authorization failed. ConnectionId={ConnectionId} Detail={Detail}", ConnectionId, exception.Message);
        }
        catch (IpcProtocolException exception)
        {
            _logger.LogWarning("IPC protocol failure. ConnectionId={ConnectionId} Code={ErrorCode}", ConnectionId, exception.Code);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "IPC session failed. ConnectionId={ConnectionId}", ConnectionId);
        }
        finally
        {
            Interlocked.Exchange(ref _terminationStarted, 1);
            _lifetime.Cancel();
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _logger.LogInformation("IPC client disconnected. ConnectionId={ConnectionId}", ConnectionId);
            _onCompleted(this);
        }
    }

    private async Task ReadRequestsAsync(ResponseReader reader, ChannelWriter<IpcEnvelope> reads,
        ChannelWriter<IpcEnvelope> mutations, SemaphoreSlim queueSlots, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var request = await reader.ReadAsync(cancellationToken, _options.IncompleteFrameTimeout).ConfigureAwait(false);
                if (request is null) break;
                if (request.RequestId is { } id && !_requestIds.TryAdd(id))
                {
                    _logger.LogWarning("Duplicate IPC request ID; closing client. ConnectionId={ConnectionId}", ConnectionId);
                    break;
                }
                await queueSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                var destination = IpcRequestClassifier.IsMutation(request.MessageType) ? mutations : reads;
                try { await destination.WriteAsync(request, cancellationToken).ConfigureAwait(false); }
                catch
                {
                    queueSlots.Release();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IpcProtocolException exception)
        {
            _logger.LogWarning("Malformed IPC frame; closing client. ConnectionId={ConnectionId} Code={ErrorCode}", ConnectionId, exception.Code);
        }
        catch (EndOfStreamException) { }
        finally
        {
            reads.TryComplete();
            mutations.TryComplete();
        }
    }

    private async Task RequestWorkerAsync(ChannelReader<IpcEnvelope> inbound, SemaphoreSlim queueSlots,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in inbound.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                queueSlots.Release();
                await DispatchAndQueueResponseAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "IPC request worker failed. ConnectionId={ConnectionId}", ConnectionId);
            Abort("Request worker failed.");
        }
    }

    private async Task MutationWorkerAsync(ChannelReader<IpcEnvelope> inbound, SemaphoreSlim queueSlots,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in inbound.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                queueSlots.Release();
                await DispatchAndQueueResponseAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "IPC mutation worker failed. ConnectionId={ConnectionId}", ConnectionId);
            Abort("Mutation worker failed.");
        }
    }

    private async Task DispatchAndQueueResponseAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var response = await _dispatcher.DispatchAsync(request, ConnectionEpoch, _identity!, Subscription,
            _rateLimiter, cancellationToken).ConfigureAwait(false);
        if (_outbound is null || !_outbound.Writer.TryWrite(response))
            Abort("Outbound response queue capacity was exceeded.");
    }

    private async Task MonitorHeartbeatAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Subscription.TimeSinceHeartbeat >= TimeSpan.FromTicks(_options.HeartbeatInterval.Ticks * _options.MissedHeartbeatLimit))
                {
                    Abort("Client heartbeat timeout.");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private sealed class ResponseReader(Stream stream)
    {
        public ValueTask<IpcEnvelope?> ReadAsync(CancellationToken cancellationToken, TimeSpan timeout) =>
            IpcFrameCodec.ReadAsync(stream, cancellationToken, timeout);
    }

    private sealed class RequestIdTracker(int capacity)
    {
        private readonly object _sync = new();
        private readonly HashSet<Guid> _ids = [];
        private readonly Queue<Guid> _order = new();

        public bool TryAdd(Guid id)
        {
            lock (_sync)
            {
                if (!_ids.Add(id)) return false;
                _order.Enqueue(id);
                if (_order.Count > capacity) _ids.Remove(_order.Dequeue());
                return true;
            }
        }
    }
}
