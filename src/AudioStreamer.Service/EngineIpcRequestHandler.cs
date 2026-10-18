using System.Collections.Immutable;
using System.Reflection;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Engine.Runtime;
using AudioStreamer.Engine.Managers;
using AudioStreamer.IPC.Contracts;
using AudioStreamer.IPC.Dispatch;
using AudioStreamer.IPC.Protocol;
using AudioStreamer.IPC.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioStreamer.Service;

/// <summary>Service boundary adapter. IPC owns framing; this adapter maps allow-listed DTOs to the public IEngine API.</summary>
public sealed class EngineIpcRequestHandler(IEngine engine, AudioStreamer.IPC.Transport.EventPublisher publisher,
    AudioStreamer.IPC.Transport.IpcServerState serverState, ILogger<EngineIpcRequestHandler>? logger = null,
    IAudioAgentSupervisor? agentSupervisor = null)
    : IIpcRequestHandler, IIpcHandshakeInfoProvider
{
    private readonly ILogger<EngineIpcRequestHandler> _logger = logger ?? NullLogger<EngineIpcRequestHandler>.Instance;
    private static readonly IReadOnlyList<string> AdvertisedCapabilities = Array.AsReadOnly(
        new[] { "Ping", "GetSnapshot", "GetStatus", "GetAgentStatus", "GetSettings", "GetStreams", "GetPeers", "GetPeer", "GetControlStatistics", "GetStatistics", "StartStream", "StopStream", "Pause", "Resume", "UpdateSettings", "GetVersion", "GetDiagnostics", "Subscribe", "Unsubscribe" });

    public async ValueTask<IpcServerInfo> GetServerInfoAsync(CancellationToken cancellationToken)
    {
        var status = await engine.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var nodeState = status.IsSuccess ? status.Value!.State.ToString() : "Unavailable";
        var versions = engine.GetVersionInfo();
        var serviceVersion = GetInformationalVersion(typeof(EngineIpcRequestHandler).Assembly);
        return new IpcServerInfo(
            serviceVersion,
            versions.ProductVersion,
            serviceVersion,
            AdvertisedCapabilities,
            serverState.IsStopping ? "Stopping" : "Running",
            nodeState);
    }

    public async ValueTask<IpcHandlerResponse> HandleAsync(string messageType, System.Text.Json.JsonElement payload,
        IpcClientIdentity identity, CancellationToken cancellationToken)
    {
        switch (messageType)
        {
            case "GetAgentStatus":
                {
                    _ = IpcSerializer.DeserializePayload<EmptyPayload>(payload);
                    var status = agentSupervisor?.Status ?? new AudioAgentStatus(AgentState.Disconnected, false, null, 0, "AgentUnavailable");
                    return Success("GetAgentStatusResult", status);
                }
            case "GetStatus":
                {
                    _ = IpcSerializer.DeserializePayload<EmptyPayload>(payload);
                    var result = await engine.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                    return result.IsSuccess
                        ? Success("GetStatusResult", Map(result.Value!))
                        : Failure("GetStatusResult", MapError(result.Error!));
                }
            case "GetSnapshot":
                {
                    _ = IpcSerializer.DeserializePayload<EmptyPayload>(payload);
                    var result = await engine.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    if (!result.IsSuccess) return Failure("GetSnapshotResult", MapError(result.Error!));
                    var snapshot = result.Value!;
                    return Success("GetSnapshotResult", new AudioStreamer.IPC.Contracts.NodeSnapshot(snapshot.NodeId, snapshot.NodeName,
                        Map(snapshot.Status), snapshot.SettingsRevision, publisher.CurrentSequence));
                }
            case "GetSettings":
                {
                    _ = IpcSerializer.DeserializePayload<EmptyPayload>(payload);
                    var result = await engine.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
                    if (!result.IsSuccess) return Failure("GetSettingsResult", MapError(result.Error!));
                    var settings = result.Value!;
                    return Success("GetSettingsResult", new AudioStreamer.IPC.Contracts.SettingsSnapshot(settings.SchemaVersion,
                        settings.Revision, settings.NodeId, settings.NodeName));
                }
            case "UpdateSettings":
                {
                    if (!identity.IsAdministrator)
                        return Failure("UpdateSettingsResult", new IpcError
                        {
                            Code = IpcErrors.Unauthorized,
                            Message = "Administrator authorization is required to change machine settings."
                        });
                    var request = IpcSerializer.DeserializePayload<UpdateSettingsRequest>(payload);
                    if (request.ExpectedRevision < 1 || request.NodeName is { Length: > 80 })
                        return Failure("UpdateSettingsResult", new IpcError { Code = IpcErrors.InvalidField, Message = "Settings patch is outside the supported bounds." });
                    var result = await engine.UpdateSettingsAsync(new AudioStreamer.Engine.Contracts.SettingsPatch(
                        request.ExpectedRevision, request.NodeName), cancellationToken).ConfigureAwait(false);
                    if (!result.IsSuccess) return Failure("UpdateSettingsResult", MapError(result.Error!));
                    var settings = result.Value!;
                    return Success("UpdateSettingsResult", new AudioStreamer.IPC.Contracts.SettingsSnapshot(settings.SchemaVersion,
                        settings.Revision, settings.NodeId, settings.NodeName));
                }
            case "GetVersion":
                {
                    _ = IpcSerializer.DeserializePayload<EmptyPayload>(payload);
                    var versions = engine.GetVersionInfo();
                    return Success("GetVersionResult", new VersionSnapshot(
                        GetInformationalVersion(typeof(EngineIpcRequestHandler).Assembly),
                        versions.ProductVersion,
                        $"{IpcProtocol.MajorVersion}.{IpcProtocol.MinorVersion}"));
                }
            case "GetDiagnostics":
                {
                    _ = IpcSerializer.DeserializePayload<EmptyPayload>(payload);
                    return await GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
                }
            case "GetStreams":
                {
                    _ = IpcSerializer.DeserializePayload<EmptyPayload>(payload);
                    var result = await engine.GetStreamsAsync(cancellationToken).ConfigureAwait(false);
                    if (!result.IsSuccess) return Failure("GetStreamsResult", MapError(result.Error!));
                    var streams = result.Value!.Select(stream => new IpcStreamSnapshot(stream.SenderNodeId,
                        stream.ReceiverNodeId, stream.State)).ToImmutableArray();
                    return Success("GetStreamsResult", streams);
                }
            case "GetStatistics":
                {
                    _ = IpcSerializer.DeserializePayload<EmptyPayload>(payload);
                    var result = await engine.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
                    if (!result.IsSuccess) return Failure("GetStatisticsResult", MapError(result.Error!));
                    return Success("GetStatisticsResult", new IpcStatisticsSnapshot(result.Value!.SampledAt, result.Value.Counters));
                }
            case "GetPeers":
                {
                    _ = IpcSerializer.DeserializePayload<EmptyPayload>(payload);
                    var peerResult = await engine.GetControlPeersAsync(cancellationToken).ConfigureAwait(false);
                    if (!peerResult.IsSuccess) return Failure("GetPeersResult", MapError(peerResult.Error!));
                    var peers = peerResult.Value!.Select(MapPeer).ToImmutableArray();
                    return Success("GetPeersResult", peers);
                }
            case "GetPeer":
                {
                    var request = IpcSerializer.DeserializePayload<PeerNodeRequest>(payload);
                    if (request.NodeId == Guid.Empty)
                        return Failure("GetPeerResult", new IpcError { Code = IpcErrors.InvalidField, Message = "Peer ID is required." });
                    var protocolId = ControlNodeIdentity.ToProtocolId(request.NodeId);
                    var peerResult = await engine.GetControlPeerAsync(protocolId, cancellationToken).ConfigureAwait(false);
                    if (!peerResult.IsSuccess) return Failure("GetPeerResult", MapError(peerResult.Error!));
                    var peer = peerResult.Value;
                    return peer is not null
                        ? Success("GetPeerResult", MapPeer(peer))
                        : Failure("GetPeerResult", new IpcError { Code = "PeerNotFound", Message = "Peer is not currently discovered.", Retryable = true });
                }
            case "GetControlStatistics":
                {
                    _ = IpcSerializer.DeserializePayload<EmptyPayload>(payload);
                    var statsResult = await engine.GetControlStatisticsAsync(cancellationToken).ConfigureAwait(false);
                    if (!statsResult.IsSuccess) return Failure("GetControlStatisticsResult", MapError(statsResult.Error!));
                    var stats = statsResult.Value!;
                    return Success("GetControlStatisticsResult", new AudioStreamer.IPC.Contracts.ControlStatisticsSnapshot(
                        stats.PacketsSent, stats.BytesSent, stats.PacketsReceived, stats.BytesReceived,
                        stats.HelloPackets, stats.Heartbeats, stats.UnknownPackets, stats.MalformedPackets,
                        stats.PeerTimeouts, stats.Reconnects, stats.RegistrySize, stats.CurrentSessions,
                        stats.DiscoveryDuration, stats.AverageRoundTripMilliseconds));
                }
            case "StartStream":
            case "StopStream":
                {
                    var request = IpcSerializer.DeserializePayload<StreamRouteRequest>(payload);
                    if (request.SenderNodeId == Guid.Empty || request.ReceiverNodeId == Guid.Empty || request.SenderNodeId == request.ReceiverNodeId)
                        return Failure(messageType + "Result", new IpcError { Code = IpcErrors.InvalidField, Message = "Stream route must contain distinct nonzero node IDs." });
                    var engineRequest = new StreamRequest(request.SenderNodeId, request.ReceiverNodeId);
                    EngineResult<OperationReceipt> result;
                    if (messageType == "StartStream")
                    {
                        try
                        {
                            result = await engine.StartStreamingAsync(engineRequest, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            _logger.LogError(exception,
                                "StartStream failed unexpectedly. SenderNodeId={SenderNodeId} ReceiverNodeId={ReceiverNodeId} ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} ExceptionStackTrace={ExceptionStackTrace} InnerException={InnerException}",
                                request.SenderNodeId, request.ReceiverNodeId, exception.GetType().FullName,
                                exception.Message, exception.StackTrace, exception.InnerException?.ToString());
                            return Failure("StartStreamResult", new IpcError
                            {
                                Code = IpcErrors.StreamStartFailed,
                                Message = "The stream could not be started. Check the selected audio devices and network configuration.",
                                Retryable = true
                            });
                        }
                        if (!result.IsSuccess)
                            return Failure("StartStreamResult", MapStreamStartError(result.Error!));
                    }
                    else
                    {
                        result = await engine.StopStreamingAsync(engineRequest, cancellationToken).ConfigureAwait(false);
                        if (!result.IsSuccess) return Failure("StopStreamResult", MapError(result.Error!));
                    }
                    return Success(messageType + "Result", new IpcStreamOperation(result.Value!.OperationId, result.Value.State));
                }
            case "Pause":
            case "Resume":
                {
                    var requested = IpcSerializer.DeserializePayload<IpcLaneSelection>(payload);
                    var lanes = new LaneSelection(requested.Sender, requested.Receiver);
                    var result = messageType == "Pause"
                        ? await engine.PauseAsync(lanes, cancellationToken).ConfigureAwait(false)
                        : await engine.ResumeAsync(lanes, cancellationToken).ConfigureAwait(false);
                    if (!result.IsSuccess) return Failure(messageType + "Result", MapError(result.Error!));
                    return Success(messageType + "Result", new IpcStreamOperation(result.Value!.OperationId, result.Value.State));
                }
            case "StartNode":
            case "StopNode":
            case "GetAudioDevices":
            case "SelectAudioDevices":
            case "GetLogsPage":
            case "GetVersions":
            case "BeginUpdateCheck":
                return Failure(messageType + "Result", new IpcError
                {
                    Code = IpcErrors.CapabilityUnavailable,
                    Message = $"{messageType} is not available in the current IPC implementation phase."
                });
            case "RestartDiscovery":
                _ = IpcSerializer.DeserializePayload<EmptyPayload>(payload);
                var discovery = await engine.RestartDiscoveryAsync(cancellationToken).ConfigureAwait(false);
                if (!discovery.IsSuccess) return Failure("RestartDiscoveryResult", MapError(discovery.Error!));
                return Success("RestartDiscoveryResult", new { State = "Discovering" });
            default:
                return Failure(messageType + "Result", new IpcError
                {
                    Code = IpcErrors.InvalidRequest,
                    Message = "Request message type is not allow-listed."
                });
        }
    }

    private async ValueTask<IpcHandlerResponse> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        Engine.Contracts.DiagnosticsSnapshot? diagnostics;
        try
        {
            var result = await engine.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess || result.Value is null)
            {
                _logger.LogDebug("Engine diagnostics are unavailable. ErrorCode={ErrorCode}", result.Error?.Code);
                diagnostics = null;
            }
            else
            {
                diagnostics = result.Value;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            _logger.LogDebug(exception, "Engine diagnostics sampling was canceled; returning an unavailable snapshot.");
            diagnostics = null;
        }
        catch (ObjectDisposedException exception)
        {
            _logger.LogDebug(exception, "Engine diagnostics are unavailable because a runtime component is disposed.");
            diagnostics = null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Engine diagnostics failed; returning an unavailable snapshot. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} InnerException={InnerException}",
                exception.GetType().FullName, exception.Message, exception.InnerException?.ToString());
            diagnostics = null;
        }

        var response = diagnostics is null
            ? CreateUnavailableDiagnostics()
            : new AudioStreamer.IPC.Contracts.DiagnosticsSnapshot(
                diagnostics.Status?.State.ToString() ?? "Unavailable",
                diagnostics.Capabilities.IsDefault ? ImmutableArray<string>.Empty : diagnostics.Capabilities,
                string.IsNullOrWhiteSpace(diagnostics.Health) ? "Unavailable" : diagnostics.Health,
                diagnostics.Uptime, diagnostics.CpuPercent, diagnostics.WorkingSetBytes,
                diagnostics.Statistics, diagnostics.SafeErrorCode);
        return Success("GetDiagnosticsResult", response);
    }

    private static AudioStreamer.IPC.Contracts.DiagnosticsSnapshot CreateUnavailableDiagnostics() => new(
        "Unavailable", ImmutableArray<string>.Empty, "Unavailable", CpuPercent: null,
        WorkingSetBytes: null, Statistics: null, SafeErrorCode: "DiagnosticsUnavailable");

    public async Task PublishEngineEventsAsync(AudioStreamer.IPC.Transport.EventPublisher publisher, CancellationToken cancellationToken)
    {
        if (engine is not IEngineDomainEventSource eventSource)
            throw new InvalidOperationException("Engine domain event source is unavailable.");
        await foreach (var engineEvent in eventSource.SubscribeDomainEventsAsync(new EventSubscription(128), cancellationToken).ConfigureAwait(false))
        {
            var payload = new IpcEventPayload(engineEvent.Status.State.ToString(), engineEvent.Status.Revision, engineEvent.Status.SafeErrorCode);
            if (engineEvent.Kind == "LifecycleChanged")
            {
                await publisher.PublishAsync("NodeStateChanged", payload, cancellationToken).ConfigureAwait(false);
                await publisher.PublishAsync("DiagnosticsChanged", payload, cancellationToken).ConfigureAwait(false);
            }
            else if (engineEvent.Kind == "ConfigurationChanged" && engineEvent.Payload is SettingsChangedPayload changed)
            {
                await publisher.PublishAsync("SettingsChanged", payload with { SettingsRevision = changed.Settings.Revision }, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (engineEvent.Payload is StatisticsChangedPayload statistics)
                await publisher.PublishAsync("StatisticsChanged", statistics.Statistics, cancellationToken).ConfigureAwait(false);
            else if (engineEvent.Payload is SessionsChangedPayload sessions)
                await publisher.PublishAsync("SessionsChanged", sessions.Sessions.Select(stream =>
                    new IpcStreamSnapshot(stream.SenderNodeId, stream.ReceiverNodeId, stream.State)).ToImmutableArray(), cancellationToken).ConfigureAwait(false);
            else if (engineEvent.Payload is SessionTransitionPayload transition)
            {
                var eventName = engineEvent.Kind == "SessionStateChanged" ? "SessionChanged" : engineEvent.Kind;
                await publisher.PublishAsync(eventName, new IpcSessionEvent(transition.Session.SenderNodeId,
                    transition.Session.ReceiverNodeId, transition.PreviousState, transition.CurrentState), cancellationToken).ConfigureAwait(false);
            }
            else if (engineEvent.Payload is AudioPlatformChangedPayload audio)
            {
                var eventName = audio.AudioEvent.Kind switch
                {
                    AudioPlatformEventKind.BufferUnderrun or AudioPlatformEventKind.JitterUnderrun => "AudioUnderrun",
                    AudioPlatformEventKind.BufferOverrun => "AudioOverrun",
                    AudioPlatformEventKind.PacketMissing => "PacketLossDetected",
                    AudioPlatformEventKind.PacketRecovered => "PacketRecovered",
                    _ => audio.AudioEvent.Kind.ToString()
                };
                await publisher.PublishAsync(eventName, new IpcAudioRuntimeEvent(audio.AudioEvent.Kind.ToString(),
                    audio.AudioEvent.OccurredAt, audio.AudioEvent.DeviceId, audio.AudioEvent.Count,
                    audio.AudioEvent.SafeErrorCode), cancellationToken).ConfigureAwait(false);
            }
            else if (engineEvent.Payload is ControlPlaneEventPayload control)
            {
                switch (control.Data)
                {
                    case ControlPeerEventData peer:
                        await publisher.PublishAsync(control.EventName, MapControlPeerEvent(peer.Peer), cancellationToken).ConfigureAwait(false);
                        break;
                    case ControlNetworkLostEventData lost:
                        await publisher.PublishAsync(control.EventName, new { lost.Code }, cancellationToken).ConfigureAwait(false);
                        break;
                    case ControlEmptyEventData:
                        await publisher.PublishAsync(control.EventName, new { }, cancellationToken).ConfigureAwait(false);
                        break;
                    case ControlSessionEventData session:
                        await publisher.PublishAsync(control.EventName, new
                        { session.SenderNodeId, session.ReceiverNodeId, session.TransactionId }, cancellationToken).ConfigureAwait(false);
                        break;
                    case ControlRemoteSessionEventData remoteSession:
                        await publisher.PublishAsync(control.EventName, new
                        { remoteSession.SenderNodeId, remoteSession.ReceiverNodeId, remoteSession.State, remoteSession.TransactionId }, cancellationToken).ConfigureAwait(false);
                        break;
                    case ControlStreamStatusEventData streamStatus:
                        await publisher.PublishAsync(control.EventName, streamStatus.Status, cancellationToken).ConfigureAwait(false);
                        break;
                    case ControlErrorEventData controlError:
                        await publisher.PublishAsync(control.EventName, controlError.Error, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
            else if (engineEvent.Kind == "StatisticsChanged")
                await publisher.PublishAsync("StatisticsChanged", payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private static StatusSnapshot Map(EngineStatus status) => new(status.State.ToString(), status.Revision, status.SafeErrorCode);

    private static string GetInformationalVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidOperationException($"Product informational version was not generated for assembly '{assembly.GetName().Name}'.");

    private static IpcPeerSnapshot MapPeer(ControlPeerSnapshot peer) => new(peer.EngineNodeId,
        peer.ProtocolNodeId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture), peer.Name, peer.Platform,
        peer.Capabilities, peer.Address, peer.AudioPort, peer.ControlPort,
        peer.StatusName, peer.LastSeenUtc, peer.ProtocolVersion);

    private static ControlPeerEventDto MapControlPeerEvent(ControlPeerSnapshot peer) => new(peer.ProtocolNodeId,
        peer.EngineNodeId, peer.Name, peer.Platform, peer.Capabilities, peer.Address, peer.AudioPort, peer.ControlPort,
        peer.Status, peer.LastSeenUtc, peer.LastSeenTimestamp,
        peer.ProtocolVersion);

    private static IpcError MapError(EngineError error) => new()
    {
        Code = error.Code switch
        {
            EngineErrorCode.CapabilityUnavailable => IpcErrors.CapabilityUnavailable,
            EngineErrorCode.ConfigurationConflict => IpcErrors.ConfigurationConflict,
            EngineErrorCode.InvalidArgument => IpcErrors.InvalidField,
            EngineErrorCode.InvalidState => IpcErrors.NodeNotRunning,
            _ => IpcErrors.InternalError
        },
        Message = error.Message,
        Retryable = error.IsRetryable
    };

    private static IpcError MapStreamStartError(EngineError error) => new()
    {
        Code = error.Code switch
        {
            EngineErrorCode.InvalidArgument => IpcErrors.InvalidField,
            EngineErrorCode.InvalidState => IpcErrors.NodeNotRunning,
            EngineErrorCode.NotFound => IpcErrors.PeerNotFound,
            EngineErrorCode.CapabilityUnavailable => IpcErrors.CapabilityUnavailable,
            EngineErrorCode.TransportUnavailable => IpcErrors.TransportUnavailable,
            _ => IpcErrors.StreamStartFailed
        },
        Message = error.Message,
        Retryable = error.IsRetryable
    };

    private static IpcHandlerResponse Success<T>(string messageType, T data) => new(messageType,
        new IpcOperationResult<T> { Result = "ok", Data = data });

    private static IpcHandlerResponse Failure(string messageType, IpcError error) => new(messageType,
        new IpcOperationResult<object> { Result = "error", Error = error }, error.Code);

    private sealed record StreamRouteRequest(Guid SenderNodeId, Guid ReceiverNodeId);
    private sealed record ControlPeerEventDto(ulong ProtocolNodeId, Guid EngineNodeId, string Name, string Platform,
        ushort Capabilities, string Address, ushort AudioPort, int ControlPort,
        byte Status, DateTimeOffset LastSeenUtc, long LastSeenTimestamp,
        string ProtocolVersion);
    private sealed record IpcStatisticsSnapshot(DateTimeOffset SampledAt, ImmutableDictionary<string, long> Counters);
    private sealed record IpcStreamOperation(Guid OperationId, string State);
}
