using AudioStreamer.Engine.Configuration;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Engine.Runtime;

namespace AudioStreamer.Engine.Managers;

/// <summary>Owns node identity and lifecycle transitions; it never creates platform resources.</summary>
internal sealed class NodeManager : IAsyncDisposable
{
    private readonly EngineEventBus _events;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private EngineStatus _status = new(EngineLifecycleState.Stopped, 0);
    private Guid _nodeId;
    private string _nodeName = string.Empty;
    private int _disposeStarted;

    public NodeManager(EngineEventBus events) => _events = events;

    public EngineStatus Status => Volatile.Read(ref _status);
    public Guid NodeId => _nodeId;
    public string NodeName => Volatile.Read(ref _nodeName);

    public async Task<EngineResult<EngineStatus>> StartAsync(EngineSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_status.State is EngineLifecycleState.Running or EngineLifecycleState.Degraded)
                return EngineResult<EngineStatus>.Success(_status);
            if (_status.State is not (EngineLifecycleState.Stopped or EngineLifecycleState.Faulted))
                return EngineResult<EngineStatus>.Failure(EngineErrorCode.InvalidState, "node", "Node cannot start from its current state.");
            _nodeId = settings.NodeId;
            Volatile.Write(ref _nodeName, settings.NodeName);
            await SetStateAsync(EngineLifecycleState.Starting, null).ConfigureAwait(false);
            await SetStateAsync(EngineLifecycleState.Running, null).ConfigureAwait(false);
            return EngineResult<EngineStatus>.Success(_status);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<EngineResult<EngineStatus>> StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_status.State == EngineLifecycleState.Stopped)
                return EngineResult<EngineStatus>.Success(_status);
            await SetStateAsync(EngineLifecycleState.Stopping, null).ConfigureAwait(false);
            await SetStateAsync(EngineLifecycleState.Stopped, null).ConfigureAwait(false);
            return EngineResult<EngineStatus>.Success(_status);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<EngineResult<EngineStatus>> RestartAsync(RestartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 120)
            return EngineResult<EngineStatus>.Failure(EngineErrorCode.InvalidArgument, "node", "Restart reason must contain 1 to 120 characters.");
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_status.State is not (EngineLifecycleState.Running or EngineLifecycleState.Degraded))
                return EngineResult<EngineStatus>.Failure(EngineErrorCode.InvalidState, "node", "Only a running node can be restarted.");
            await SetStateAsync(EngineLifecycleState.Restarting, null).ConfigureAwait(false);
            await SetStateAsync(EngineLifecycleState.Starting, null).ConfigureAwait(false);
            await SetStateAsync(EngineLifecycleState.Running, null).ConfigureAwait(false);
            return EngineResult<EngineStatus>.Success(_status);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task ApplyNodeNameAsync(string nodeName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nodeName)) return;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _nodeName, nodeName);
            await _events.PublishAsync("node", "NodeNameChanged", _status,
                new NodeStateChangedPayload(_nodeId, _nodeName, _status), CancellationToken.None).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task FailStartupAsync(EngineError error)
    {
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { await SetStateAsync(EngineLifecycleState.Faulted, error.Code.ToString()).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    private async ValueTask SetStateAsync(EngineLifecycleState state, string? errorCode)
    {
        var next = new EngineStatus(state, checked(_status.Revision + 1), errorCode);
        Volatile.Write(ref _status, next);
        await _events.PublishAsync("node", "LifecycleChanged", next,
            new NodeStateChangedPayload(_nodeId, _nodeName, next), CancellationToken.None).ConfigureAwait(false);
    }
}
