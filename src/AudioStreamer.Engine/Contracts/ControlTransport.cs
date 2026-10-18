namespace AudioStreamer.Engine.Contracts;

/// <summary>A platform-neutral destination used by the Control Plane transport.</summary>
public sealed record ControlTransportEndpoint(string Address, int Port);

/// <summary>Binding and queue limits supplied by the host; address parsing belongs to the adapter.</summary>
public sealed record ControlTransportOptions(string BindAddress, int Port, int OutboundQueueCapacity,
    string BroadcastAddress = "255.255.255.255");

/// <summary>Base type for bounded Control Plane transport observations.</summary>
public abstract record ControlTransportEvent;

/// <summary>A received UDP payload and its platform-neutral source address.</summary>
public sealed record ControlDatagramReceived(ReadOnlyMemory<byte> Payload, ControlTransportEndpoint Source)
    : ControlTransportEvent;

/// <summary>A completed UDP send.</summary>
public sealed record ControlDatagramSent(int ByteCount) : ControlTransportEvent;

/// <summary>The host network configuration changed.</summary>
public sealed record ControlNetworkAvailabilityChanged : ControlTransportEvent;

/// <summary>A transport failure represented without platform exception types.</summary>
public sealed record ControlTransportFault(string Operation, string ErrorCode, bool IsRetryable)
    : ControlTransportEvent;

/// <summary>Stable transport failure without platform socket exception types.</summary>
public sealed class ControlTransportException(string operation, string errorCode)
    : IOException($"Control transport {operation} failed ({errorCode}).")
{
    public string Operation { get; } = operation;
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// Platform-owned Control Plane datagram transport. Payload bytes are already encoded by
/// the authoritative Control Protocol implementation and are not interpreted by adapters.
/// </summary>
public interface IControlTransport : IAsyncDisposable
{
    IAsyncEnumerable<ControlTransportEvent> ReadEventsAsync(CancellationToken cancellationToken);
    Task StartAsync(ControlTransportOptions options, CancellationToken cancellationToken);
    Task QueueAsync(ReadOnlyMemory<byte> payload, ControlTransportEndpoint destination,
        CancellationToken cancellationToken);
    Task<int> SendAsync(ReadOnlyMemory<byte> payload, ControlTransportEndpoint destination,
        CancellationToken cancellationToken);
    bool TrySend(ReadOnlyMemory<byte> payload, ControlTransportEndpoint destination);
    Task StopAsync(CancellationToken cancellationToken);
}
