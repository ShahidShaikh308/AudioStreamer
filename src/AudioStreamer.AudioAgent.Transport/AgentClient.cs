using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading.Channels;
using AudioStreamer.AudioAgent.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.AudioAgent.Transport;

public sealed class AgentClient(IAgentTransport transport, AgentTransportOptions options,
    ILogger<AgentClient> logger) : BackgroundService, IAgentAudioClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<AgentChannel, IAgentConnection> _connections = new();
    private readonly ConcurrentDictionary<Guid, Channel<(AgentPcmMetadata Metadata, ReadOnlyMemory<byte> Pcm)>> _captureStreams = new();
    private readonly ConcurrentDictionary<Guid, AgentOperationRequest> _activeCaptures = new();
    private readonly ConcurrentDictionary<Guid, AgentOperationRequest> _activePlaybacks = new();
    private readonly ConcurrentDictionary<Guid, byte> _pausedCaptures = new();
    private readonly ConcurrentDictionary<Guid, byte> _pausedPlaybacks = new();
    private readonly ConcurrentDictionary<Guid, uint> _lastCaptureSequences = new();
    private CancellationTokenSource? _connectionLifetime;
    private Task? _captureReader;
    private DateTimeOffset _lastHeartbeat;
    private Guid _instanceId;
    private int _negotiatedVersion;
    private AgentReady? _lastReady;
    private int _isConnected;
    public bool IsConnected => Volatile.Read(ref _isConnected) != 0;
    public event Action<DeviceListResponse>? DevicesChanged;
    public event Action<AgentPlatformEvent>? PlatformEventReceived;
    public DateTimeOffset? LastHeartbeat => _lastHeartbeat == default ? null : _lastHeartbeat;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected) return;
            await DisconnectCoreAsync().ConfigureAwait(false);
            foreach (var channel in Enum.GetValues<AgentChannel>())
                _connections[channel] = await transport.ConnectAsync(channel, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Audio Agent transport connected. Channels={Channels}", _connections.Count);
            await HandshakeCoreAsync(cancellationToken).ConfigureAwait(false);
            _connectionLifetime = new CancellationTokenSource();
            _captureReader = ReadCaptureChannelAsync(_connections[AgentChannel.CapturePcm], _connectionLifetime.Token);
            foreach (var operation in _activeCaptures.Values)
            {
                _captureStreams.TryAdd(operation.StreamId, CreateCaptureQueue());
                var restoreRequest = operation with { InitialSequenceNumber = _lastCaptureSequences.GetValueOrDefault(operation.StreamId) };
                var response = await RequestCoreAsync<AgentOperationRequest, AgentOperationResponse>(AgentMessageType.CaptureStart,
                    restoreRequest, AgentMessageType.OperationResponse, cancellationToken).ConfigureAwait(false);
                if (!response.Succeeded) logger.LogWarning("Capture restoration was rejected. StreamId={StreamId} ErrorCode={ErrorCode}", operation.StreamId, response.ErrorCode);
                else if (_pausedCaptures.ContainsKey(operation.StreamId))
                    await RequestCoreAsync<AgentOperationRequest, AgentOperationResponse>(AgentMessageType.CapturePause,
                        new AgentOperationRequest(operation.StreamId, null), AgentMessageType.OperationResponse, cancellationToken).ConfigureAwait(false);
            }
            foreach (var operation in _activePlaybacks.Values)
            {
                var response = await RequestCoreAsync<AgentOperationRequest, AgentOperationResponse>(AgentMessageType.PlaybackStart,
                    operation, AgentMessageType.OperationResponse, cancellationToken).ConfigureAwait(false);
                if (!response.Succeeded) logger.LogWarning("Playback restoration was rejected. StreamId={StreamId} ErrorCode={ErrorCode}", operation.StreamId, response.ErrorCode);
                else if (_pausedPlaybacks.ContainsKey(operation.StreamId))
                    await RequestCoreAsync<AgentOperationRequest, AgentOperationResponse>(AgentMessageType.PlaybackPause,
                        new AgentOperationRequest(operation.StreamId, null), AgentMessageType.OperationResponse, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<AgentReady> HandshakeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _lastReady ?? await HandshakeCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async ValueTask<AgentReady> HandshakeCoreAsync(CancellationToken cancellationToken)
    {
        var control = _connections[AgentChannel.Control];
        var hello = new AgentHello(AgentProtocol.CurrentVersion, AgentProtocol.CurrentVersion, Guid.NewGuid());
        await SendJsonAsync(control, AgentMessageType.Hello, hello, cancellationToken).ConfigureAwait(false);
        var response = await control.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        ValidateVersion(response);
        if (response.MessageType != AgentMessageType.Ready) throw new InvalidDataException("Agent handshake did not return Ready.");
        var ready = JsonSerializer.Deserialize<AgentReady>(response.Payload.Span) ?? throw new InvalidDataException("Invalid AgentReady payload.");
        if (ready.NegotiatedVersion != AgentProtocol.CurrentVersion) throw new InvalidDataException("Agent protocol version mismatch.");
        _instanceId = ready.InstanceId;
        _negotiatedVersion = ready.NegotiatedVersion;
        _lastReady = ready;
        _lastHeartbeat = DateTimeOffset.UtcNow;
        Volatile.Write(ref _isConnected, 1);
        logger.LogInformation("Audio Agent handshake completed. InstanceId={InstanceId} ProtocolVersion={ProtocolVersion}", _instanceId, _negotiatedVersion);
        return ready;
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await DisconnectCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async ValueTask ShutdownAgentAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_connections.TryGetValue(AgentChannel.Control, out var control) || !control.IsConnected) return;
            await SendJsonAsync(control, AgentMessageType.Shutdown, new AgentShutdown("Service stopping."), cancellationToken).ConfigureAwait(false);
            // The heartbeat reader owns control-channel reads; process exit confirms graceful shutdown.
        }
        finally { _gate.Release(); }
    }

    private async ValueTask DisconnectCoreAsync()
    {
        Volatile.Write(ref _isConnected, 0);
        if (_connectionLifetime is not null)
        {
            await _connectionLifetime.CancelAsync().ConfigureAwait(false);
            _connectionLifetime.Dispose();
            _connectionLifetime = null;
        }
        foreach (var channel in _captureStreams.Values) channel.Writer.TryComplete(new IOException("Audio Agent disconnected."));
        _captureStreams.Clear();
        foreach (var connection in _connections.Values)
            try { await connection.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { logger.LogDebug(exception, "Audio Agent channel disposal failed."); }
        if (_connections.Count > 0) logger.LogWarning("Audio Agent transport disconnected. Channels={Channels}", _connections.Count);
        _connections.Clear();
        _lastReady = null;
        if (_captureReader is not null)
        {
            try { await _captureReader.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            _captureReader = null;
        }
    }

    public async ValueTask<DeviceListResponse> EnumerateDevicesAsync(CancellationToken cancellationToken) =>
        await RequestAsync<DeviceListRequest, DeviceListResponse>(AgentMessageType.DeviceListRequest,
            new DeviceListRequest(), AgentMessageType.DeviceListResponse, cancellationToken).ConfigureAwait(false);

    public ValueTask<AgentOperationResponse> SelectDevicesAsync(AgentSelectionRequest request, CancellationToken cancellationToken) =>
        RequestAsync<AgentSelectionRequest, AgentOperationResponse>(AgentMessageType.SelectionRequest, request,
            AgentMessageType.SelectionResponse, cancellationToken);

    public ValueTask<AgentOperationResponse> StartCaptureAsync(AgentOperationRequest request, CancellationToken cancellationToken)
    {
        _captureStreams.TryAdd(request.StreamId, CreateCaptureQueue());
        return StartCaptureCoreAsync(request, cancellationToken);
    }

    private async ValueTask<AgentOperationResponse> StartCaptureCoreAsync(AgentOperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await RequestAsync<AgentOperationRequest, AgentOperationResponse>(AgentMessageType.CaptureStart,
                request, AgentMessageType.OperationResponse, cancellationToken).ConfigureAwait(false);
            if (response.Succeeded) _activeCaptures[request.StreamId] = request;
            else if (_captureStreams.TryRemove(request.StreamId, out var failedQueue)) failedQueue.Writer.TryComplete();
            return response;
        }
        catch
        {
            if (_captureStreams.TryRemove(request.StreamId, out var failedQueue)) failedQueue.Writer.TryComplete();
            throw;
        }
    }

    public async ValueTask<AgentOperationResponse> StopCaptureAsync(Guid streamId, CancellationToken cancellationToken)
    {
        _activeCaptures.TryRemove(streamId, out _);
        _pausedCaptures.TryRemove(streamId, out _);
        try
        {
            return await RequestAsync<AgentOperationRequest, AgentOperationResponse>(AgentMessageType.CaptureStop,
                new AgentOperationRequest(streamId, null), AgentMessageType.OperationResponse, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_captureStreams.TryRemove(streamId, out var queue)) queue.Writer.TryComplete();
            _lastCaptureSequences.TryRemove(streamId, out _);
        }
    }

    public async ValueTask<AgentOperationResponse> PauseCaptureAsync(Guid streamId, bool pause, CancellationToken cancellationToken)
    {
        var response = await RequestAsync<AgentOperationRequest, AgentOperationResponse>(pause ? AgentMessageType.CapturePause : AgentMessageType.CaptureResume,
            new AgentOperationRequest(streamId, null), AgentMessageType.OperationResponse, cancellationToken).ConfigureAwait(false);
        if (response.Succeeded)
        {
            if (pause) _pausedCaptures[streamId] = 0;
            else _pausedCaptures.TryRemove(streamId, out _);
        }
        return response;
    }

    public async ValueTask<AgentOperationResponse> StartPlaybackAsync(AgentOperationRequest request, CancellationToken cancellationToken)
    {
        var response = await RequestAsync<AgentOperationRequest, AgentOperationResponse>(AgentMessageType.PlaybackStart,
            request, AgentMessageType.OperationResponse, cancellationToken).ConfigureAwait(false);
        if (response.Succeeded) _activePlaybacks[request.StreamId] = request;
        return response;
    }

    public async ValueTask<AgentOperationResponse> StopPlaybackAsync(Guid streamId, CancellationToken cancellationToken)
    {
        _activePlaybacks.TryRemove(streamId, out _);
        _pausedPlaybacks.TryRemove(streamId, out _);
        var response = await RequestAsync<AgentOperationRequest, AgentOperationResponse>(AgentMessageType.PlaybackStop,
            new AgentOperationRequest(streamId, null), AgentMessageType.OperationResponse, cancellationToken).ConfigureAwait(false);
        return response;
    }

    public async ValueTask<AgentOperationResponse> PausePlaybackAsync(Guid streamId, bool pause, CancellationToken cancellationToken)
    {
        var response = await RequestAsync<AgentOperationRequest, AgentOperationResponse>(pause ? AgentMessageType.PlaybackPause : AgentMessageType.PlaybackResume,
            new AgentOperationRequest(streamId, null), AgentMessageType.OperationResponse, cancellationToken).ConfigureAwait(false);
        if (response.Succeeded)
        {
            if (pause) _pausedPlaybacks[streamId] = 0;
            else _pausedPlaybacks.TryRemove(streamId, out _);
        }
        return response;
    }

    public async IAsyncEnumerable<(AgentPcmMetadata Metadata, ReadOnlyMemory<byte> Pcm)> ReadCaptureFramesAsync(
        Guid streamId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var channel = _captureStreams.GetOrAdd(streamId, static _ => CreateCaptureQueue());
            var available = true;
            while (available)
            {
                try { available = await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false); }
                catch (ChannelClosedException exception) when (!cancellationToken.IsCancellationRequested && exception.InnerException is not null)
                { throw new IOException("Audio Agent capture stream failed.", exception.InnerException); }
                catch (ChannelClosedException) when (!cancellationToken.IsCancellationRequested) { available = false; }
                if (!available) break;
                while (channel.Reader.TryRead(out var frame)) yield return frame;
            }
            if (!_activeCaptures.ContainsKey(streamId)) yield break;
            if (!IsConnected && _activeCaptures.ContainsKey(streamId))
                await Task.Delay(options.EffectiveReconnectDelay, cancellationToken).ConfigureAwait(false);
            else if (_activeCaptures.ContainsKey(streamId))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
            }
            else yield break;
        }
    }

    public async ValueTask WritePlaybackFrameAsync(AgentPcmMetadata metadata, ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(AgentChannel.PlaybackPcm, out var connection) || !connection.IsConnected)
        {
            Volatile.Write(ref _isConnected, 0);
            throw new IOException("Audio Agent playback channel is disconnected.");
        }
        try
        {
            await connection.SendAsync(new AgentFrame(AgentProtocol.CurrentVersion, AgentChannel.PlaybackPcm,
                AgentMessageType.PlaybackPcm, metadata.SequenceNumber, Guid.Empty, pcm, metadata), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Volatile.Write(ref _isConnected, 0);
            logger.LogWarning(exception, "Audio Agent playback channel failed; the supervisor will restart the transport.");
            throw;
        }
    }

    private async ValueTask<TResponse> RequestAsync<TRequest, TResponse>(AgentMessageType requestType, TRequest payload,
        AgentMessageType responseType, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await RequestCoreAsync<TRequest, TResponse>(requestType, payload, responseType, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async ValueTask<TResponse> RequestCoreAsync<TRequest, TResponse>(AgentMessageType requestType, TRequest payload,
        AgentMessageType responseType, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(AgentChannel.Control, out var control) || !control.IsConnected)
            throw new IOException("Audio Agent control channel is disconnected.");
        var correlationId = Guid.NewGuid();
        try
        {
            await control.SendAsync(new AgentFrame(AgentProtocol.CurrentVersion, AgentChannel.Control, requestType, 0,
                correlationId, JsonSerializer.SerializeToUtf8Bytes(payload)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Volatile.Write(ref _isConnected, 0);
            logger.LogWarning(exception, "Audio Agent control channel write failed; marking the connection unavailable.");
            throw;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.EffectiveHeartbeatTimeout);
        AgentFrame response;
        do
        {
            response = await ReceiveControlFrameAsync(control, timeout.Token).ConfigureAwait(false);
            if (response.CorrelationId != correlationId) throw new InvalidDataException("Audio Agent response correlation ID mismatch.");
            break;
        } while (true);
        if (response.MessageType == AgentMessageType.Error)
        {
            var error = JsonSerializer.Deserialize<AgentError>(response.Payload.Span);
            throw new AgentOperationException(error?.Code ?? "agent-error", error?.Message ?? "Audio Agent operation failed.", error?.IsRetryable ?? true);
        }
        if (response.MessageType != responseType) throw new InvalidDataException($"Unexpected Audio Agent response {response.MessageType}; expected {responseType}.");
        return JsonSerializer.Deserialize<TResponse>(response.Payload.Span) ?? throw new InvalidDataException("Invalid Audio Agent response payload.");
    }

    private static Channel<(AgentPcmMetadata Metadata, ReadOnlyMemory<byte> Pcm)> CreateCaptureQueue() =>
        Channel.CreateBounded<(AgentPcmMetadata, ReadOnlyMemory<byte>)>(new BoundedChannelOptions(8)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });

    private async Task ReadCaptureChannelAsync(IAgentConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                ValidateVersion(frame);
                if (frame.MessageType == AgentMessageType.Error)
                {
                    var error = JsonSerializer.Deserialize<AgentError>(frame.Payload.Span)
                        ?? throw new InvalidDataException("Invalid Audio Agent capture error payload.");
                    var streamId = frame.CorrelationId;
                    _activeCaptures.TryRemove(streamId, out _);
                    _pausedCaptures.TryRemove(streamId, out _);
                    _lastCaptureSequences.TryRemove(streamId, out _);
                    if (_captureStreams.TryRemove(streamId, out var failedQueue))
                        failedQueue.Writer.TryComplete(new IOException(error.Message));
                    logger.LogError("Audio Agent capture worker failed. StreamId={StreamId} ErrorCode={ErrorCode}", streamId, error.Code);
                    continue;
                }
                if (frame.MessageType != AgentMessageType.CapturePcm) throw new InvalidDataException("Unexpected message on capture PCM channel.");
                var decoded = AgentProtocolCodec.DecodePcm(frame.Payload);
                _lastCaptureSequences.AddOrUpdate(decoded.Metadata.StreamId, decoded.Metadata.SequenceNumber,
                    (_, previous) => unchecked((int)(decoded.Metadata.SequenceNumber - previous)) > 0 ? decoded.Metadata.SequenceNumber : previous);
                if (_captureStreams.TryGetValue(decoded.Metadata.StreamId, out var channel) &&
                    !channel.Writer.TryWrite(decoded)) logger.LogWarning("Capture PCM frame dropped by bounded service queue. StreamId={StreamId} Sequence={Sequence}", decoded.Metadata.StreamId, decoded.Metadata.SequenceNumber);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Volatile.Write(ref _isConnected, 0);
            logger.LogWarning(exception, "Audio Agent capture PCM channel disconnected.");
            foreach (var channel in _captureStreams.Values) channel.Writer.TryComplete(exception);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!IsConnected)
                {
                    await Task.Delay(options.EffectiveReconnectDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                await HeartbeatAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(options.EffectiveHeartbeatInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Audio Agent disconnected or heartbeat failed. InstanceId={InstanceId}", _instanceId);
                await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                // AudioAgentSupervisor owns process and transport recovery. This worker remains
                // passive until the supervisor establishes the next connection.
            }
        }
    }

    private async ValueTask HeartbeatAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_connections.TryGetValue(AgentChannel.Control, out var control) || !control.IsConnected)
                throw new IOException("Audio Agent control channel is disconnected.");
            foreach (var (channel, connection) in _connections)
                if (!connection.IsConnected)
                    throw new IOException($"Audio Agent {channel} channel is disconnected.");
            await SendJsonAsync(control, AgentMessageType.Heartbeat, new AgentHeartbeat(DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.EffectiveHeartbeatTimeout);
            var response = await ReceiveControlFrameAsync(control, timeout.Token).ConfigureAwait(false);
            ValidateVersion(response);
            if (response.MessageType != AgentMessageType.Heartbeat) throw new InvalidDataException("Unexpected message on heartbeat channel.");
            _lastHeartbeat = DateTimeOffset.UtcNow;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidDataException)
        {
            Volatile.Write(ref _isConnected, 0);
            logger.LogWarning(exception, "Audio Agent heartbeat or channel health check failed.");
            throw;
        }
        finally { _gate.Release(); }
    }

    private async ValueTask<AgentFrame> ReceiveControlFrameAsync(IAgentConnection control, CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await control.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            ValidateVersion(frame);
            if (frame.MessageType == AgentMessageType.DeviceChanged)
            {
                var devices = JsonSerializer.Deserialize<DeviceListResponse>(frame.Payload.Span)
                    ?? throw new InvalidDataException("Invalid Agent device-change payload.");
                try { DevicesChanged?.Invoke(devices); }
                catch (Exception exception) { logger.LogError(exception, "Audio Agent device-change event subscriber failed."); }
            }
            else if (frame.MessageType == AgentMessageType.PlatformEvent)
            {
                var audioEvent = JsonSerializer.Deserialize<AgentPlatformEvent>(frame.Payload.Span)
                    ?? throw new InvalidDataException("Invalid Agent audio event payload.");
                try { PlatformEventReceived?.Invoke(audioEvent); }
                catch (Exception exception) { logger.LogError(exception, "Audio Agent platform-event subscriber failed."); }
            }
            else return frame;
        }
    }

    private static ValueTask SendJsonAsync<T>(IAgentConnection connection, AgentMessageType type, T message, CancellationToken token) =>
        connection.SendAsync(new AgentFrame(AgentProtocol.CurrentVersion, connection.Channel, type, 0, Guid.NewGuid(),
            JsonSerializer.SerializeToUtf8Bytes(message)), token);

    private static void ValidateVersion(AgentFrame frame)
    {
        if (frame.Version != AgentProtocol.CurrentVersion) throw new InvalidDataException($"Unsupported Audio Agent protocol version {frame.Version}.");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    private sealed class AgentOperationException(string code, string message, bool retryable) : IOException(message)
    {
        public string Code { get; } = code;
        public bool Retryable { get; } = retryable;
    }
}
