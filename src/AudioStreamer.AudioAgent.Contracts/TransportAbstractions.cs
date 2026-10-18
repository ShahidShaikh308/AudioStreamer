namespace AudioStreamer.AudioAgent.Contracts;

public interface IAgentConnection : IAsyncDisposable
{
    AgentChannel Channel { get; }
    bool IsConnected { get; }
    ValueTask SendAsync(AgentFrame frame, CancellationToken cancellationToken);
    ValueTask<AgentFrame> ReceiveAsync(CancellationToken cancellationToken);
}

public interface IAgentTransport
{
    ValueTask<IAgentConnection> ConnectAsync(AgentChannel channel, CancellationToken cancellationToken);
}

public interface IAgentServer : IAsyncDisposable
{
    ValueTask<IAgentConnection> AcceptAsync(AgentChannel channel, CancellationToken cancellationToken);
}

public interface IAgentClient
{
    bool IsConnected { get; }
    ValueTask ConnectAsync(CancellationToken cancellationToken);
    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);
    ValueTask<AgentReady> HandshakeAsync(CancellationToken cancellationToken);
}

/// <summary>Audio-plane operations over the independent agent channels.</summary>
public interface IAgentAudioClient : IAgentClient
{
    event Action<DeviceListResponse>? DevicesChanged;
    event Action<AgentPlatformEvent>? PlatformEventReceived;
    ValueTask<DeviceListResponse> EnumerateDevicesAsync(CancellationToken cancellationToken);
    ValueTask<AgentOperationResponse> SelectDevicesAsync(AgentSelectionRequest request, CancellationToken cancellationToken);
    ValueTask<AgentOperationResponse> StartCaptureAsync(AgentOperationRequest request, CancellationToken cancellationToken);
    ValueTask<AgentOperationResponse> StopCaptureAsync(Guid streamId, CancellationToken cancellationToken);
    ValueTask<AgentOperationResponse> PauseCaptureAsync(Guid streamId, bool pause, CancellationToken cancellationToken);
    ValueTask<AgentOperationResponse> StartPlaybackAsync(AgentOperationRequest request, CancellationToken cancellationToken);
    ValueTask<AgentOperationResponse> StopPlaybackAsync(Guid streamId, CancellationToken cancellationToken);
    ValueTask<AgentOperationResponse> PausePlaybackAsync(Guid streamId, bool pause, CancellationToken cancellationToken);
    IAsyncEnumerable<(AgentPcmMetadata Metadata, ReadOnlyMemory<byte> Pcm)> ReadCaptureFramesAsync(Guid streamId, CancellationToken cancellationToken);
    ValueTask WritePlaybackFrameAsync(AgentPcmMetadata metadata, ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken);
}

/// <summary>Agent-host audio implementation; transport owns framing while this port owns native audio behavior.</summary>
public interface IAgentAudioCommandHandler
{
    void AttachControlConnection(IAgentConnection connection, CancellationToken cancellationToken);
    ValueTask<AgentFrame?> HandleControlAsync(AgentFrame request, CancellationToken cancellationToken);
    Task RunCaptureChannelAsync(IAgentConnection connection, CancellationToken cancellationToken);
    Task RunPlaybackChannelAsync(IAgentConnection connection, CancellationToken cancellationToken);
}
