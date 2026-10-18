using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioStreamer.IPC.Contracts;

public static class IpcProtocol
{
    public const string PipeName = "AudioStreamer.Node.v1";
    public const int MajorVersion = 1;
    public const int MinorVersion = 0;
    public const int MaximumFrameBytes = 1_048_576;
    public const int MaximumHelloVersions = 8;
    public static readonly TimeSpan IncompleteFrameTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    public const int MissedHeartbeatLimit = 3;

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            MaxDepth = 32,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public sealed record IpcSchemaVersion(int Major, int Minor);

public sealed record IpcEnvelope
{
    public required IpcSchemaVersion SchemaVersion { get; init; }
    public required string Kind { get; init; }
    public required string MessageType { get; init; }
    public Guid? RequestId { get; init; }
    public Guid ConnectionEpoch { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public long? EventSequence { get; init; }
    public required JsonElement Payload { get; init; }
}

public sealed record HelloRequest
{
    public required ImmutableArray<IpcSchemaVersion> SupportedVersions { get; init; }
    public required string ClientVersion { get; init; }
    public required int ProcessId { get; init; }
    public required string RequestedRole { get; init; }
}

public sealed record HelloResult
{
    public required IpcSchemaVersion SelectedVersion { get; init; }
    public required string ServiceVersion { get; init; }
    public required string EngineVersion { get; init; }
    public required string MinimumSupportedClientVersion { get; init; }
    public required ImmutableArray<string> Capabilities { get; init; }
    public required Guid ConnectionEpoch { get; init; }
    public required string ServiceState { get; init; }
    public required string NodeState { get; init; }
}

public sealed record EmptyPayload;
public sealed record PingPayload(DateTimeOffset? ClientTimestamp = null);
public sealed record PingResult(DateTimeOffset ServerTimestamp, string ServiceState);
public sealed record SubscribeRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<string> EventKinds = default);
public sealed record SubscriptionResult(bool IsSubscribed);
public sealed record UpdateSettingsRequest(long ExpectedRevision, string? NodeName);

public sealed record IpcError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public bool Retryable { get; init; }
    public ImmutableDictionary<string, string>? FieldErrors { get; init; }
}

public sealed record IpcOperationResult<T>
{
    public required string Result { get; init; }
    public T? Data { get; init; }
    public IpcError? Error { get; init; }
}

public sealed record ErrorResponse(string Result, IpcError Error);
public sealed record EventEnvelope(string MessageType, long EventSequence, Guid ConnectionEpoch,
    DateTimeOffset Timestamp, JsonElement Payload);

public sealed record StatusSnapshot(string State, long Revision, string? SafeErrorCode);
public sealed record NodeSnapshot(Guid NodeId, string NodeName, StatusSnapshot Status, long SettingsRevision, long EventWatermark);
public sealed record SettingsSnapshot(int SchemaVersion, long Revision, Guid NodeId, string NodeName);
public sealed record DiagnosticsSnapshot(string State, ImmutableArray<string> Capabilities,
    string Health = "Unknown", TimeSpan Uptime = default, double? CpuPercent = null,
    long? WorkingSetBytes = null, ImmutableDictionary<string, long>? Statistics = null,
    string? SafeErrorCode = null);
public sealed record VersionSnapshot(string ServiceVersion, string EngineVersion, string IpcVersion);
public sealed record IpcEventPayload(string State, long Revision, string? SafeErrorCode, long? SettingsRevision = null);
public sealed record PeerNodeRequest(Guid NodeId);
public sealed record IpcPeerSnapshot(Guid NodeId, string ProtocolNodeId, string Name, string Platform,
    ushort Capabilities, string Address, int AudioPort, int ControlPort, string Status,
    DateTimeOffset LastSeenUtc, string ProtocolVersion);
public sealed record IpcStreamSnapshot(Guid SenderNodeId, Guid ReceiverNodeId, string State);
public sealed record IpcLaneSelection(bool Sender, bool Receiver);
public sealed record IpcSessionEvent(Guid SenderNodeId, Guid ReceiverNodeId, string PreviousState, string State);
public sealed record IpcAudioRuntimeEvent(string Kind, DateTimeOffset OccurredAt, string? DeviceId,
    long Count, string? SafeErrorCode);
public sealed record ControlStatisticsSnapshot(long PacketsSent, long BytesSent, long PacketsReceived,
    long BytesReceived, long HelloPackets, long Heartbeats, long UnknownPackets, long MalformedPackets,
    long PeerTimeouts, long Reconnects, int RegistrySize, int CurrentSessions,
    TimeSpan DiscoveryDuration, double? AverageRoundTripMilliseconds);

public static class IpcErrors
{
    public const string InvalidRequest = "InvalidRequest";
    public const string InvalidField = "InvalidField";
    public const string Unauthorized = "Unauthorized";
    public const string RateLimited = "RateLimited";
    public const string NodeNotRunning = "NodeNotRunning";
    public const string NodeStarting = "NodeStarting";
    public const string NodeStopping = "NodeStopping";
    public const string CapabilityUnavailable = "CapabilityUnavailable";
    public const string PeerNotFound = "PeerNotFound";
    public const string TransportUnavailable = "TransportUnavailable";
    public const string StreamStartFailed = "StreamStartFailed";
    public const string ConfigurationConflict = "ConfigurationConflict";
    public const string IpcVersionUnsupported = "IpcVersionUnsupported";
    public const string ServiceStopping = "ServiceStopping";
    public const string OperationTimedOut = "OperationTimedOut";
    public const string InternalError = "InternalError";
}

public static class IpcEnvelopeFactory
{
    public static IpcEnvelope Request<T>(string messageType, Guid requestId, Guid connectionEpoch, T payload,
        IpcSchemaVersion? schema = null) => Create("request", messageType, requestId, connectionEpoch, payload, schema);

    public static IpcEnvelope Response<T>(string messageType, Guid requestId, Guid connectionEpoch, T payload,
        IpcSchemaVersion? schema = null) => Create("response", messageType, requestId, connectionEpoch, payload, schema);

    public static IpcEnvelope Event<T>(string messageType, long eventSequence, Guid connectionEpoch, T payload,
        IpcSchemaVersion? schema = null)
    {
        var eventMessage = new EventEnvelope(messageType, eventSequence, connectionEpoch, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(payload, IpcProtocol.JsonOptions));
        return new IpcEnvelope
        {
            SchemaVersion = schema ?? new IpcSchemaVersion(IpcProtocol.MajorVersion, IpcProtocol.MinorVersion),
            Kind = "event",
            MessageType = eventMessage.MessageType,
            ConnectionEpoch = eventMessage.ConnectionEpoch,
            Timestamp = eventMessage.Timestamp,
            EventSequence = eventMessage.EventSequence,
            Payload = eventMessage.Payload
        };
    }

    private static IpcEnvelope Create<T>(string kind, string messageType, Guid? requestId, Guid connectionEpoch,
        T payload, IpcSchemaVersion? schema) => new()
        {
            SchemaVersion = schema ?? new IpcSchemaVersion(IpcProtocol.MajorVersion, IpcProtocol.MinorVersion),
            Kind = kind,
            MessageType = messageType,
            RequestId = requestId,
            ConnectionEpoch = connectionEpoch,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(payload, IpcProtocol.JsonOptions)
        };
}
