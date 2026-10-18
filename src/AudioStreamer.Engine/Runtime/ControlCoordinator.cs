using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using AudioStreamer.Control.Protocol;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Engine.Configuration;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.Engine.Runtime;

/// <summary>Engine-owned Control v1 discovery, routing, correlation, expiry, and recovery policy.</summary>
internal sealed class ControlCoordinator : IAsyncDisposable
{
    private readonly IEngineControlPlaneSink _engineSink;
    private readonly ControlPlaneOptions _options;
    private readonly byte[]? _controllerSecret;
    private readonly ControllerCommandReplayGuard _controllerReplayGuard = new();
    private readonly EngineEventBus _events;
    private readonly ILogger<ControlCoordinator> _logger;
    private readonly Channel<RemoteCommand> _remoteCommands = Channel.CreateBounded<RemoteCommand>(new BoundedChannelOptions(64)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly Channel<ulong> _peerRecoveries = Channel.CreateBounded<ulong>(new BoundedChannelOptions(64)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly Channel<byte> _networkChanges = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<ushort, PendingControlOperation> _pending = new();
    private readonly ConcurrentDictionary<(ulong Sender, ulong Receiver), byte> _activeRoutes = new();
    private readonly ConcurrentDictionary<(ulong Sender, ulong Receiver), byte> _desiredRoutes = new();
    private readonly ConcurrentDictionary<ulong, byte> _recoveringPeers = new();
    private CancellationTokenSource? _lifetime;
    private readonly IControlTransport _transport;
    private Task[] _workers = [];
    private Guid _localEngineId;
    private ulong _localProtocolId;
    private string _nodeName = "AudioStreamer";
    private long _startedTimestamp;
    private long _packetsSent, _bytesSent, _packetsReceived, _bytesReceived, _helloPackets, _heartbeats;
    private long _unknownPackets, _malformedPackets, _peerTimeouts, _reconnects;
    private long _controllerAuthenticationFailures;
    private long _roundTripTicks, _roundTripSamples;
    private int _running;
    private int _acceptCommands = 1;
    private long _transactionSequence;

    public ControlCoordinator(IEngineControlPlaneSink engineSink, ControlPlaneOptions options,
        IControlTransport transport, EngineEventBus events, ILogger<ControlCoordinator> logger)
    {
        _engineSink = engineSink ?? throw new ArgumentNullException(nameof(engineSink));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        try { _controllerSecret = _options.ControllerSecret; }
        catch (FormatException exception)
        {
            _logger.LogError(exception, "Authenticated controller commands are disabled because the configured controller secret is invalid.");
        }
    }

    public async Task<ImmutableArray<ControlPeerSnapshot>> GetPeersAsync(CancellationToken cancellationToken) =>
        (await _engineSink.GetControlPeersAsync(cancellationToken).ConfigureAwait(false)).Select(ToControlPeer).ToImmutableArray();

    public async Task<ControlPeerSnapshot?> GetPeerAsync(ulong protocolNodeId, CancellationToken cancellationToken)
    {
        var peer = await _engineSink.FindControlPeerAsync(protocolNodeId, cancellationToken).ConfigureAwait(false);
        return peer is null ? null : ToControlPeer(peer);
    }

    public async Task<ControlPlaneStatisticsSnapshot> GetStatisticsAsync(CancellationToken cancellationToken)
    {
        var started = Interlocked.Read(ref _startedTimestamp);
        var duration = started == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(started);
        var samples = Interlocked.Read(ref _roundTripSamples);
        var averageRtt = samples == 0 ? (double?)null :
            Interlocked.Read(ref _roundTripTicks) * 1000.0 / Stopwatch.Frequency / samples;
        return new(Interlocked.Read(ref _packetsSent), Interlocked.Read(ref _bytesSent),
            Interlocked.Read(ref _packetsReceived), Interlocked.Read(ref _bytesReceived),
            Interlocked.Read(ref _helloPackets), Interlocked.Read(ref _heartbeats),
            Interlocked.Read(ref _unknownPackets), Interlocked.Read(ref _malformedPackets),
            Interlocked.Read(ref _peerTimeouts), Interlocked.Read(ref _reconnects), (await _engineSink.GetControlPeersAsync(cancellationToken).ConfigureAwait(false)).Length,
            _activeRoutes.Count, duration, averageRtt);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _running) != 0) return;
            var node = await _engineSink.GetNodeSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!node.IsSuccess) throw new InvalidOperationException("Engine must be running before Control Plane startup.");
            _localEngineId = node.Value!.NodeId;
            _nodeName = node.Value.NodeName;
            _localProtocolId = ControlNodeIdentity.ToProtocolId(_localEngineId);
            await _transport.StartAsync(new ControlTransportOptions(_options.Transport.BindAddress,
                _options.Transport.Port, _options.Transport.OutboundQueueCapacity), cancellationToken).ConfigureAwait(false);
            _lifetime = new CancellationTokenSource();
            _startedTimestamp = Stopwatch.GetTimestamp();
            var token = _lifetime.Token;
            _workers = [ReceiveLoopAsync(token), HeartbeatLoopAsync(token), NetworkChangeLoopAsync(token), RemoteCommandLoopAsync(token), PeerRecoveryLoopAsync(token)];
            Volatile.Write(ref _running, 1);
            Volatile.Write(ref _acceptCommands, 1);
            await SendMessageAsync(CreateHello(), BroadcastEndpoint(), cancellationToken, waitForSend: false).ConfigureAwait(false);
            _logger.LogInformation("Control Plane started. NodeId={NodeId} ControlPort={ControlPort}", _localProtocolId, _options.Transport.Port);
            if (_controllerSecret is null)
                _logger.LogWarning("Authenticated controller commands are disabled because ControlPlane:ControllerSecret is not configured.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception,
                "Control Coordinator startup failed and rollback will run. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} HResult={HResult} StackTrace={StackTrace} InnerException={InnerException} InnerStackTrace={InnerStackTrace}",
                exception.GetType().FullName, exception.Message, exception.HResult, exception.StackTrace,
                exception.InnerException?.ToString(), exception.InnerException?.StackTrace);
            _lifetime?.Cancel();
            await _transport.StopAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(_workers).ConfigureAwait(false); } catch (OperationCanceledException) { }
            _lifetime?.Dispose();
            _lifetime = null;
            _workers = [];
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { await StopCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    public void Quiesce() => Volatile.Write(ref _acceptCommands, 0);

    public async Task RestartDiscoveryAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _running) == 0) throw new InvalidOperationException("Control Plane is not running.");
        if (Volatile.Read(ref _acceptCommands) == 0) return;
        TryQueueMessage(CreateHello(), BroadcastEndpoint());
        TryQueueMessage(new HeartbeatMessage(_localProtocolId, ControlNodeStatus.Online), BroadcastEndpoint());
    }

    public Task<EngineResult<OperationReceipt>> StartStreamAsync(StreamRequest request, CancellationToken cancellationToken) =>
        RouteFromIpcAsync(request, start: true, cancellationToken);

    public Task<EngineResult<OperationReceipt>> StopStreamAsync(StreamRequest request, CancellationToken cancellationToken) =>
        RouteFromIpcAsync(request, start: false, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        if (_controllerSecret is not null) CryptographicOperations.ZeroMemory(_controllerSecret);
        _lifecycle.Dispose();
    }

    private async Task<EngineResult<OperationReceipt>> RouteFromIpcAsync(StreamRequest request, bool start,
        CancellationToken cancellationToken)
    {
        if (request is null || request.SenderNodeId == Guid.Empty || request.ReceiverNodeId == Guid.Empty || request.SenderNodeId == request.ReceiverNodeId)
            return EngineResult<OperationReceipt>.Failure(EngineErrorCode.InvalidArgument, "control", "Stream route must contain distinct node identities.");
        var senderWire = ControlNodeIdentity.ToProtocolId(request.SenderNodeId);
        var receiverWire = ControlNodeIdentity.ToProtocolId(request.ReceiverNodeId);
        if (senderWire != _localProtocolId && receiverWire != _localProtocolId)
            return EngineResult<OperationReceipt>.Failure(EngineErrorCode.InvalidArgument, "control", "Stream route must include this node.");
        var remoteWire = senderWire == _localProtocolId ? receiverWire : senderWire;
        var remote = await _engineSink.FindControlPeerAsync(remoteWire, cancellationToken).ConfigureAwait(false);
        return await ContinueRouteAsync(request, start, senderWire, receiverWire, remoteWire, remote, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EngineResult<OperationReceipt>> ContinueRouteAsync(StreamRequest request, bool start,
        ulong senderWire, ulong receiverWire, ulong remoteWire, EngineControlPeer? remote,
        CancellationToken cancellationToken)
    {
        var route = (senderWire, receiverWire);
        if (remote is null && !start)
        {
            var localStop = await _engineSink.StopLocalStreamAsync(request, cancellationToken).ConfigureAwait(false);
            _activeRoutes.TryRemove(route, out _);
            _desiredRoutes.TryRemove(route, out _);
            return localStop;
        }
        if (remote is null)
            return EngineResult<OperationReceipt>.Failure(EngineErrorCode.NotFound, "control", "The remote peer is not available.", true);
        var localIsSender = senderWire == _localProtocolId;
        var remoteCapabilities = (ControlCapability)remote.Capabilities;
        var requiredRemoteCapability = localIsSender ? ControlCapability.Receiver : ControlCapability.Sender;
        if (start && (remoteCapabilities & requiredRemoteCapability) == 0)
            return EngineResult<OperationReceipt>.Failure(EngineErrorCode.CapabilityUnavailable, "control", "The peer does not advertise the required stream capability.");
        var localStarted = false;
        EngineResult<OperationReceipt>? localResult = null;
        if (start && !localIsSender)
        {
            localResult = await _engineSink.StartLocalStreamAsync(request, cancellationToken).ConfigureAwait(false);
            if (!localResult.IsSuccess)
                return localResult;
            localStarted = true;
        }
        if (!start)
        {
            localResult = await _engineSink.StopLocalStreamAsync(request, cancellationToken).ConfigureAwait(false);
            if (!localResult.IsSuccess) return localResult;
        }
        var transactionId = ReserveTransaction(senderWire, receiverWire, start);
        try
        {
            await SendMessageAsync(new StreamCommandMessage(start, senderWire, receiverWire, transactionId),
                remote.ControlEndpoint, cancellationToken).ConfigureAwait(false);
            var response = await _pending[transactionId].Completion.Task.WaitAsync(_options.SessionTimeout, cancellationToken).ConfigureAwait(false);
            var expectedResponse = start ? ControlStreamState.Running : ControlStreamState.Stopped;
            if (response.State != expectedResponse)
            {
                if (localStarted)
                {
                    _ = await _engineSink.StopLocalStreamAsync(request, CancellationToken.None).ConfigureAwait(false);
                }
                return EngineResult<OperationReceipt>.Failure(EngineErrorCode.TransportUnavailable, "control", "The peer did not accept the requested stream state.", true);
            }
            if (start && localIsSender)
            {
                localResult = await _engineSink.StartLocalStreamAsync(request, cancellationToken).ConfigureAwait(false);
                if (!localResult.IsSuccess)
                {
                    await SendMessageAsync(new StreamCommandMessage(false, senderWire, receiverWire, transactionId),
                        remote.ControlEndpoint, CancellationToken.None).ConfigureAwait(false);
                    return localResult;
                }
                localStarted = true;
            }
            if (start) _activeRoutes.TryAdd(route, 0); else _activeRoutes.TryRemove(route, out _);
            if (start) _desiredRoutes.TryAdd(route, 0); else _desiredRoutes.TryRemove(route, out _);
            await PublishAsync(start ? "SessionStarted" : "SessionStopped",
                new ControlSessionEventData(request.SenderNodeId, request.ReceiverNodeId, transactionId), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (localStarted)
            {
                _ = await _engineSink.StopLocalStreamAsync(request, CancellationToken.None).ConfigureAwait(false);
            }
            _activeRoutes.TryRemove(route, out _);
            try
            {
                await SendMessageAsync(new StreamCommandMessage(false, senderWire, receiverWire, transactionId),
                remote.ControlEndpoint, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) { _logger.LogWarning(exception, "Timed-out stream rollback could not reach peer."); }
            return EngineResult<OperationReceipt>.Failure(EngineErrorCode.TransportUnavailable, "control", "The peer did not confirm the stream operation before timeout.", true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (localStarted)
            {
                _ = await _engineSink.StopLocalStreamAsync(request, CancellationToken.None).ConfigureAwait(false);
            }
            _activeRoutes.TryRemove(route, out _);
            throw;
        }
        catch (Exception exception) when (exception is ObjectDisposedException or ControlTransportException)
        {
            _logger.LogWarning(exception, "Control request could not be sent.");
            if (localStarted)
            {
                _ = await _engineSink.StopLocalStreamAsync(request, CancellationToken.None).ConfigureAwait(false);
            }
            _activeRoutes.TryRemove(route, out _);
            return EngineResult<OperationReceipt>.Failure(EngineErrorCode.TransportUnavailable, "control", "The Control Plane could not send the stream request.", true);
        }
        finally { _pending.TryRemove(transactionId, out _); }
        return localResult ?? EngineResult<OperationReceipt>.Success(new OperationReceipt(Guid.NewGuid(), start ? "Streaming" : "Stopped"));
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 0) == 0) return;
        try { await SendMessageAsync(new GoodbyeMessage(_localProtocolId), BroadcastEndpoint(), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { _logger.LogDebug(exception, "Best-effort Control GOODBYE failed."); }
        if (_lifetime is not null) _lifetime.Cancel();
        foreach (var pending in _pending.Values)
            pending.Completion.TrySetCanceled(_lifetime?.Token ?? CancellationToken.None);
        await _transport.StopAsync(CancellationToken.None).ConfigureAwait(false);
        try { await Task.WhenAll(_workers).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime?.IsCancellationRequested == true) { }
        catch (ObjectDisposedException) { }
        _workers = [];
        while (_remoteCommands.Reader.TryRead(out _)) { }
        while (_peerRecoveries.Reader.TryRead(out _)) { }
        while (_networkChanges.Reader.TryRead(out _)) { }
        _activeRoutes.Clear();
        _desiredRoutes.Clear();
        _recoveringPeers.Clear();
        _lifetime?.Dispose();
        _lifetime = null;
        _logger.LogInformation("Control Plane stopped.");
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromMilliseconds(250);
        var recovering = false;
        try
        {
            await foreach (var transportEvent in _transport.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (transportEvent is ControlDatagramSent sent)
                {
                    Interlocked.Increment(ref _packetsSent);
                    Interlocked.Add(ref _bytesSent, sent.ByteCount);
                    continue;
                }

                if (transportEvent is ControlNetworkAvailabilityChanged)
                {
                    _networkChanges.Writer.TryWrite(1);
                    continue;
                }

                if (transportEvent is ControlTransportFault fault)
                {
                    Interlocked.Increment(ref _reconnects);
                    if (fault.Operation != "receive")
                    {
                        _logger.LogWarning("Control datagram send failed. ErrorCode={ErrorCode}", fault.ErrorCode);
                        continue;
                    }

                    _logger.LogWarning("Control socket receive failed; discovery will be retried. ErrorCode={ErrorCode}", fault.ErrorCode);
                    await PublishAsync("NetworkLost", new ControlNetworkLostEventData("control-socket-receive"), cancellationToken).ConfigureAwait(false);
                    recovering = true;
                    try
                    {
                        await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                        await _transport.StopAsync(CancellationToken.None).ConfigureAwait(false);
                        await _transport.StartAsync(new ControlTransportOptions(_options.Transport.BindAddress,
                            _options.Transport.Port, _options.Transport.OutboundQueueCapacity), cancellationToken).ConfigureAwait(false);
                        TryQueueMessage(CreateHello(), BroadcastEndpoint());
                        backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 5000));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                    catch (Exception retryException)
                    {
                        _logger.LogWarning(retryException, "Control socket recreation failed.");
                    }
                    continue;
                }

                if (transportEvent is not ControlDatagramReceived received) continue;
                Interlocked.Increment(ref _packetsReceived);
                Interlocked.Add(ref _bytesReceived, received.Payload.Length);
                var bytes = received.Payload;
                if (bytes.Length > ControlProtocolV1Codec.MaximumDatagramLength)
                { Interlocked.Increment(ref _malformedPackets); continue; }
                if (bytes.Length >= 3 && bytes.Span[2] == AuthenticatedControllerCommandCodec.Version)
                {
                    if (_controllerSecret is null)
                    { Interlocked.Increment(ref _malformedPackets); continue; }
                    try
                    {
                        var controllerCommand = AuthenticatedControllerCommandCodec.ParseAndVerify(bytes.Span, _controllerSecret);
                        if (!_controllerReplayGuard.TryAccept(controllerCommand.ControllerNodeId,
                                controllerCommand.Nonce, DateTimeOffset.UtcNow))
                        {
                            _logger.LogWarning("Rejected replayed authenticated controller command. ControllerNodeId={ControllerNodeId} SourceAddress={SourceAddress}",
                                controllerCommand.ControllerNodeId, received.Source.Address);
                            continue;
                        }
                        QueueRemoteCommand(controllerCommand, received.Source);
                    }
                    catch (FormatException)
                    {
                        Interlocked.Increment(ref _malformedPackets);
                        var failures = Interlocked.Increment(ref _controllerAuthenticationFailures);
                        if (failures == 1 || failures % 100 == 0)
                            _logger.LogWarning("Rejected invalid authenticated controller command. RejectionCount={RejectionCount} SourceAddress={SourceAddress}",
                                failures, received.Source.Address);
                    }
                    continue;
                }
                if (bytes.Length >= 4 && (bytes.Span[3] < (byte)ControlMessageType.Hello || bytes.Span[3] > (byte)ControlMessageType.Error))
                { Interlocked.Increment(ref _unknownPackets); continue; }
                ControlMessage message;
                try { message = ControlProtocolV1Codec.Parse(bytes.ToArray()); }
                catch (FormatException) { Interlocked.Increment(ref _malformedPackets); continue; }
                try { await HandleMessageAsync(message, received.Source, cancellationToken).ConfigureAwait(false); }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                { _logger.LogError(exception, "Control datagram processing failed; receive loop remains active."); }
                if (recovering)
                {
                    recovering = false;
                    backoff = TimeSpan.FromMilliseconds(250);
                    await PublishAsync("NetworkRecovered", new ControlEmptyEventData(), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        { _logger.LogError(exception, "Control transport event worker failed."); }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Volatile.Read(ref _acceptCommands) == 0) break;
                var expired = await _engineSink.ExpireControlPeersAsync(cancellationToken).ConfigureAwait(false);
                foreach (var peer in expired)
                {
                    Interlocked.Increment(ref _peerTimeouts);
                    _recoveringPeers.TryAdd(peer.ProtocolNodeId, 0);
                    RemoveActiveRoutes(peer.ProtocolNodeId);
                    await PublishAsync("PeerRemoved", new ControlPeerEventData(ToControlPeer(peer)), cancellationToken).ConfigureAwait(false);
                }
                var settings = await _engineSink.GetNodeSettingsAsync(cancellationToken).ConfigureAwait(false);
                if (settings.IsSuccess) _nodeName = settings.Value!.NodeName;
                TryQueueMessage(CreateHello(), BroadcastEndpoint());
                var localStatus = _activeRoutes.IsEmpty ? ControlNodeStatus.Online : ControlNodeStatus.Streaming;
                TryQueueMessage(new HeartbeatMessage(_localProtocolId, localStatus), BroadcastEndpoint());
                Interlocked.Increment(ref _heartbeats);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Control heartbeat worker failed.");
        }
    }

    private async Task NetworkChangeLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in _networkChanges.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _reconnects);
                await RestartDiscoveryAsync(cancellationToken).ConfigureAwait(false);
                await PublishAsync("NetworkRecovered", new ControlEmptyEventData(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Control network-change worker failed.");
        }
    }

    private async Task HandleMessageAsync(ControlMessage message, ControlTransportEndpoint source, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _acceptCommands) == 0 && message is not StreamCommandMessage) return;
        switch (message)
        {
            case HelloMessage hello:
                Interlocked.Increment(ref _helloPackets);
                if (hello.NodeId == _localProtocolId) return;
                if (ControlNodeIdentity.ToEnginePeerId(hello.NodeId) == _localEngineId)
                { Interlocked.Increment(ref _malformedPackets); return; }
                var existing = await _engineSink.FindControlPeerAsync(hello.NodeId, cancellationToken).ConfigureAwait(false);
                var added = existing is null;
                var engineId = ControlNodeIdentity.ToEnginePeerId(hello.NodeId);
                var peerUpdate = new EngineControlPeer(hello.NodeId, engineId, hello.Name, hello.Platform,
                    (ushort)hello.Capabilities, new ControlTransportEndpoint(source.Address, hello.ReceivePort),
                    new ControlTransportEndpoint(source.Address, _options.Transport.Port), (byte)ControlNodeStatus.Online,
                    DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
                await _engineSink.ApplyControlPeerAsync(peerUpdate, cancellationToken).ConfigureAwait(false);
                await PublishAsync(added ? "PeerAdded" : "PeerUpdated", new ControlPeerEventData(ToControlPeer(peerUpdate)), cancellationToken).ConfigureAwait(false);
                if (_recoveringPeers.TryRemove(hello.NodeId, out _)) _peerRecoveries.Writer.TryWrite(hello.NodeId);
                if (added)
                    TryQueueMessage(CreateHello(), new ControlTransportEndpoint(source.Address, _options.Transport.Port));
                break;
            case HeartbeatMessage heartbeat when heartbeat.NodeId != _localProtocolId:
                var before = await _engineSink.FindControlPeerAsync(heartbeat.NodeId, cancellationToken).ConfigureAwait(false);
                if (before is not null)
                {
                    var update = before with
                    {
                        AudioEndpoint = new ControlTransportEndpoint(source.Address, before.AudioEndpoint.Port),
                        ControlEndpoint = new ControlTransportEndpoint(source.Address, _options.Transport.Port),
                        Status = (byte)heartbeat.Status,
                        LastSeenUtc = DateTimeOffset.UtcNow,
                        LastSeenTimestamp = Stopwatch.GetTimestamp()
                    };
                    await _engineSink.ApplyControlPeerAsync(update, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(before.ControlEndpoint.Address, source.Address, StringComparison.OrdinalIgnoreCase))
                        await PublishAsync("PeerUpdated", new ControlPeerEventData(ToControlPeer(update)), cancellationToken).ConfigureAwait(false);
                }
                break;
            case GoodbyeMessage goodbye when goodbye.NodeId != _localProtocolId:
                await RemovePeerAsync(goodbye.NodeId, cancellationToken).ConfigureAwait(false);
                break;
            case StreamCommandMessage command:
                QueueRemoteCommand(command, source);
                break;
            case AuthenticatedControllerCommandMessage controllerCommand:
                QueueRemoteCommand(controllerCommand, source);
                break;
            case StreamStatusMessage:
                {
                    var status = (StreamStatusMessage)message;
                    if (status.TransactionId is { } transactionId && _pending.TryGetValue(transactionId, out var pending) &&
                        pending.SenderNodeId == status.SenderNodeId && pending.ReceiverNodeId == status.ReceiverNodeId)
                    {
                        Interlocked.Add(ref _roundTripTicks, Stopwatch.GetTimestamp() - pending.StartedTimestamp);
                        Interlocked.Increment(ref _roundTripSamples);
                        pending.Completion.TrySetResult(status);
                    }
                    var route = (status.SenderNodeId, status.ReceiverNodeId);
                    if (status.State == ControlStreamState.Running) _activeRoutes.TryAdd(route, 0);
                    else
                    {
                        _activeRoutes.TryRemove(route, out _);
                        if (status.State == ControlStreamState.Stopped) _desiredRoutes.TryRemove(route, out _);
                    }
                    await PublishAsync("ControlStreamStatus", new ControlStreamStatusEventData(status), cancellationToken).ConfigureAwait(false);
                    break;
                }
            case ErrorMessage error:
                if (error.TransactionId is { } errorTransaction && _pending.TryGetValue(errorTransaction, out var failed))
                    failed.Completion.TrySetResult(new StreamStatusMessage(failed.SenderNodeId, failed.ReceiverNodeId,
                        ControlStreamState.Failed, error.ErrorCode, error.TransactionId));
                await PublishAsync("ControlError", new ControlErrorEventData(error), cancellationToken).ConfigureAwait(false);
                break;
            default:
                Interlocked.Increment(ref _unknownPackets);
                break;
        }
    }

    private void QueueRemoteCommand(StreamCommandMessage command, ControlTransportEndpoint source)
    {
        var queued = new RemoteCommand(command.IsStart, command.SenderNodeId, command.ReceiverNodeId,
            command.TransactionId, source, IsAuthenticatedController: false, ControllerNodeId: 0);
        if (!_remoteCommands.Writer.TryWrite(queued))
            TryQueueMessage(new StreamStatusMessage(command.SenderNodeId, command.ReceiverNodeId,
                ControlStreamState.Failed, 3, command.TransactionId), new ControlTransportEndpoint(source.Address, _options.Transport.Port));
    }

    private void QueueRemoteCommand(AuthenticatedControllerCommandMessage command, ControlTransportEndpoint source)
    {
        var queued = new RemoteCommand(command.IsStart, command.SenderNodeId, command.ReceiverNodeId,
            command.TransactionId, source, IsAuthenticatedController: true, command.ControllerNodeId);
        if (!_remoteCommands.Writer.TryWrite(queued))
            TryQueueMessage(new StreamStatusMessage(command.SenderNodeId, command.ReceiverNodeId,
                ControlStreamState.Failed, 3, command.TransactionId), new ControlTransportEndpoint(source.Address, _options.Transport.Port));
    }

    private async Task HandleRemoteCommandAsync(RemoteCommand command,
        CancellationToken cancellationToken)
    {
        if (command.SenderNodeId != _localProtocolId && command.ReceiverNodeId != _localProtocolId) return;
        if (Volatile.Read(ref _acceptCommands) == 0)
        {
            await SendMessageAsync(new StreamStatusMessage(command.SenderNodeId, command.ReceiverNodeId,
                ControlStreamState.Failed, 2, command.TransactionId),
                new ControlTransportEndpoint(command.Source.Address, _options.Transport.Port), cancellationToken).ConfigureAwait(false);
            return;
        }
        var remoteProtocolId = command.SenderNodeId == _localProtocolId ? command.ReceiverNodeId : command.SenderNodeId;
        var peer = await _engineSink.FindControlPeerAsync(remoteProtocolId, cancellationToken).ConfigureAwait(false);
        if (peer is null || (!command.IsAuthenticatedController && !peer.ControlEndpoint.Address.Equals(command.Source.Address)) ||
            peer.Status == (byte)ControlNodeStatus.Error)
        {
            TryQueueMessage(new StreamStatusMessage(command.SenderNodeId, command.ReceiverNodeId,
                ControlStreamState.Failed, 4, command.TransactionId), new ControlTransportEndpoint(command.Source.Address, _options.Transport.Port));
            return;
        }
        var remoteCapabilities = (ControlCapability)peer.Capabilities;
        var requiredRemoteCapability = command.SenderNodeId == _localProtocolId
            ? ControlCapability.Receiver : ControlCapability.Sender;
        if (command.IsStart && (remoteCapabilities & requiredRemoteCapability) == 0)
        {
            TryQueueMessage(new StreamStatusMessage(command.SenderNodeId, command.ReceiverNodeId,
                ControlStreamState.Failed, 5, command.TransactionId), new ControlTransportEndpoint(command.Source.Address, _options.Transport.Port));
            return;
        }
        var sender = EngineId(command.SenderNodeId);
        var receiver = EngineId(command.ReceiverNodeId);
        var route = new StreamRequest(sender, receiver);
        var result = command.IsStart
            ? await _engineSink.StartLocalStreamAsync(route, cancellationToken).ConfigureAwait(false)
            : await _engineSink.StopLocalStreamAsync(route, cancellationToken).ConfigureAwait(false);
        var state = result.IsSuccess
            ? command.IsStart ? ControlStreamState.Running : ControlStreamState.Stopped
            : ControlStreamState.Failed;
        if (result.IsSuccess)
        {
            var streamRoute = (command.SenderNodeId, command.ReceiverNodeId);
            if (command.IsStart)
            {
                _activeRoutes.TryAdd(streamRoute, 0);
                _desiredRoutes.TryAdd(streamRoute, 0);
            }
            else
            {
                _activeRoutes.TryRemove(streamRoute, out _);
                _desiredRoutes.TryRemove(streamRoute, out _);
            }
        }
        var status = new StreamStatusMessage(command.SenderNodeId, command.ReceiverNodeId, state,
            result.IsSuccess ? (ushort)0 : (ushort)1, command.TransactionId);
        TryQueueMessage(status, new ControlTransportEndpoint(command.Source.Address, _options.Transport.Port));
        await PublishAsync(command.IsStart ? "SessionStarted" : "SessionStopped",
            new ControlRemoteSessionEventData(command.SenderNodeId, command.ReceiverNodeId, state.ToString(), command.TransactionId), cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoteCommandLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var command in _remoteCommands.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                await HandleRemoteCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Control stream-command worker failed.");
        }
    }

    private async Task PeerRecoveryLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var protocolNodeId in _peerRecoveries.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var route in _desiredRoutes.Keys.Where(route => route.Sender == protocolNodeId || route.Receiver == protocolNodeId).ToArray())
                {
                    var request = new StreamRequest(EngineId(route.Sender), EngineId(route.Receiver));
                    var result = await RouteFromIpcAsync(request, start: true, cancellationToken).ConfigureAwait(false);
                    if (!result.IsSuccess)
                        _logger.LogWarning("Stream recovery did not complete. PeerNodeId={PeerNodeId} Error={ErrorCode}",
                            protocolNodeId, result.Error?.Code);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Control peer recovery worker failed.");
        }
    }

    private async Task RemovePeerAsync(ulong protocolNodeId, CancellationToken cancellationToken)
    {
        var peer = await _engineSink.FindControlPeerAsync(protocolNodeId, cancellationToken).ConfigureAwait(false);
        if (peer is null) return;
        await _engineSink.RemoveControlPeerAsync(protocolNodeId, cancellationToken).ConfigureAwait(false);
        _recoveringPeers.TryAdd(protocolNodeId, 0);
        RemoveActiveRoutes(protocolNodeId);
        await PublishAsync("PeerRemoved", new ControlPeerEventData(ToControlPeer(peer)), cancellationToken).ConfigureAwait(false);
    }

    private void RemoveActiveRoutes(ulong protocolNodeId)
    {
        foreach (var route in _activeRoutes.Keys.Where(route => route.Sender == protocolNodeId || route.Receiver == protocolNodeId))
            _activeRoutes.TryRemove(route, out _);
    }

    private async Task SendMessageAsync(ControlMessage message, ControlTransportEndpoint endpoint, CancellationToken cancellationToken,
        bool waitForSend = true)
    {
        if (Volatile.Read(ref _running) == 0 && message is not GoodbyeMessage)
            throw new InvalidOperationException("Control Plane is not running.");
        var bytes = ControlProtocolV1Codec.Serialize(message);
        if (waitForSend)
            _ = await _transport.SendAsync(bytes, endpoint, cancellationToken).ConfigureAwait(false);
        else
            await _transport.QueueAsync(bytes, endpoint, cancellationToken).ConfigureAwait(false);
    }

    private void TryQueueMessage(ControlMessage message, ControlTransportEndpoint endpoint)
    {
        var bytes = ControlProtocolV1Codec.Serialize(message);
        if (!_transport.TrySend(bytes, endpoint))
            _logger.LogWarning("Control outbound queue is full; a best-effort message was dropped. Type={MessageType}", message.GetType().Name);
    }

    private ushort ReserveTransaction(ulong senderNodeId, ulong receiverNodeId, bool isStart)
    {
        for (var attempt = 0; attempt < ushort.MaxValue; attempt++)
        {
            var transactionId = unchecked((ushort)Interlocked.Increment(ref _transactionSequence));
            if (_pending.TryAdd(transactionId, new PendingControlOperation(senderNodeId, receiverNodeId, isStart, Stopwatch.GetTimestamp())))
                return transactionId;
        }
        throw new InvalidOperationException("All Control Protocol transaction IDs are in use.");
    }

    private async ValueTask PublishAsync(string name, ControlEventData payload, CancellationToken cancellationToken)
    {
        var status = await _engineSink.GetNodeStatusAsync(CancellationToken.None).ConfigureAwait(false);
        if (status.IsSuccess)
            await _events.PublishAsync("control", name, status.Value!, new ControlPlaneEventPayload(name, payload),
                cancellationToken).ConfigureAwait(false);
    }

    private HelloMessage CreateHello()
    {
        return new HelloMessage(_localProtocolId, _nodeName, _options.LocalPlatform,
            _options.LocalCapabilities, _options.AudioReceivePort);
    }

    private static ControlPeerSnapshot ToControlPeer(EngineControlPeer peer) => new(peer.ProtocolNodeId, peer.EngineNodeId,
        peer.Name, peer.Platform, peer.Capabilities, peer.ControlEndpoint.Address.ToString(),
        checked((ushort)peer.AudioEndpoint.Port), peer.ControlEndpoint.Port, peer.Status,
        ((ControlNodeStatus)peer.Status).ToString(),
        peer.LastSeenUtc, peer.LastSeenTimestamp, peer.ProtocolVersion);

    private Guid EngineId(ulong protocolId) => protocolId == _localProtocolId ? _localEngineId : ControlNodeIdentity.ToEnginePeerId(protocolId);
    private ControlTransportEndpoint BroadcastEndpoint() => new(_options.Transport.BroadcastAddress, _options.Transport.Port);
    private sealed record RemoteCommand(bool IsStart, ulong SenderNodeId, ulong ReceiverNodeId,
        ushort? TransactionId, ControlTransportEndpoint Source, bool IsAuthenticatedController,
        ulong ControllerNodeId);
    private sealed record PendingControlOperation(ulong SenderNodeId, ulong ReceiverNodeId, bool IsStart, long StartedTimestamp)
    {
        public TaskCompletionSource<StreamStatusMessage> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
