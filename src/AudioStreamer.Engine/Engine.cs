using System.Collections.Immutable;
using System.Net;
using AudioStreamer.Engine.Configuration;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Engine.Managers;
using AudioStreamer.Engine.Runtime;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.Engine;

/// <summary>Coordinates the platform-neutral managers and exposes the stable public Engine API.</summary>
public sealed class Engine : IEngine, IAsyncDisposable, IEngineControlPlaneSink, IEngineDomainEventSource
{
    private readonly EngineOptions _options;
    private readonly EngineEventBus _events;
    private readonly StateStore _stateStore;
    private readonly NodeManager _nodeManager;
    private readonly SettingsManager _settingsManager;
    private readonly DeviceManager _deviceManager;
    private readonly PeerManager _peerManager;
    private readonly SessionManager _sessionManager;
    private readonly StatisticsManager _statisticsManager;
    private readonly DiagnosticsManager _diagnosticsManager;
    private readonly ILogger<Engine> _logger;
    private readonly ControlCoordinator? _controlCoordinator;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private int _disposeStarted;
    private bool _disposed;

    /// <summary>Creates an Engine with platform-independent defaults for development and tests.</summary>
    public Engine(IConfigurationStore store, ILogger<Engine> logger, EngineOptions? options = null)
        : this(EngineComposition.Create(store, logger, options ?? new EngineOptions(),
            new UnavailableAudioDeviceCatalog(), TimeProvider.System), logger)
    {
    }

    internal Engine(EngineComponents components, ILogger<Engine> logger)
    {
        _options = components.Options;
        _events = components.Events;
        _stateStore = components.StateStore;
        _nodeManager = components.NodeManager;
        _settingsManager = components.SettingsManager;
        _deviceManager = components.DeviceManager;
        _peerManager = components.PeerManager;
        _sessionManager = components.SessionManager;
        _statisticsManager = components.StatisticsManager;
        _diagnosticsManager = components.DiagnosticsManager;
        _logger = logger;
        if ((components.ControlTransport is null) != (components.ControlPlaneOptions is null))
            throw new ArgumentException("Control transport and options must be configured together.", nameof(components));
        if (components.ControlTransport is not null)
        {
            if (_options.EffectivePeerExpiration <= components.ControlPlaneOptions!.HeartbeatInterval)
                throw new ArgumentException("Peer expiry must exceed the Control heartbeat interval.", nameof(components));
            _controlCoordinator = new ControlCoordinator(this, components.ControlPlaneOptions!, components.ControlTransport,
                _events, (components.LoggerFactory ?? throw new ArgumentException("LoggerFactory is required with Control Plane.", nameof(components)))
                    .CreateLogger<ControlCoordinator>());
        }
    }

    public async Task<EngineResult<EngineStatus>> StartAsync(CancellationToken cancellationToken)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_nodeManager.Status.State is EngineLifecycleState.Running or EngineLifecycleState.Degraded)
                return EngineResult<EngineStatus>.Success(_stateStore.Current.Status);

            var settings = await _settingsManager.LoadAsync(_nodeManager.Status, cancellationToken).ConfigureAwait(false);
            if (!settings.IsSuccess)
            {
                await _nodeManager.FailStartupAsync(settings.Error!).ConfigureAwait(false);
                await SynchronizeStateAsync(CancellationToken.None).ConfigureAwait(false);
                return EngineResult<EngineStatus>.Failure(settings.Error!.Code, settings.Error.Subsystem,
                    settings.Error.Message, settings.Error.IsRetryable);
            }

            var started = await _nodeManager.StartAsync(settings.Value!, cancellationToken).ConfigureAwait(false);
            if (!started.IsSuccess) return started;
            _diagnosticsManager.MarkStarted();
            await _statisticsManager.PublishCurrentAsync(started.Value!, CancellationToken.None).ConfigureAwait(false);
            await _diagnosticsManager.SampleAsync(started.Value!, CancellationToken.None).ConfigureAwait(false);
            await SynchronizeStateAsync(CancellationToken.None).ConfigureAwait(false);
            if (_controlCoordinator is not null)
            {
                try { await _controlCoordinator.StartAsync(cancellationToken).ConfigureAwait(false); }
                catch
                {
                    var stopped = await _nodeManager.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    if (stopped.IsSuccess)
                    {
                        await _sessionManager.ClearAsync(stopped.Value!, CancellationToken.None).ConfigureAwait(false);
                        await _peerManager.ClearAsync(stopped.Value!, CancellationToken.None).ConfigureAwait(false);
                        await SynchronizeStateAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    throw;
                }
            }
            _logger.LogInformation("AudioStreamer Engine started. NodeId={NodeId}", _stateStore.Current.NodeId);
            return EngineResult<EngineStatus>.Success(_stateStore.Current.Status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Engine startup failed.");
            return EngineResult<EngineStatus>.Failure(EngineErrorCode.InternalFailure, "engine", "Engine startup failed.", true);
        }
        finally { _commands.Release(); }
    }

    public async Task<EngineResult<EngineStatus>> StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken; // Once accepted, stop owns cleanup through completion.
        await _commands.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed || _nodeManager.Status.State == EngineLifecycleState.Stopped)
                return EngineResult<EngineStatus>.Success(_stateStore.Current.Status);
            Exception? controlStopFailure = null;
            _controlCoordinator?.Quiesce();
            if (_controlCoordinator is not null)
            {
                try { await _controlCoordinator.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception)
                {
                    controlStopFailure = exception;
                    _logger.LogError(exception, "Control Plane shutdown failed; Engine cleanup will continue.");
                }
            }
            var stopped = await _nodeManager.StopAsync(CancellationToken.None).ConfigureAwait(false);
            if (!stopped.IsSuccess) return stopped;
            await _sessionManager.ClearAsync(stopped.Value!, CancellationToken.None).ConfigureAwait(false);
            await _peerManager.ClearAsync(stopped.Value!, CancellationToken.None).ConfigureAwait(false);
            _diagnosticsManager.MarkStopped();
            await _diagnosticsManager.SampleAsync(stopped.Value!, CancellationToken.None).ConfigureAwait(false);
            await SynchronizeStateAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("AudioStreamer Engine stopped.");
            if (controlStopFailure is not null)
                return EngineResult<EngineStatus>.Failure(EngineErrorCode.ShutdownTimedOut, "control",
                    "Control Plane transport cleanup did not complete cleanly.", true);
            return EngineResult<EngineStatus>.Success(_stateStore.Current.Status);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Engine shutdown failed.");
            return EngineResult<EngineStatus>.Failure(EngineErrorCode.ShutdownTimedOut, "engine", "Engine cleanup did not complete cleanly.", true);
        }
        finally { _commands.Release(); }
    }

    public async Task<EngineResult<EngineStatus>> RestartAsync(RestartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_controlCoordinator is not null)
            {
                _controlCoordinator.Quiesce();
                await _controlCoordinator.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            var restarted = await _nodeManager.RestartAsync(request, cancellationToken).ConfigureAwait(false);
            if (!restarted.IsSuccess) return restarted;
            await _sessionManager.ClearAsync(restarted.Value!, CancellationToken.None).ConfigureAwait(false);
            await _peerManager.ClearAsync(restarted.Value!, CancellationToken.None).ConfigureAwait(false);
            _diagnosticsManager.MarkStarted();
            await _diagnosticsManager.SampleAsync(restarted.Value!, CancellationToken.None).ConfigureAwait(false);
            await SynchronizeStateAsync(CancellationToken.None).ConfigureAwait(false);
            if (_controlCoordinator is not null)
                await _controlCoordinator.StartAsync(cancellationToken).ConfigureAwait(false);
            return EngineResult<EngineStatus>.Success(_stateStore.Current.Status);
        }
        finally { _commands.Release(); }
    }

    public Task<EngineResult<EngineStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EngineResult<EngineStatus>.Success(_stateStore.Current.Status));
    }

    async Task IEngineControlPlaneSink.ApplyControlPeerAsync(EngineControlPeer peer, CancellationToken cancellationToken)
    {
        var previous = await _peerManager.FindControlPeerAsync(peer.ProtocolNodeId, cancellationToken).ConfigureAwait(false);
        await _peerManager.UpsertControlPeerAsync(peer, _stateStore.Current.Status, cancellationToken).ConfigureAwait(false);
        if (previous is not null && previous.EngineNodeId == peer.EngineNodeId &&
            !previous.AudioEndpoint.Equals(peer.AudioEndpoint))
            await _sessionManager.UpdatePeerEndpointAsync(_stateStore.Current.NodeId, peer.EngineNodeId,
                new IPEndPoint(IPAddress.Parse(peer.AudioEndpoint.Address), peer.AudioEndpoint.Port),
                _stateStore.Current.Status, cancellationToken).ConfigureAwait(false);
        await SynchronizeStateAsync(cancellationToken).ConfigureAwait(false);
    }

    Task<EngineResult<NodeSnapshot>> IEngineControlPlaneSink.GetNodeSnapshotAsync(CancellationToken cancellationToken) =>
        GetSnapshotAsync(cancellationToken);

    Task<EngineResult<SettingsSnapshot>> IEngineControlPlaneSink.GetNodeSettingsAsync(CancellationToken cancellationToken) =>
        GetSettingsAsync(cancellationToken);

    Task<EngineResult<EngineStatus>> IEngineControlPlaneSink.GetNodeStatusAsync(CancellationToken cancellationToken) =>
        GetStatusAsync(cancellationToken);

    Task<EngineResult<OperationReceipt>> IEngineControlPlaneSink.StartLocalStreamAsync(StreamRequest request,
        CancellationToken cancellationToken) => StartLocalStreamingAsync(request, cancellationToken);

    Task<EngineResult<OperationReceipt>> IEngineControlPlaneSink.StopLocalStreamAsync(StreamRequest request,
        CancellationToken cancellationToken) => StopLocalStreamingAsync(request, cancellationToken);

    async Task IEngineControlPlaneSink.RemoveControlPeerAsync(ulong protocolNodeId, CancellationToken cancellationToken)
    {
        var peer = await _peerManager.FindControlPeerAsync(protocolNodeId, cancellationToken).ConfigureAwait(false);
        if (peer is not null)
        {
            await _sessionManager.MarkPeerRecoveringAsync(_stateStore.Current.NodeId, peer.EngineNodeId,
                _stateStore.Current.Status, cancellationToken).ConfigureAwait(false);
            await _peerManager.RemoveAsync(peer.EngineNodeId, _stateStore.Current.Status, cancellationToken).ConfigureAwait(false);
        }
        await SynchronizeStateAsync(cancellationToken).ConfigureAwait(false);
    }

    Task<EngineControlPeer?> IEngineControlPlaneSink.FindControlPeerAsync(ulong protocolNodeId, CancellationToken cancellationToken) =>
        _peerManager.FindControlPeerAsync(protocolNodeId, cancellationToken);

    Task<ImmutableArray<EngineControlPeer>> IEngineControlPlaneSink.GetControlPeersAsync(CancellationToken cancellationToken) =>
        _peerManager.GetControlPeersAsync(cancellationToken);

    async Task<ImmutableArray<EngineControlPeer>> IEngineControlPlaneSink.ExpireControlPeersAsync(CancellationToken cancellationToken)
    {
        var expired = await _peerManager.ExpireControlPeersAsync(_stateStore.Current.Status, cancellationToken).ConfigureAwait(false);
        if (!expired.IsEmpty) await SynchronizeStateAsync(cancellationToken).ConfigureAwait(false);
        return expired;
    }

    public Task<EngineResult<NodeSnapshot>> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _stateStore.Current;
        if (snapshot.Status.State is not (EngineLifecycleState.Running or EngineLifecycleState.Degraded))
            return Task.FromResult(EngineResult<NodeSnapshot>.Failure(EngineErrorCode.InvalidState, "node", "Node snapshot is unavailable before startup."));
        return Task.FromResult(EngineResult<NodeSnapshot>.Success(new NodeSnapshot(snapshot.NodeId, snapshot.NodeName,
            snapshot.Status, snapshot.Settings?.Revision ?? 0, snapshot.EventWatermark)));
    }

    public Task<EngineResult<ImmutableArray<StreamSnapshot>>> GetStreamsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EngineResult<ImmutableArray<StreamSnapshot>>.Success(_stateStore.Current.Sessions));
    }

    public async Task<EngineResult<OperationReceipt>> StartStreamingAsync(StreamRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_stateStore.Current.Status.State is not (EngineLifecycleState.Running or EngineLifecycleState.Degraded))
            return EngineResult<OperationReceipt>.Failure(EngineErrorCode.InvalidState, "session", "Engine must be running before starting a stream.");
        if (_controlCoordinator is not null)
            return await _controlCoordinator.StartStreamAsync(request, cancellationToken).ConfigureAwait(false);
        return await StartLocalStreamingAsync(request, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<EngineResult<OperationReceipt>> StartLocalStreamingAsync(StreamRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_stateStore.Current.Status.State is not (EngineLifecycleState.Running or EngineLifecycleState.Degraded))
                return EngineResult<OperationReceipt>.Failure(EngineErrorCode.InvalidState, "session", "Engine must be running before starting a stream.");
            var snapshot = _stateStore.Current;
            if (request.SenderNodeId != snapshot.NodeId && request.ReceiverNodeId != snapshot.NodeId)
                return EngineResult<OperationReceipt>.Failure(EngineErrorCode.InvalidArgument, "session", "The stream route does not include this node.");
            var remoteNodeId = request.SenderNodeId == snapshot.NodeId ? request.ReceiverNodeId : request.SenderNodeId;
            var endpoint = await _peerManager.GetAudioEndpointAsync(remoteNodeId, cancellationToken).ConfigureAwait(false);
            if (endpoint is null)
                return EngineResult<OperationReceipt>.Failure(EngineErrorCode.NotFound, "peers", "The remote audio endpoint is not available.", true);
            var result = await _sessionManager.StartAsync(request, snapshot.NodeId, endpoint, snapshot.Status, cancellationToken).ConfigureAwait(false);
            await SynchronizeStateAsync(CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally { _commands.Release(); }
    }

    public async Task<EngineResult<OperationReceipt>> StopStreamingAsync(StreamRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_controlCoordinator is not null)
            return await _controlCoordinator.StopStreamAsync(request, cancellationToken).ConfigureAwait(false);
        return await StopLocalStreamingAsync(request, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<EngineResult<OperationReceipt>> StopLocalStreamingAsync(StreamRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var result = await _sessionManager.StopAsync(request, _stateStore.Current.Status, cancellationToken).ConfigureAwait(false);
            await SynchronizeStateAsync(CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally { _commands.Release(); }
    }

    public async Task<EngineResult<ImmutableArray<PeerSnapshot>>> GetPeersAsync(CancellationToken cancellationToken)
    {
        var result = await _peerManager.GetPeersAsync(_stateStore.Current.Status, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess) return result;
        await SynchronizeStateAsync(cancellationToken).ConfigureAwait(false);
        return EngineResult<ImmutableArray<PeerSnapshot>>.Success(_stateStore.Current.Peers);
    }

    public Task<EngineResult<StatisticsSnapshot>> GetStatisticsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EngineResult<StatisticsSnapshot>.Success(_stateStore.Current.Statistics));
    }

    public Task<EngineResult<SettingsSnapshot>> GetSettingsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = _stateStore.Current.Settings;
        return Task.FromResult(settings is null
            ? EngineResult<SettingsSnapshot>.Failure(EngineErrorCode.InvalidState, "configuration", "Settings are unavailable before startup.")
            : EngineResult<SettingsSnapshot>.Success(settings));
    }

    public async Task<EngineResult<SettingsSnapshot>> UpdateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_nodeManager.Status.State is not (EngineLifecycleState.Running or EngineLifecycleState.Degraded))
                return EngineResult<SettingsSnapshot>.Failure(EngineErrorCode.InvalidState, "configuration", "Settings cannot be updated before startup.");
            var result = await _settingsManager.UpdateAsync(patch, _nodeManager.Status, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess) return result;
            await _nodeManager.ApplyNodeNameAsync(result.Value!.NodeName, CancellationToken.None).ConfigureAwait(false);
            await SynchronizeStateAsync(CancellationToken.None).ConfigureAwait(false);
            return _stateStore.Current.Settings is { } current
                ? EngineResult<SettingsSnapshot>.Success(current)
                : EngineResult<SettingsSnapshot>.Failure(EngineErrorCode.InternalFailure, "configuration", "Committed settings were not projected into Engine state.");
        }
        finally { _commands.Release(); }
    }

    public async Task<EngineResult<DiagnosticsSnapshot>> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _stateStore.Current;
            if (_disposed)
                return EngineResult<DiagnosticsSnapshot>.Success(CreateFinalDiagnostics(current));

            EngineDiagnosticsSnapshot sampled;
            try
            {
                sampled = await _diagnosticsManager.SampleAsync(current.Status, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception,
                    "Diagnostics sampling failed; returning the last available snapshot. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} InnerException={InnerException}",
                    exception.GetType().FullName, exception.Message, exception.InnerException?.ToString());
                sampled = _diagnosticsManager.Current;
            }

            return EngineResult<DiagnosticsSnapshot>.Success(new DiagnosticsSnapshot(current.Status,
                sampled.Capabilities.IsDefault ? ImmutableArray<string>.Empty : sampled.Capabilities,
                GetDiagnosticsHealth(current.Status), sampled.Uptime, sampled.CpuPercent, sampled.WorkingSetBytes,
                sampled.Version ?? GetVersionInfo(), NormalizeDiagnosticsStatistics(current.Statistics.Counters),
                sampled.SafeErrorCode));
        }
        finally
        {
            _commands.Release();
        }
    }

    private DiagnosticsSnapshot CreateFinalDiagnostics(EngineRuntimeSnapshot current) => new(current.Status,
        ImmutableArray.Create("node-lifecycle", "configuration", "peer-registry", "session-state", "statistics", "diagnostics"),
        "Stopped", _diagnosticsManager.Current.Uptime, null, _diagnosticsManager.Current.WorkingSetBytes, GetVersionInfo(),
        NormalizeDiagnosticsStatistics(current.Statistics.Counters));

    private static string GetDiagnosticsHealth(EngineStatus status) => status.State switch
    {
        EngineLifecycleState.Running => "Healthy",
        EngineLifecycleState.Degraded => "Degraded",
        EngineLifecycleState.Faulted => "Faulted",
        _ => status.State.ToString()
    };

    private static ImmutableDictionary<string, long> NormalizeDiagnosticsStatistics(
        ImmutableDictionary<string, long>? statistics)
    {
        var builder = (statistics ?? ImmutableDictionary<string, long>.Empty)
            .ToBuilder();
        foreach (var name in DiagnosticStatisticNames)
            builder.TryAdd(name, 0);
        return builder.ToImmutable();
    }

    private static readonly string[] DiagnosticStatisticNames =
    [
        "packets_sent", "packets_received", "frames_captured", "frames_played", "bytes_sent", "bytes_received",
        "latency_microseconds_total", "latency_samples", "dropped_packets", "capture_drops", "playback_underruns",
        "queue_depth_frames", "queue_capacity_frames", "invalid_packets", "queue_depth_packets",
        "queue_duration_milliseconds", "packet_reordered", "packet_missing", "packet_recovered",
        "jitter_buffer_milliseconds", "latency_average_microseconds", "throughput_sent_bytes_per_second",
        "throughput_received_bytes_per_second"
    ];

    public async Task<EngineResult<ImmutableArray<AudioDeviceSnapshot>>> GetAudioDevicesAsync(CancellationToken cancellationToken)
    {
        var result = await _deviceManager.EnumerateAsync(_stateStore.Current.Status, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess) return result;
        await SynchronizeStateAsync(cancellationToken).ConfigureAwait(false);
        return EngineResult<ImmutableArray<AudioDeviceSnapshot>>.Success(_stateStore.Current.Devices);
    }

    public Task<EngineResult<OperationReceipt>> SelectAudioDevicesAsync(AudioDeviceSelection selection, CancellationToken cancellationToken) =>
        _deviceManager.SelectAsync(selection, cancellationToken);

    public async Task<EngineResult<OperationReceipt>> PauseAsync(LaneSelection lanes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lanes);
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _stateStore.Current;
            if (snapshot.Status.State is not (EngineLifecycleState.Running or EngineLifecycleState.Degraded))
                return EngineResult<OperationReceipt>.Failure(EngineErrorCode.InvalidState, "session", "Engine must be running before pausing audio.");
            var result = await _sessionManager.PauseAsync(snapshot.NodeId, lanes, snapshot.Status, cancellationToken).ConfigureAwait(false);
            await SynchronizeStateAsync(CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally { _commands.Release(); }
    }

    public async Task<EngineResult<OperationReceipt>> ResumeAsync(LaneSelection lanes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lanes);
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _stateStore.Current;
            if (snapshot.Status.State is not (EngineLifecycleState.Running or EngineLifecycleState.Degraded))
                return EngineResult<OperationReceipt>.Failure(EngineErrorCode.InvalidState, "session", "Engine must be running before resuming audio.");
            var result = await _sessionManager.ResumeAsync(snapshot.NodeId, lanes, snapshot.Status, cancellationToken).ConfigureAwait(false);
            await SynchronizeStateAsync(CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally { _commands.Release(); }
    }

    public Task<EngineResult<EngineStatus>> RestartDiscoveryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_controlCoordinator is not null)
            return RestartDiscoveryCoreAsync(cancellationToken);
        return Task.FromResult(EngineResult<EngineStatus>.Failure(EngineErrorCode.CapabilityUnavailable, "discovery",
            "Control Plane discovery is unavailable until its transport adapter is implemented."));
    }

    private async Task<EngineResult<EngineStatus>> RestartDiscoveryCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _controlCoordinator!.RestartDiscoveryAsync(cancellationToken).ConfigureAwait(false);
            return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ControlTransportException)
        {
            return EngineResult<EngineStatus>.Failure(EngineErrorCode.TransportUnavailable, "discovery",
                "Control Plane discovery could not be restarted.", true);
        }
    }

    public async Task<EngineResult<ImmutableArray<ControlPeerSnapshot>>> GetControlPeersAsync(CancellationToken cancellationToken)
    {
        if (_controlCoordinator is null)
            return EngineResult<ImmutableArray<ControlPeerSnapshot>>.Failure(EngineErrorCode.CapabilityUnavailable,
                "control", "Control Plane is unavailable.");
        try { return EngineResult<ImmutableArray<ControlPeerSnapshot>>.Success(await _controlCoordinator.GetPeersAsync(cancellationToken).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return EngineResult<ImmutableArray<ControlPeerSnapshot>>.Failure(EngineErrorCode.InternalFailure, "control", "Control peer registry is unavailable.", true); }
    }

    public async Task<EngineResult<ControlPeerSnapshot?>> GetControlPeerAsync(ulong protocolNodeId, CancellationToken cancellationToken)
    {
        if (_controlCoordinator is null)
            return EngineResult<ControlPeerSnapshot?>.Failure(EngineErrorCode.CapabilityUnavailable, "control", "Control Plane is unavailable.");
        try { return EngineResult<ControlPeerSnapshot?>.Success(await _controlCoordinator.GetPeerAsync(protocolNodeId, cancellationToken).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return EngineResult<ControlPeerSnapshot?>.Failure(EngineErrorCode.InternalFailure, "control", "Control peer lookup failed.", true); }
    }

    public async Task<EngineResult<ControlPlaneStatisticsSnapshot>> GetControlStatisticsAsync(CancellationToken cancellationToken)
    {
        if (_controlCoordinator is null)
            return EngineResult<ControlPlaneStatisticsSnapshot>.Failure(EngineErrorCode.CapabilityUnavailable, "control", "Control Plane is unavailable.");
        try { return EngineResult<ControlPlaneStatisticsSnapshot>.Success(await _controlCoordinator.GetStatisticsAsync(cancellationToken).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return EngineResult<ControlPlaneStatisticsSnapshot>.Failure(EngineErrorCode.InternalFailure, "control", "Control statistics are unavailable.", true); }
    }

    public EngineVersionInfo GetVersionInfo() => EngineVersionProvider.Current;

    public async IAsyncEnumerable<EngineEvent> SubscribeAsync(EventSubscription subscription,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var capacity = subscription.Capacity == 64 ? _options.DefaultEventCapacity : subscription.Capacity;
        await using var stream = _events.Subscribe(capacity);
        await foreach (var message in stream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return new EngineEvent(message.EngineInstanceId, message.Sequence, message.OccurredAt,
                message.Kind, message.Status);
    }

    async IAsyncEnumerable<EngineDomainEvent> IEngineDomainEventSource.SubscribeDomainEventsAsync(EventSubscription subscription,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var capacity = subscription.Capacity == 64 ? _options.DefaultEventCapacity : subscription.Capacity;
        await using var stream = _events.Subscribe(capacity);
        await foreach (var message in stream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return message;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        var stopResult = await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _commands.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _disposed = true;
            if (_controlCoordinator is not null)
                await _controlCoordinator.DisposeAsync().ConfigureAwait(false);
            await _events.DisposeAsync().ConfigureAwait(false);
            await _stateStore.DisposeAsync().ConfigureAwait(false);
            await _nodeManager.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _commands.Release();
        }

        if (!stopResult.IsSuccess)
            throw new InvalidOperationException($"Engine disposal encountered {stopResult.Error?.Code}: " +
                (stopResult.Error?.Message ?? "Engine shutdown did not complete cleanly."));
    }

    private async Task SynchronizeStateAsync(CancellationToken cancellationToken) =>
        await _stateStore.WaitForSequenceAsync(_events.CurrentSequence, cancellationToken).ConfigureAwait(false);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
