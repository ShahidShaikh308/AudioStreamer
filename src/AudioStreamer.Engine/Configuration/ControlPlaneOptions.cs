using System.Text;
using AudioStreamer.Control.Protocol;
using AudioStreamer.Engine.Contracts;

namespace AudioStreamer.Engine.Configuration;

/// <summary>Platform-neutral Control Plane policy and transport configuration loaded by the host.</summary>
public sealed record ControlPlaneOptions(
    ControlTransportOptions Transport,
    ushort AudioReceivePort,
    TimeSpan HeartbeatInterval,
    TimeSpan SessionTimeout,
    string LocalPlatform = "unknown",
    ControlCapability LocalCapabilities = ControlCapability.Capture | ControlCapability.Playback |
        ControlCapability.Sender | ControlCapability.Receiver,
    string? ControllerSecretHex = null)
{
    public byte[]? ControllerSecret => string.IsNullOrWhiteSpace(ControllerSecretHex)
        ? null
        : AuthenticatedControllerCommandCodec.ParseSecret(ControllerSecretHex);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Transport);
        if (string.IsNullOrWhiteSpace(Transport.BindAddress) || Transport.Port is < 1 or > 65535 ||
            Transport.OutboundQueueCapacity is < 8 or > 4096 || string.IsNullOrWhiteSpace(Transport.BroadcastAddress) ||
            AudioReceivePort == 0 ||
            string.IsNullOrWhiteSpace(LocalPlatform) || LocalPlatform.Contains('\0') ||
            Encoding.UTF8.GetByteCount(LocalPlatform) > byte.MaxValue ||
            (LocalCapabilities & ~(ControlCapability)0x000f) != 0)
            throw new ArgumentOutOfRangeException(nameof(Transport), "Control Plane bounds are invalid.");
        if (HeartbeatInterval < TimeSpan.FromMilliseconds(250) || HeartbeatInterval > TimeSpan.FromMinutes(1) ||
            SessionTimeout < TimeSpan.FromMilliseconds(250) || SessionTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(HeartbeatInterval), "Control Plane intervals are invalid.");
    }
}
