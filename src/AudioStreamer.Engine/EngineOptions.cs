namespace AudioStreamer.Engine;

/// <summary>Immutable, host-supplied creation options. Paths and platform resources belong to host adapters.</summary>
public sealed record EngineOptions(string DefaultNodeName = "AudioStreamer Node", int DefaultEventCapacity = 64,
    TimeSpan? EventBackpressureTimeout = null, int MaximumPeers = 256, TimeSpan? PeerExpiration = null,
    TimeSpan? DiagnosticsSampleInterval = null)
{
    public TimeSpan EffectiveEventBackpressureTimeout => EventBackpressureTimeout ?? TimeSpan.FromSeconds(2);
    public TimeSpan EffectivePeerExpiration => PeerExpiration ?? TimeSpan.FromSeconds(90);
    public TimeSpan EffectiveDiagnosticsSampleInterval => DiagnosticsSampleInterval ?? TimeSpan.FromSeconds(1);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DefaultNodeName) || DefaultNodeName.Length > 80)
            throw new ArgumentException("Default node name must contain 1 to 80 characters.", nameof(DefaultNodeName));
        if (DefaultEventCapacity is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(DefaultEventCapacity));
        if (EffectiveEventBackpressureTimeout <= TimeSpan.Zero || MaximumPeers is < 1 or > 4096 ||
            EffectivePeerExpiration <= TimeSpan.Zero || EffectiveDiagnosticsSampleInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(EventBackpressureTimeout), "Engine limits and intervals must be positive and bounded.");
    }
}
