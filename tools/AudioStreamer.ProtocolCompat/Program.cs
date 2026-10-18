using System.Globalization;
using System.Text.Json;
using AudioStreamer.Audio.Transport.Protocol;

try
{
    var json = await Console.In.ReadToEndAsync();
    var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    var request = JsonSerializer.Deserialize<Request>(json, jsonOptions) ?? throw new InvalidOperationException("Missing request.");
    if (request.Operation == "parse")
    {
        var packet = AudioProtocolV3Codec.Parse(Convert.FromBase64String(request.Datagram!));
        Console.Write(JsonSerializer.Serialize(new Response(packet.SequenceNumber, packet.StreamId,
            packet.SampleRate, packet.Channels, packet.SampleFormat, Convert.ToBase64String(packet.Payload.Span)), jsonOptions));
    }
    else if (request.Operation == "serialize")
    {
        var packet = new AudioProtocolV3Packet(uint.Parse(request.Sequence!, CultureInfo.InvariantCulture),
            ulong.Parse(request.StreamId!, CultureInfo.InvariantCulture), request.SampleRate!.Value,
            request.Channels!.Value, request.SampleFormat!.Value, Convert.FromBase64String(request.Payload!));
        var datagram = new byte[AudioProtocolV3Codec.HeaderSize + packet.Payload.Length];
        var length = AudioProtocolV3Codec.Serialize(packet, datagram);
        Console.Write(Convert.ToBase64String(datagram.AsSpan(0, length)));
    }
    else throw new InvalidOperationException("Operation must be parse or serialize.");
}
catch (Exception exception)
{
    Console.Error.Write(JsonSerializer.Serialize(new { error = exception.GetType().Name, exception.Message }));
    Environment.ExitCode = 2;
}

internal sealed record Request(string Operation, string? Datagram = null, string? Sequence = null,
    string? StreamId = null, int? SampleRate = null, byte? Channels = null,
    byte? SampleFormat = null, string? Payload = null);
internal sealed record Response(uint SequenceNumber, ulong StreamId, int SampleRate,
    byte Channels, byte SampleFormat, string Payload);
