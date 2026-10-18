using System.Collections.Immutable;
using AudioStreamer.Engine.Contracts;

namespace AudioStreamer.Engine.Runtime;

/// <summary>Immutable Control v1 peer metadata owned by the Engine Control Plane.</summary>
internal sealed record EngineControlPeer(ulong ProtocolNodeId, Guid EngineNodeId, string Name, string Platform,
    ushort Capabilities, ControlTransportEndpoint AudioEndpoint, ControlTransportEndpoint ControlEndpoint, byte Status,
    DateTimeOffset LastSeenUtc, long LastSeenTimestamp, string ProtocolVersion = "Control/1");

/// <summary>Internal port between the Engine Control Coordinator and Engine-owned managers.</summary>
internal interface IEngineControlPlaneSink
{
    Task<EngineResult<NodeSnapshot>> GetNodeSnapshotAsync(CancellationToken cancellationToken);
    Task<EngineResult<SettingsSnapshot>> GetNodeSettingsAsync(CancellationToken cancellationToken);
    Task<EngineResult<EngineStatus>> GetNodeStatusAsync(CancellationToken cancellationToken);
    Task<EngineResult<OperationReceipt>> StartLocalStreamAsync(StreamRequest request, CancellationToken cancellationToken);
    Task<EngineResult<OperationReceipt>> StopLocalStreamAsync(StreamRequest request, CancellationToken cancellationToken);
    Task ApplyControlPeerAsync(EngineControlPeer peer, CancellationToken cancellationToken);
    Task RemoveControlPeerAsync(ulong protocolNodeId, CancellationToken cancellationToken);
    Task<EngineControlPeer?> FindControlPeerAsync(ulong protocolNodeId, CancellationToken cancellationToken);
    Task<ImmutableArray<EngineControlPeer>> GetControlPeersAsync(CancellationToken cancellationToken);
    Task<ImmutableArray<EngineControlPeer>> ExpireControlPeersAsync(CancellationToken cancellationToken);
}

internal interface IEngineDomainEventSource
{
    IAsyncEnumerable<EngineDomainEvent> SubscribeDomainEventsAsync(EventSubscription subscription, CancellationToken cancellationToken);
}
