using System.Collections.Immutable;

namespace AudioStreamer.Engine.Contracts;

public enum EngineLifecycleState { Stopped, Starting, Running, Degraded, Stopping, Restarting, Faulted }
public enum EngineErrorCode { InvalidState, InvalidArgument, NotFound, CapabilityUnavailable, ConfigurationConflict, PersistenceFailure, TransportUnavailable, ResourceBusy, ShutdownTimedOut, InternalFailure }

public sealed record EngineError(EngineErrorCode Code, string Subsystem, string Message, bool IsRetryable = false);

public sealed record EngineResult<T>(T? Value, EngineError? Error)
{
    public bool IsSuccess => Error is null;
    public static EngineResult<T> Success(T value) => new(value, null);
    public static EngineResult<T> Failure(EngineErrorCode code, string subsystem, string message, bool retryable = false) =>
        new(default, new EngineError(code, subsystem, message, retryable));
}

public sealed record EngineStatus(EngineLifecycleState State, long Revision, string? SafeErrorCode = null);
public sealed record NodeSnapshot(Guid NodeId, string NodeName, EngineStatus Status, long SettingsRevision, long EventWatermark);
public sealed record EngineVersionInfo(string EngineApiVersion, string ProductVersion, int AudioProtocolVersion, int ControlProtocolVersion);
public sealed record RestartRequest(string Reason, Guid? CorrelationId = null);
public sealed record StreamRequest(Guid SenderNodeId, Guid ReceiverNodeId);
public sealed record OperationReceipt(Guid OperationId, string State);
public sealed record PeerSnapshot(Guid NodeId, string DisplayName);
public sealed record ControlPeerSnapshot(ulong ProtocolNodeId, Guid EngineNodeId, string Name, string Platform,
    ushort Capabilities, string Address, ushort AudioPort, int ControlPort, byte Status, string StatusName,
    DateTimeOffset LastSeenUtc, long LastSeenTimestamp, string ProtocolVersion);
public sealed record ControlPlaneStatisticsSnapshot(long PacketsSent, long BytesSent, long PacketsReceived,
    long BytesReceived, long HelloPackets, long Heartbeats, long UnknownPackets, long MalformedPackets,
    long PeerTimeouts, long Reconnects, int RegistrySize, int CurrentSessions,
    TimeSpan DiscoveryDuration, double? AverageRoundTripMilliseconds);
public sealed record StreamSnapshot(Guid SenderNodeId, Guid ReceiverNodeId, string State);
public sealed record StatisticsSnapshot(DateTimeOffset SampledAt, ImmutableDictionary<string, long> Counters);
public sealed record SettingsSnapshot(int SchemaVersion, long Revision, Guid NodeId, string NodeName);
public sealed record SettingsPatch(long ExpectedRevision, string? NodeName = null);
public sealed record DiagnosticsSnapshot(EngineStatus Status, ImmutableArray<string> Capabilities,
    string Health = "Unavailable", TimeSpan Uptime = default, double? CpuPercent = null,
    long? WorkingSetBytes = null, EngineVersionInfo? Version = null,
    ImmutableDictionary<string, long>? Statistics = null, string? SafeErrorCode = null);
public sealed record AudioDeviceSnapshot(string Id, string Name, string Direction);
public sealed record AudioDeviceSelection(string? CaptureDeviceId, string? PlaybackDeviceId);
public sealed record LaneSelection(bool Sender, bool Receiver);
public sealed record EventSubscription(int Capacity = 64);
public sealed record EngineEvent(Guid EngineInstanceId, long Sequence, DateTimeOffset OccurredAt, string Kind, EngineStatus Status);

public interface IEngine
{
    Task<EngineResult<EngineStatus>> StartAsync(CancellationToken cancellationToken);
    Task<EngineResult<EngineStatus>> StopAsync(CancellationToken cancellationToken);
    Task<EngineResult<EngineStatus>> RestartAsync(RestartRequest request, CancellationToken cancellationToken);
    Task<EngineResult<EngineStatus>> GetStatusAsync(CancellationToken cancellationToken);
    Task<EngineResult<NodeSnapshot>> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<EngineResult<ImmutableArray<StreamSnapshot>>> GetStreamsAsync(CancellationToken cancellationToken);
    Task<EngineResult<OperationReceipt>> StartStreamingAsync(StreamRequest request, CancellationToken cancellationToken);
    Task<EngineResult<OperationReceipt>> StopStreamingAsync(StreamRequest request, CancellationToken cancellationToken);
    Task<EngineResult<ImmutableArray<PeerSnapshot>>> GetPeersAsync(CancellationToken cancellationToken);
    Task<EngineResult<ImmutableArray<ControlPeerSnapshot>>> GetControlPeersAsync(CancellationToken cancellationToken);
    Task<EngineResult<ControlPeerSnapshot?>> GetControlPeerAsync(ulong protocolNodeId, CancellationToken cancellationToken);
    Task<EngineResult<ControlPlaneStatisticsSnapshot>> GetControlStatisticsAsync(CancellationToken cancellationToken);
    Task<EngineResult<StatisticsSnapshot>> GetStatisticsAsync(CancellationToken cancellationToken);
    Task<EngineResult<SettingsSnapshot>> GetSettingsAsync(CancellationToken cancellationToken);
    Task<EngineResult<SettingsSnapshot>> UpdateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken);
    Task<EngineResult<DiagnosticsSnapshot>> GetDiagnosticsAsync(CancellationToken cancellationToken);
    Task<EngineResult<ImmutableArray<AudioDeviceSnapshot>>> GetAudioDevicesAsync(CancellationToken cancellationToken);
    Task<EngineResult<OperationReceipt>> SelectAudioDevicesAsync(AudioDeviceSelection selection, CancellationToken cancellationToken);
    Task<EngineResult<OperationReceipt>> PauseAsync(LaneSelection lanes, CancellationToken cancellationToken);
    Task<EngineResult<OperationReceipt>> ResumeAsync(LaneSelection lanes, CancellationToken cancellationToken);
    Task<EngineResult<EngineStatus>> RestartDiscoveryAsync(CancellationToken cancellationToken);
    EngineVersionInfo GetVersionInfo();
    IAsyncEnumerable<EngineEvent> SubscribeAsync(EventSubscription subscription, CancellationToken cancellationToken);
}
