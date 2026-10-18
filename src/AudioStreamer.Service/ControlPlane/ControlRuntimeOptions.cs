using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace AudioStreamer.Service.ControlPlane;

public sealed record ControlRuntimeOptions(IPAddress BindAddress, int Port, ushort AudioReceivePort,
    TimeSpan HeartbeatInterval, TimeSpan PeerTimeout, TimeSpan SessionTimeout,
    int OutboundQueueCapacity, int MaximumPeers, string? ControllerSecretHex = null)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(BindAddress);
        if (BindAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || Port is < 49152 or > 65535 || AudioReceivePort == 0)
            throw new ArgumentOutOfRangeException(nameof(Port));
        if (HeartbeatInterval < TimeSpan.FromMilliseconds(250) || HeartbeatInterval > TimeSpan.FromMinutes(1) ||
            PeerTimeout <= HeartbeatInterval || PeerTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(HeartbeatInterval));
        if (SessionTimeout < TimeSpan.FromMilliseconds(250) || SessionTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(SessionTimeout));
        if (OutboundQueueCapacity is < 8 or > 4096) throw new ArgumentOutOfRangeException(nameof(OutboundQueueCapacity));
        if (MaximumPeers is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(MaximumPeers));
    }

    public static ControlRuntimeOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("ControlPlane");
        string Required(string key) => !string.IsNullOrWhiteSpace(section[key])
            ? section[key]!
            : throw new InvalidOperationException($"Missing required configuration value ControlPlane:{key}.");
        int Integer(string key) => int.Parse(Required(key), NumberStyles.Integer, CultureInfo.InvariantCulture);
        var options = new ControlRuntimeOptions(IPAddress.Parse(Required("BindAddress")), Integer("Port"),
            checked((ushort)int.Parse(configuration["AudioTransport:ReceivePort"] ?? "0", CultureInfo.InvariantCulture)),
            TimeSpan.FromMilliseconds(Integer("HeartbeatMilliseconds")),
            TimeSpan.FromMilliseconds(Integer("PeerTimeoutMilliseconds")),
            TimeSpan.FromMilliseconds(Integer("SessionTimeoutMilliseconds")), Integer("OutboundQueueCapacity"),
            Integer("MaximumPeers"), section["ControllerSecret"]);
        options.Validate();
        return options;
    }
}
