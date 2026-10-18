using System.Collections.Immutable;
using System.Net;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Engine.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioStreamer.Engine.Managers;

internal enum StreamSessionState { Idle, Negotiating, Preparing, Streaming, Paused, Recovering, Stopping, Stopped, Failed }

/// <summary>Owns deterministic session transitions and runtime lifetime without opening platform resources.</summary>
internal sealed class SessionManager
{
    private readonly EngineEventBus _events;
    private readonly IAudioSessionRuntime _runtime;
    private readonly ILogger<SessionManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly Dictionary<(Guid Sender, Guid Receiver), StreamSessionState> _sessions = [];
    private readonly HashSet<(Guid Sender, Guid Receiver)> _recovering = [];

    public SessionManager(EngineEventBus events, IAudioSessionRuntime? runtime = null, ILogger<SessionManager>? logger = null)
    {
        _events = events;
        _runtime = runtime ?? new UnavailableAudioSessionRuntime();
        _logger = logger ?? NullLogger<SessionManager>.Instance;
    }

    public ImmutableArray<StreamSnapshot> GetSnapshot()
    {
        lock (_sessions)
            return _sessions.OrderBy(pair => pair.Key.Sender).ThenBy(pair => pair.Key.Receiver)
                .Select(pair => new StreamSnapshot(pair.Key.Sender, pair.Key.Receiver, pair.Value.ToString()))
                .ToImmutableArray();
    }

    public async Task ClearAsync(EngineStatus status, CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var active = GetSnapshot();
            foreach (var stream in active.Where(stream => stream.State is not ("Stopped" or "Idle")))
            {
                var result = await _runtime.StopAsync(new StreamRequest(stream.SenderNodeId, stream.ReceiverNodeId), CancellationToken.None).ConfigureAwait(false);
                if (!result.IsSuccess) throw new InvalidOperationException("An audio stream did not stop cleanly.");
                await ApplyStateAsync(new StreamRequest(stream.SenderNodeId, stream.ReceiverNodeId),
                    StreamSessionState.Stopping, status, CancellationToken.None).ConfigureAwait(false);
                await ApplyStateAsync(new StreamRequest(stream.SenderNodeId, stream.ReceiverNodeId),
                    StreamSessionState.Stopped, status, CancellationToken.None).ConfigureAwait(false);
            }
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await _events.PublishAsync("sessions", "SessionsChanged", status,
                    new SessionsChangedPayload(GetSnapshot()), cancellationToken).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        finally { _operations.Release(); }
    }

    public async Task<EngineResult<OperationReceipt>> StartAsync(StreamRequest request, Guid localNodeId,
        IPEndPoint? audioEndpoint, EngineStatus status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsValid(request)) return EngineResult<OperationReceipt>.Failure(EngineErrorCode.InvalidArgument, "session", "Stream endpoints must be distinct, non-empty node identities.");
        if (localNodeId != request.SenderNodeId && localNodeId != request.ReceiverNodeId)
            return EngineResult<OperationReceipt>.Failure(EngineErrorCode.InvalidArgument, "session", "The stream route does not include this node.");
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var recovering = false;
            lock (_sessions)
            {
                var current = _sessions.GetValueOrDefault((request.SenderNodeId, request.ReceiverNodeId), StreamSessionState.Idle);
                if (current is StreamSessionState.Streaming or StreamSessionState.Preparing or StreamSessionState.Negotiating or StreamSessionState.Paused or StreamSessionState.Recovering)
                {
                    if (current == StreamSessionState.Recovering) recovering = true;
                    else return EngineResult<OperationReceipt>.Success(new OperationReceipt(Guid.NewGuid(), current.ToString()));
                }
            }
            if (recovering)
            {
                var recoveryPreparation = await ApplyStateAsync(request, StreamSessionState.Preparing, status, cancellationToken).ConfigureAwait(false);
                if (!recoveryPreparation.IsSuccess) return EngineResult<OperationReceipt>.Failure(recoveryPreparation.Error!.Code, recoveryPreparation.Error.Subsystem, recoveryPreparation.Error.Message, recoveryPreparation.Error.IsRetryable);
            }
            else
            {
                var negotiating = await ApplyStateAsync(request, StreamSessionState.Negotiating, status, cancellationToken).ConfigureAwait(false);
                if (!negotiating.IsSuccess) return EngineResult<OperationReceipt>.Failure(negotiating.Error!.Code, negotiating.Error.Subsystem, negotiating.Error.Message, negotiating.Error.IsRetryable);
                var preparing = await ApplyStateAsync(request, StreamSessionState.Preparing, status, cancellationToken).ConfigureAwait(false);
                if (!preparing.IsSuccess) return EngineResult<OperationReceipt>.Failure(preparing.Error!.Code, preparing.Error.Subsystem, preparing.Error.Message, preparing.Error.IsRetryable);
            }
            EngineResult<bool> started;
            try
            {
                started = await _runtime.StartAsync(request, localNodeId, audioEndpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await ApplyStateAsync(request, StreamSessionState.Failed, status, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception,
                    "Audio session runtime start threw. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} HResult={HResult} StackTrace={StackTrace} InnerException={InnerException} InnerStackTrace={InnerStackTrace}",
                    exception.GetType().FullName, exception.Message, exception.HResult, exception.StackTrace,
                    exception.InnerException?.ToString(), exception.InnerException?.StackTrace);
                started = EngineResult<bool>.Failure(EngineErrorCode.TransportUnavailable, "audio-transport", "Audio transport startup failed.", true);
            }
            if (!started.IsSuccess)
            {
                await ApplyStateAsync(request, StreamSessionState.Failed, status, CancellationToken.None).ConfigureAwait(false);
                return EngineResult<OperationReceipt>.Failure(started.Error!.Code, started.Error.Subsystem, started.Error.Message, started.Error.IsRetryable);
            }
            await ApplyStateAsync(request, StreamSessionState.Streaming, status, CancellationToken.None).ConfigureAwait(false);
            return EngineResult<OperationReceipt>.Success(new OperationReceipt(Guid.NewGuid(), "Streaming"));
        }
        finally { _operations.Release(); }
    }

    public async Task<EngineResult<OperationReceipt>> StopAsync(StreamRequest request, EngineStatus status,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsValid(request)) return EngineResult<OperationReceipt>.Failure(EngineErrorCode.InvalidArgument, "session", "Stream endpoints must be distinct, non-empty node identities.");
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sessions)
            {
                var current = _sessions.GetValueOrDefault((request.SenderNodeId, request.ReceiverNodeId), StreamSessionState.Idle);
                if (current is StreamSessionState.Idle or StreamSessionState.Stopped)
                    return EngineResult<OperationReceipt>.Success(new OperationReceipt(Guid.NewGuid(), "Stopped"));
            }
            var transition = await ApplyStateAsync(request, StreamSessionState.Stopping, status, CancellationToken.None).ConfigureAwait(false);
            if (!transition.IsSuccess && transition.Error!.Code != EngineErrorCode.InvalidState)
                return EngineResult<OperationReceipt>.Failure(transition.Error.Code, transition.Error.Subsystem, transition.Error.Message, transition.Error.IsRetryable);
            EngineResult<bool> stopped;
            try { stopped = await _runtime.StopAsync(request, CancellationToken.None).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                _logger.LogError(exception,
                    "Audio session runtime stop threw. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} HResult={HResult} StackTrace={StackTrace} InnerException={InnerException} InnerStackTrace={InnerStackTrace}",
                    exception.GetType().FullName, exception.Message, exception.HResult, exception.StackTrace,
                    exception.InnerException?.ToString(), exception.InnerException?.StackTrace);
                stopped = EngineResult<bool>.Failure(EngineErrorCode.TransportUnavailable, "audio-transport", "Audio transport shutdown failed.", true);
            }
            if (!stopped.IsSuccess)
            {
                await ApplyStateAsync(request, StreamSessionState.Failed, status, CancellationToken.None).ConfigureAwait(false);
                return EngineResult<OperationReceipt>.Failure(stopped.Error!.Code, stopped.Error.Subsystem, stopped.Error.Message, stopped.Error.IsRetryable);
            }
            await ApplyStateAsync(request, StreamSessionState.Stopped, status, CancellationToken.None).ConfigureAwait(false);
            return EngineResult<OperationReceipt>.Success(new OperationReceipt(Guid.NewGuid(), "Stopped"));
        }
        finally { _operations.Release(); }
    }

    public async Task UpdatePeerEndpointAsync(Guid localNodeId, Guid peerNodeId, IPEndPoint endpoint,
        EngineStatus status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StreamRequest[] routes;
            lock (_sessions) routes = _sessions.Where(pair =>
                (pair.Key.Sender == localNodeId && pair.Key.Receiver == peerNodeId ||
                 pair.Key.Sender == peerNodeId && pair.Key.Receiver == localNodeId) &&
                (pair.Value is StreamSessionState.Streaming or StreamSessionState.Paused))
                .Select(pair => new StreamRequest(pair.Key.Sender, pair.Key.Receiver)).ToArray();
            foreach (var route in routes)
            {
                var updated = await _runtime.UpdateEndpointAsync(route, endpoint, cancellationToken).ConfigureAwait(false);
                if (!updated.IsSuccess)
                {
                    await ApplyStateAsync(route, StreamSessionState.Recovering, status, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }
                await _events.PublishAsync("sessions", "RouteChanged", status,
                    new SessionTransitionPayload(new StreamSnapshot(route.SenderNodeId, route.ReceiverNodeId, StreamSessionState.Streaming.ToString()),
                        StreamSessionState.Streaming.ToString(), StreamSessionState.Streaming.ToString()), cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _operations.Release(); }
    }

    public async Task MarkPeerRecoveringAsync(Guid localNodeId, Guid peerNodeId, EngineStatus status,
        CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StreamRequest[] routes;
            lock (_sessions) routes = _sessions.Where(pair =>
                (pair.Key.Sender == peerNodeId && pair.Key.Receiver == localNodeId ||
                 pair.Key.Sender == localNodeId && pair.Key.Receiver == peerNodeId) &&
                (pair.Value is StreamSessionState.Streaming or StreamSessionState.Paused))
                .Select(pair => new StreamRequest(pair.Key.Sender, pair.Key.Receiver)).ToArray();
            foreach (var route in routes)
            {
                var stopped = await _runtime.StopAsync(route, CancellationToken.None).ConfigureAwait(false);
                await ApplyStateAsync(route, stopped.IsSuccess ? StreamSessionState.Recovering : StreamSessionState.Failed,
                    status, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally { _operations.Release(); }
    }

    public Task<EngineResult<OperationReceipt>> PauseAsync(Guid localNodeId, LaneSelection lanes,
        EngineStatus status, CancellationToken cancellationToken) => ChangePausedStateAsync(localNodeId, lanes, status, pause: true, cancellationToken: cancellationToken);

    public Task<EngineResult<OperationReceipt>> ResumeAsync(Guid localNodeId, LaneSelection lanes,
        EngineStatus status, CancellationToken cancellationToken) => ChangePausedStateAsync(localNodeId, lanes, status, pause: false, cancellationToken: cancellationToken);

    private async Task<EngineResult<OperationReceipt>> ChangePausedStateAsync(Guid localNodeId, LaneSelection lanes,
        EngineStatus status, bool pause, CancellationToken cancellationToken)
    {
        if (!lanes.Sender && !lanes.Receiver)
            return EngineResult<OperationReceipt>.Failure(EngineErrorCode.InvalidArgument, "session", "At least one local lane must be selected.");
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (StreamRequest Request, bool IsSender)[] routes;
            lock (_sessions) routes = _sessions.Where(pair => pair.Value == (pause ? StreamSessionState.Streaming : StreamSessionState.Paused) &&
                ((pair.Key.Sender == localNodeId && lanes.Sender) || (pair.Key.Receiver == localNodeId && lanes.Receiver)))
                .Select(pair => (new StreamRequest(pair.Key.Sender, pair.Key.Receiver), pair.Key.Sender == localNodeId)).ToArray();
            foreach (var route in routes)
            {
                var changed = pause
                    ? await _runtime.PauseAsync(route.Request, route.IsSender, cancellationToken).ConfigureAwait(false)
                    : await _runtime.ResumeAsync(route.Request, route.IsSender, cancellationToken).ConfigureAwait(false);
                if (!changed.IsSuccess)
                    return EngineResult<OperationReceipt>.Failure(changed.Error!.Code, changed.Error.Subsystem, changed.Error.Message, changed.Error.IsRetryable);
                var transition = await ApplyStateAsync(route.Request, pause ? StreamSessionState.Paused : StreamSessionState.Streaming,
                    status, CancellationToken.None).ConfigureAwait(false);
                if (!transition.IsSuccess)
                    return EngineResult<OperationReceipt>.Failure(transition.Error!.Code, transition.Error.Subsystem, transition.Error.Message, transition.Error.IsRetryable);
            }
            return EngineResult<OperationReceipt>.Success(new OperationReceipt(Guid.NewGuid(), pause ? "Paused" : "Streaming"));
        }
        finally { _operations.Release(); }
    }

    internal async Task<EngineResult<StreamSnapshot>> ApplyStateAsync(StreamRequest request, StreamSessionState state,
        EngineStatus status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsValid(request))
            return EngineResult<StreamSnapshot>.Failure(EngineErrorCode.InvalidArgument, "session", "Stream endpoints must be distinct, non-empty node identities.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = (request.SenderNodeId, request.ReceiverNodeId);
            StreamSessionState current;
            bool exists;
            lock (_sessions)
            {
                current = _sessions.GetValueOrDefault(key, StreamSessionState.Idle);
                exists = _sessions.ContainsKey(key);
            }
            if (current == state) return EngineResult<StreamSnapshot>.Success(new StreamSnapshot(key.SenderNodeId, key.ReceiverNodeId, state.ToString()));
            if (!CanTransition(current, state))
                return EngineResult<StreamSnapshot>.Failure(EngineErrorCode.InvalidState, "session", "Stream session transition is not valid.");
            if (!exists && state != StreamSessionState.Negotiating)
                return EngineResult<StreamSnapshot>.Failure(EngineErrorCode.InvalidState, "session", "A session must be created before it can transition.");
            bool wasRecovering;
            lock (_sessions)
            {
                _sessions[key] = state;
                if (state == StreamSessionState.Recovering) _recovering.Add(key);
                if (state is StreamSessionState.Failed or StreamSessionState.Stopped) _recovering.Remove(key);
                wasRecovering = state == StreamSessionState.Streaming && _recovering.Remove(key);
            }
            var snapshot = new StreamSnapshot(key.SenderNodeId, key.ReceiverNodeId, state.ToString());
            var eventName = (current, state) switch
            {
                (StreamSessionState.Idle or StreamSessionState.Stopped or StreamSessionState.Failed, StreamSessionState.Negotiating) => "SessionCreated",
                (_, StreamSessionState.Preparing) => "SessionPrepared",
                (_, StreamSessionState.Streaming) when wasRecovering || current == StreamSessionState.Recovering => "SessionRecovered",
                (_, StreamSessionState.Streaming) => "SessionStarted",
                (_, StreamSessionState.Stopped) => "SessionStopped",
                (_, StreamSessionState.Failed) => "SessionFailed",
                _ => "SessionStateChanged"
            };
            await _events.PublishAsync("sessions", eventName, status,
                new SessionTransitionPayload(snapshot, current.ToString(), state.ToString()), cancellationToken).ConfigureAwait(false);
            await _events.PublishAsync("sessions", "SessionsChanged", status,
                new SessionsChangedPayload(GetSnapshot()), cancellationToken).ConfigureAwait(false);
            return EngineResult<StreamSnapshot>.Success(snapshot);
        }
        finally { _gate.Release(); }
    }

    private static bool CanTransition(StreamSessionState current, StreamSessionState next) => (current, next) switch
    {
        (StreamSessionState.Idle or StreamSessionState.Stopped or StreamSessionState.Failed, StreamSessionState.Negotiating) => true,
        (StreamSessionState.Negotiating, StreamSessionState.Preparing or StreamSessionState.Failed or StreamSessionState.Stopping) => true,
        (StreamSessionState.Preparing, StreamSessionState.Streaming or StreamSessionState.Failed or StreamSessionState.Stopping) => true,
        (StreamSessionState.Streaming, StreamSessionState.Paused or StreamSessionState.Recovering or StreamSessionState.Stopping or StreamSessionState.Failed) => true,
        (StreamSessionState.Paused, StreamSessionState.Streaming or StreamSessionState.Recovering or StreamSessionState.Stopping or StreamSessionState.Failed) => true,
        (StreamSessionState.Recovering, StreamSessionState.Preparing or StreamSessionState.Streaming or StreamSessionState.Failed or StreamSessionState.Stopping) => true,
        (StreamSessionState.Stopping, StreamSessionState.Stopped or StreamSessionState.Failed) => true,
        (StreamSessionState.Failed, StreamSessionState.Stopping or StreamSessionState.Stopped) => true,
        _ => current == next
    };

    private static bool IsValid(StreamRequest request) => request.SenderNodeId != Guid.Empty &&
        request.ReceiverNodeId != Guid.Empty && request.SenderNodeId != request.ReceiverNodeId;

}

internal sealed record SessionTransitionPayload(StreamSnapshot Session, string PreviousState, string CurrentState) : EngineEventPayload;

internal interface IAudioSessionRuntime
{
    Task<EngineResult<bool>> StartAsync(StreamRequest request, Guid localNodeId, IPEndPoint? audioEndpoint, CancellationToken cancellationToken);
    Task<EngineResult<bool>> StopAsync(StreamRequest request, CancellationToken cancellationToken);
    Task<EngineResult<bool>> UpdateEndpointAsync(StreamRequest request, IPEndPoint audioEndpoint, CancellationToken cancellationToken);
    Task<EngineResult<bool>> PauseAsync(StreamRequest request, bool isSender, CancellationToken cancellationToken);
    Task<EngineResult<bool>> ResumeAsync(StreamRequest request, bool isSender, CancellationToken cancellationToken);
}

internal sealed class UnavailableAudioSessionRuntime : IAudioSessionRuntime
{
    public Task<EngineResult<bool>> StartAsync(StreamRequest request, Guid localNodeId, IPEndPoint? audioEndpoint, CancellationToken cancellationToken) =>
        Task.FromResult(EngineResult<bool>.Failure(EngineErrorCode.CapabilityUnavailable, "session", "No audio transport runtime is registered."));
    public Task<EngineResult<bool>> StopAsync(StreamRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(EngineResult<bool>.Success(true));
    public Task<EngineResult<bool>> UpdateEndpointAsync(StreamRequest request, IPEndPoint audioEndpoint, CancellationToken cancellationToken) =>
        Task.FromResult(EngineResult<bool>.Failure(EngineErrorCode.CapabilityUnavailable, "session", "No audio transport runtime is registered."));
    public Task<EngineResult<bool>> PauseAsync(StreamRequest request, bool isSender, CancellationToken cancellationToken) =>
        Task.FromResult(EngineResult<bool>.Failure(EngineErrorCode.CapabilityUnavailable, "session", "No audio transport runtime is registered."));
    public Task<EngineResult<bool>> ResumeAsync(StreamRequest request, bool isSender, CancellationToken cancellationToken) =>
        Task.FromResult(EngineResult<bool>.Failure(EngineErrorCode.CapabilityUnavailable, "session", "No audio transport runtime is registered."));
}
