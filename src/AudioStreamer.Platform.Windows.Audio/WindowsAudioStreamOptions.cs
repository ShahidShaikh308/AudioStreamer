using System.Globalization;
using System.Net;
using AudioStreamer.Audio.Transport.Transport;
using AudioStreamer.Engine.Contracts;
using Microsoft.Extensions.Configuration;

namespace AudioStreamer.Platform.Windows.Audio;

/// <summary>Service configuration for the local audio transport lane. Runtime endpoints are externalized.</summary>
public sealed record WindowsAudioStreamOptions(AudioUdpOptions Udp, string? CaptureDeviceId,
    string? PlaybackDeviceId, AudioFormat Format)
{
    public void Validate()
    {
        Udp.Validate();
        Format.EnsureSupported();
        if (CaptureDeviceId?.Length > 256 || PlaybackDeviceId?.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(CaptureDeviceId));
    }

    public static WindowsAudioStreamOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("AudioTransport");
        string Required(string key) => !string.IsNullOrWhiteSpace(section[key])
            ? section[key]!
            : throw new InvalidOperationException($"Missing required configuration value AudioTransport:{key}.");
        int Integer(string key) => int.Parse(Required(key), NumberStyles.Integer, CultureInfo.InvariantCulture);
        var bind = new IPEndPoint(IPAddress.Parse(Required("BindAddress")), Integer("ReceivePort"));
        var udp = new AudioUdpOptions(bind, Integer("SendQueueCapacity"), Integer("ReceiveQueueCapacity"),
            TimeSpan.FromMilliseconds(Integer("ReceiveBufferMilliseconds")));
        var format = new AudioFormat(Integer("SampleRate"), 2, PcmSampleFormat.Float32);
        var options = new WindowsAudioStreamOptions(udp, section["CaptureDeviceId"], section["PlaybackDeviceId"], format);
        options.Validate();
        return options;
    }
}
