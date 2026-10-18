namespace AudioStreamer.AudioAgent.Contracts;

public enum AgentChannel : byte { Control = 1, CapturePcm = 2, PlaybackPcm = 3 }
public enum AgentMessageType : ushort
{
    Hello = 1, Ready = 2, Shutdown = 3, CaptureStart = 4, CaptureStop = 5,
    PlaybackStart = 6, PlaybackStop = 7, DeviceListRequest = 8, DeviceListResponse = 9,
    Heartbeat = 10, Error = 11,
    CapturePause = 15, CaptureResume = 16, PlaybackPause = 17, PlaybackResume = 18,
    CapturePcm = 19, PlaybackPcm = 20, DeviceChanged = 21, SelectionRequest = 22,
    SelectionResponse = 23, OperationResponse = 24, PlatformEvent = 26
}

public sealed record AgentFrame(ushort Version, AgentChannel Channel, AgentMessageType MessageType,
    uint SequenceNumber, Guid CorrelationId, ReadOnlyMemory<byte> Payload, AgentPcmMetadata? PcmMetadata = null);
public sealed record AgentHello(int MinimumVersion, int MaximumVersion, Guid InstanceId);
public sealed record AgentReady(int NegotiatedVersion, Guid InstanceId, string AgentVersion,
    int SessionId = -1, string? UserSid = null, int ProcessId = 0);
public sealed record AgentShutdown(string Reason);
public sealed record DeviceListRequest();
public sealed record AgentDevice(string Id, string Name, string Direction, bool IsDefault, bool IsAvailable,
    string State = "Active", bool LoopbackCapable = false, int? MixSampleRate = null, int? MixChannels = null,
    string? MixSampleFormat = null);
public sealed record DeviceListResponse(IReadOnlyList<AgentDevice> Devices);
public sealed record AgentSelectionRequest(string? CaptureDeviceId, string? PlaybackDeviceId);
public sealed record AgentOperationRequest(Guid StreamId, string? DeviceId, int SampleRate = 0, int Channels = 0,
    string? SampleFormat = null, string? CaptureSource = null, int QueueCapacity = 8,
    int BufferDurationMilliseconds = 100, int RequestedLatencyMilliseconds = 50, uint InitialSequenceNumber = 0);
public sealed record AgentOperationResponse(bool Succeeded, string? ErrorCode = null, string? SafeMessage = null,
    bool IsRetryable = false);
public sealed record AgentPcmMetadata(Guid StreamId, uint SequenceNumber, long TimestampTicks,
    int SampleRate, int Channels, string SampleFormat, int FrameCount);
public sealed record AgentPlatformEvent(string Kind, DateTimeOffset OccurredAt, string? DeviceId = null,
    long Count = 0, string? SafeErrorCode = null);
public sealed record AgentHeartbeat(DateTimeOffset SentAtUtc);
public sealed record AgentError(string Code, string Message, bool IsRetryable);
public static class AgentProtocol
{
    public const ushort CurrentVersion = 2;
    public const int HeaderLength = 36;
    public const int MaximumPayloadLength = 1024 * 1024;
    public const uint Magic = 0x41535452; // ASTR
    public const int MaximumPcmPayloadLength = 256 * 1024;
}
