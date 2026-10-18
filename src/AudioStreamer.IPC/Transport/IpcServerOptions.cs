namespace AudioStreamer.IPC.Transport;

using AudioStreamer.IPC.Contracts;

public sealed record IpcServerOptions
{
    public string PipeName { get; init; } = IpcProtocol.PipeName;
    public int MaximumClients { get; init; } = 8;
    public int RequestQueueCapacity { get; init; } = 64;
    public int RequestWorkersPerClient { get; init; } = 4;
    public int ReadRateLimitCapacity { get; init; } = 120;
    public int ReadRateLimitTokensPerSecond { get; init; } = 60;
    public int MutationRateLimitCapacity { get; init; } = 10;
    public int MutationRateLimitTokensPerSecond { get; init; } = 2;
    public int OutboundQueueCapacity { get; init; } = 128;
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan IncompleteFrameTimeout { get; init; } = IpcProtocol.IncompleteFrameTimeout;
    public TimeSpan ReadRequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MutationTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan LifecycleTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ShutdownDrainTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan HeartbeatInterval { get; init; } = IpcProtocol.HeartbeatInterval;
    public int MissedHeartbeatLimit { get; init; } = IpcProtocol.MissedHeartbeatLimit;
    public int PipeBufferSizeBytes { get; init; } = 64 * 1024;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PipeName) || PipeName.Length > 200 || PipeName.Contains('\\') || PipeName.Contains('/'))
            throw new ArgumentException("IPC pipe name is invalid.", nameof(PipeName));
        if (MaximumClients is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(MaximumClients));
        if (RequestQueueCapacity is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(RequestQueueCapacity));
        if (RequestWorkersPerClient is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(RequestWorkersPerClient));
        if (ReadRateLimitCapacity is < 1 or > 100_000 || ReadRateLimitTokensPerSecond is < 1 or > 100_000 ||
            MutationRateLimitCapacity is < 1 or > 100_000 || MutationRateLimitTokensPerSecond is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(ReadRateLimitCapacity), "IPC rate-limit capacities and refill rates must be within supported bounds.");
        if (OutboundQueueCapacity is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(OutboundQueueCapacity));
        if (PipeBufferSizeBytes is < 4096 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(PipeBufferSizeBytes));
        if (HandshakeTimeout <= TimeSpan.Zero || IncompleteFrameTimeout <= TimeSpan.Zero ||
            ReadRequestTimeout <= TimeSpan.Zero || MutationTimeout <= TimeSpan.Zero ||
            LifecycleTimeout <= TimeSpan.Zero || ShutdownDrainTimeout < TimeSpan.Zero ||
            HeartbeatInterval <= TimeSpan.Zero || MissedHeartbeatLimit is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(HandshakeTimeout), "IPC timeouts must be positive (shutdown may be zero).");
    }
}
