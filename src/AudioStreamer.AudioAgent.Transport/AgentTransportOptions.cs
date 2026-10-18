namespace AudioStreamer.AudioAgent.Transport;

public sealed record AgentTransportOptions(string PipePrefix = "AudioStreamer.AudioAgent.v2",
    string ServiceIdentity = "NT AUTHORITY\\LOCAL SERVICE",
    TimeSpan? HeartbeatInterval = null, TimeSpan? HeartbeatTimeout = null, TimeSpan? ReconnectDelay = null,
    TimeSpan? ConnectTimeout = null)
{
    public TimeSpan EffectiveHeartbeatInterval => HeartbeatInterval ?? TimeSpan.FromSeconds(2);
    public TimeSpan EffectiveHeartbeatTimeout => HeartbeatTimeout ?? TimeSpan.FromSeconds(8);
    public TimeSpan EffectiveReconnectDelay => ReconnectDelay ?? TimeSpan.FromSeconds(2);
    public TimeSpan EffectiveConnectTimeout => ConnectTimeout ?? TimeSpan.FromSeconds(2);
    public string GetPipeName(AudioStreamer.AudioAgent.Contracts.AgentChannel channel) => $"{PipePrefix}.{channel}";
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PipePrefix) || PipePrefix.Length > 180) throw new ArgumentOutOfRangeException(nameof(PipePrefix));
        if (string.IsNullOrWhiteSpace(ServiceIdentity)) throw new ArgumentOutOfRangeException(nameof(ServiceIdentity));
        if (EffectiveHeartbeatInterval <= TimeSpan.Zero || EffectiveHeartbeatTimeout <= EffectiveHeartbeatInterval ||
            EffectiveReconnectDelay <= TimeSpan.Zero || EffectiveConnectTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(HeartbeatInterval));
    }
}
