using System.Collections.Immutable;
using AudioStreamer.Engine.Configuration;
using AudioStreamer.Engine.Contracts;

namespace AudioStreamer.Engine.Runtime;

internal abstract record EngineEventPayload;

internal sealed record NodeStateChangedPayload(Guid NodeId, string NodeName, EngineStatus Status) : EngineEventPayload;
internal sealed record SettingsChangedPayload(EngineSettings Settings) : EngineEventPayload;
internal sealed record PeersChangedPayload(ImmutableArray<PeerSnapshot> Peers) : EngineEventPayload;
internal sealed record SessionsChangedPayload(ImmutableArray<StreamSnapshot> Sessions) : EngineEventPayload;
internal sealed record DevicesChangedPayload(ImmutableArray<AudioDeviceSnapshot> Devices) : EngineEventPayload;
internal sealed record StatisticsChangedPayload(StatisticsSnapshot Statistics) : EngineEventPayload;
internal sealed record DiagnosticsChangedPayload(EngineDiagnosticsSnapshot Diagnostics) : EngineEventPayload;
internal sealed record AudioPlatformChangedPayload(AudioPlatformEvent AudioEvent) : EngineEventPayload;
internal abstract record ControlEventData;
internal sealed record ControlPeerEventData(ControlPeerSnapshot Peer) : ControlEventData;
internal sealed record ControlNetworkLostEventData(string Code) : ControlEventData;
internal sealed record ControlEmptyEventData : ControlEventData;
internal sealed record ControlSessionEventData(Guid SenderNodeId, Guid ReceiverNodeId, ushort? TransactionId) : ControlEventData;
internal sealed record ControlRemoteSessionEventData(ulong SenderNodeId, ulong ReceiverNodeId, string State,
    ushort? TransactionId) : ControlEventData;
internal sealed record ControlStreamStatusEventData(AudioStreamer.Control.Protocol.StreamStatusMessage Status) : ControlEventData;
internal sealed record ControlErrorEventData(AudioStreamer.Control.Protocol.ErrorMessage Error) : ControlEventData;
internal sealed record ControlPlaneEventPayload(string EventName, ControlEventData Data) : EngineEventPayload;

internal sealed record EngineDomainEvent(Guid EngineInstanceId, long Sequence, long StateRevision,
    DateTimeOffset OccurredAt, string Source, string Kind, EngineStatus Status, EngineEventPayload Payload);

/// <summary>Immutable, engine-owned runtime projection. A reference to one version is safe to share.</summary>
internal sealed record EngineRuntimeSnapshot(
    Guid EngineInstanceId,
    long EventWatermark,
    EngineStatus Status,
    Guid NodeId,
    string NodeName,
    SettingsSnapshot? Settings,
    ImmutableArray<PeerSnapshot> Peers,
    ImmutableArray<StreamSnapshot> Sessions,
    ImmutableArray<AudioDeviceSnapshot> Devices,
    StatisticsSnapshot Statistics,
    EngineDiagnosticsSnapshot Diagnostics)
{
    public static EngineRuntimeSnapshot Create(Guid engineInstanceId) => new(
        engineInstanceId, 0, new EngineStatus(EngineLifecycleState.Stopped, 0), Guid.Empty, string.Empty, null,
        ImmutableArray<PeerSnapshot>.Empty, ImmutableArray<StreamSnapshot>.Empty, ImmutableArray<AudioDeviceSnapshot>.Empty,
        new StatisticsSnapshot(DateTimeOffset.UtcNow, ImmutableDictionary<string, long>.Empty),
        EngineDiagnosticsSnapshot.Empty);
}

internal sealed record EngineDiagnosticsSnapshot(
    string Health,
    TimeSpan Uptime,
    double? CpuPercent,
    long? WorkingSetBytes,
    EngineVersionInfo Version,
    ImmutableArray<string> Capabilities,
    string? SafeErrorCode)
{
    public static EngineDiagnosticsSnapshot Empty { get; } = new(
        "Stopped", TimeSpan.Zero, null, null, EngineVersionProvider.Current,
        ImmutableArray<string>.Empty, null);
}
