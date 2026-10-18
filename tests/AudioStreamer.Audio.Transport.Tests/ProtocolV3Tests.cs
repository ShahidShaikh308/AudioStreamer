using System.Net;
using System.Net.Sockets;
using System.Buffers;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Audio.Transport.Protocol;
using AudioStreamer.Audio.Transport.Transport;
using Xunit;

namespace AudioStreamer.Audio.Transport.Tests;

public sealed class ProtocolV3Tests
{
    [Fact]
    public void CanonicalDocumentVectorMatchesPythonOracle()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "audio_v3_canonical.hex");
        var expected = Convert.FromHexString(File.ReadAllText(path).Trim());
        var packet = new AudioProtocolV3Packet(0x01020304, 0x0102030405060708, 48000, 2, 1,
            Convert.FromHexString("0000803f000080bf"));
        var output = new byte[1200];
        var length = AudioProtocolV3Codec.Serialize(packet, output);
        Assert.Equal(expected, output.AsSpan(0, length).ToArray());
        var parsed = AudioProtocolV3Codec.Parse(expected);
        Assert.Equal(packet.SequenceNumber, parsed.SequenceNumber);
        Assert.Equal(packet.StreamId, parsed.StreamId);
        Assert.Equal(packet.SampleRate, parsed.SampleRate);
        Assert.Equal(packet.Payload.ToArray(), parsed.Payload.ToArray());
    }

    [Theory]
    [InlineData("0000000003", "Invalid protocol magic")]
    [InlineData("4153415502", "Unsupported protocol version")]
    public void ParserRejectsInvalidDatagrams(string prefixHex, string message)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "audio_v3_canonical.hex");
        var canonical = Convert.FromHexString(File.ReadAllText(path).Trim());
        Convert.FromHexString(prefixHex).CopyTo(canonical, 0);
        Assert.Contains(message, Assert.Throws<AudioProtocolException>(() => AudioProtocolV3Codec.Parse(canonical)).Message);
    }

    [Fact]
    public void PacketizerKeepsStereoFramesAndMtuBounded()
    {
        var pcm = new byte[AudioProtocolV3Codec.StereoFloat32FrameSize * 400];
        var packets = AudioProtocolV3Packetizer.Split(pcm).ToArray();
        Assert.Equal(new[] { 1168, 1168, 864 }, packets.Select(packet => packet.Length));
        Assert.All(packets, packet => Assert.Equal(0, packet.Length % 8));
    }

    [Fact]
    public async Task ReceiveQueueBoundsDurationAndDropsOldestOnOverflow()
    {
        await using var queue = new AudioReceiveQueue(2, TimeSpan.FromMilliseconds(20));
        var payload = new byte[8 * 480];
        Assert.True(queue.Offer(Packet(0, 1, payload)));
        Assert.True(queue.Offer(Packet(1, 1, payload)));
        Assert.True(queue.Offer(Packet(2, 1, payload)));
        Assert.Equal(1, queue.Snapshot.DroppedPackets);
        Assert.Equal(2, queue.Snapshot.PacketCount);
        Assert.Equal(1u, (await queue.TakeAsync(CancellationToken.None))!.SequenceNumber);
    }

    [Fact]
    public async Task ReceiveQueueReordersPacketsAndSuppressesDuplicates()
    {
        await using var queue = new AudioReceiveQueue(8, TimeSpan.FromMilliseconds(100),
            targetLatency: TimeSpan.FromMilliseconds(10), minimumLatency: TimeSpan.FromMilliseconds(5));
        var payload = new byte[8 * 480];
        Assert.True(queue.Offer(Packet(0, 11, payload)));
        Assert.True(queue.Offer(Packet(2, 11, payload)));
        Assert.True(queue.Offer(Packet(1, 11, payload)));
        Assert.False(queue.Offer(Packet(1, 11, payload)));

        Assert.Equal(new uint[] { 0, 1, 2 }, new[]
        {
            (await queue.TakeAsync(CancellationToken.None))!.SequenceNumber,
            (await queue.TakeAsync(CancellationToken.None))!.SequenceNumber,
            (await queue.TakeAsync(CancellationToken.None))!.SequenceNumber
        });
        Assert.True(queue.Snapshot.ReorderedPackets > 0);
        Assert.Equal(1, queue.Snapshot.DuplicatePackets);
        Assert.True(queue.Snapshot.RecoveredPackets > 0);
    }

    [Fact]
    public async Task ReceiverRoutesPacketsToTheMatchingPeerQueue()
    {
        await using var receiver = new AudioUdpReceiver(new IPEndPoint(IPAddress.Loopback, 0), 8,
            TimeSpan.FromMilliseconds(100));
        var queue = receiver.RegisterSource(IPAddress.Loopback);
        receiver.Start();
        var endpoint = Assert.IsType<IPEndPoint>(receiver.BoundEndpoint);
        var packetBytes = new byte[AudioProtocolV3Codec.MaximumDatagramSize];
        var packetLength = AudioProtocolV3Codec.Serialize(Packet(0, 12, new byte[8]), packetBytes);
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        await sender.SendToAsync(packetBytes.AsMemory(0, packetLength), SocketFlags.None, endpoint, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var received = await queue.TakeAsync(timeout.Token);
        Assert.NotNull(received);
        Assert.Equal(12ul, received!.StreamId);
        Assert.Equal(0, receiver.Queue.Snapshot.PacketCount);
        await receiver.UnregisterSourceAsync(IPAddress.Loopback, queue);
    }

    [Fact]
    public async Task ReceiveQueueCountsMissingPacketsAndPlaybackUnderrun()
    {
        await using var queue = new AudioReceiveQueue(8, TimeSpan.FromMilliseconds(100),
            targetLatency: TimeSpan.FromMilliseconds(5), minimumLatency: TimeSpan.FromMilliseconds(5));
        var payload = new byte[8 * 480];
        Assert.True(queue.Offer(Packet(0, 13, payload)));
        Assert.True(queue.Offer(Packet(2, 13, payload)));
        Assert.Equal(0u, (await queue.TakeAsync(CancellationToken.None))!.SequenceNumber);
        Assert.Equal(2u, (await queue.TakeAsync(CancellationToken.None))!.SequenceNumber);
        Assert.Equal(1, queue.Snapshot.MissingPackets);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await queue.TakeAsync(timeout.Token).AsTask());
        Assert.True(queue.Snapshot.Underruns > 0);
    }

    [Fact]
    public void SequenceTrackerHandlesWrapReorderAndStreamRestart()
    {
        var tracker = new AudioSequenceTracker();
        Assert.True(tracker.TryAccept(uint.MaxValue, 1, out var missing));
        Assert.True(tracker.TryAccept(0, 1, out missing));
        Assert.Equal(0u, missing);
        Assert.False(tracker.TryAccept(uint.MaxValue, 1, out _));
        Assert.True(tracker.TryAccept(8, 2, out missing));
        Assert.Equal(0u, missing);
    }

    [Fact]
    public async Task UdpReceiverReceivesAndParsesPythonCanonicalBytes()
    {
        await using var receiver = new AudioUdpReceiver(new IPEndPoint(IPAddress.Loopback, 0), 4,
            TimeSpan.FromMilliseconds(100));
        receiver.Start();
        var endpoint = Assert.IsType<IPEndPoint>(receiver.BoundEndpoint);
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "audio_v3_canonical.hex");
        var bytes = Convert.FromHexString(File.ReadAllText(path).Trim());
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        await sender.SendToAsync(bytes, SocketFlags.None, endpoint, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var packet = await receiver.Queue.TakeAsync(timeout.Token);
        Assert.NotNull(packet);
        Assert.Equal(0x01020304u, packet!.SequenceNumber);
        Assert.Equal(1, receiver.Snapshot.PacketsReceived);
    }

    [Fact]
    public async Task UdpSenderPacketizesAndSendsAudioFrameToLoopback()
    {
        using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var destination = (IPEndPoint)receiver.LocalEndPoint!;
        await using var sender = new AudioUdpSender(destination, 4, new IdentityConverter());
        sender.Start();
        var owner = new TestMemoryOwner(8);
        owner.Memory.Span.Clear();
        using var frame = new AudioFrame(owner, 8, new AudioFormat(48000, 2, PcmSampleFormat.Float32), 1, 0);

        Assert.True(sender.Enqueue(frame));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        var buffer = new byte[AudioProtocolV3Codec.MaximumDatagramSize];
        var received = await receiver.ReceiveFromAsync(buffer.AsMemory(), SocketFlags.None, remote, timeout.Token);
        var packet = AudioProtocolV3Codec.Parse(buffer.AsSpan(0, received.ReceivedBytes));
        await sender.StopAsync();

        Assert.Equal(sender.StreamId, packet.StreamId);
        Assert.Equal(0u, packet.SequenceNumber);
        Assert.Equal(8, packet.Payload.Length);
        Assert.Equal(1, sender.Snapshot.PacketsSent);
    }

    [Fact]
    public async Task UdpSenderDrainsPacketizedPcmWithoutTimerQuantization()
    {
        using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var destination = (IPEndPoint)receiver.LocalEndPoint!;
        await using var sender = new AudioUdpSender(destination, 4, new IdentityConverter());
        sender.Start();

        const int framesPerPacket = AudioProtocolV3Codec.MaximumPayloadSize /
            AudioProtocolV3Codec.StereoFloat32FrameSize;
        var owner = new TestMemoryOwner(framesPerPacket * 2 * sizeof(float) * 2);
        owner.Memory.Span.Clear();
        using var frame = new AudioFrame(owner, owner.Memory.Length,
            new AudioFormat(48000, 2, PcmSampleFormat.Float32), framesPerPacket * 2, 0);
        Assert.True(sender.Enqueue(frame));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var buffer = new byte[AudioProtocolV3Codec.MaximumDatagramSize];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        var first = await receiver.ReceiveFromAsync(buffer.AsMemory(), SocketFlags.None, remote, timeout.Token);
        var second = await receiver.ReceiveFromAsync(buffer.AsMemory(), SocketFlags.None, remote, timeout.Token);
        await sender.StopAsync();

        Assert.Equal(25 + framesPerPacket * 2 * sizeof(float), first.ReceivedBytes);
        Assert.Equal(first.ReceivedBytes, second.ReceivedBytes);
        Assert.Equal(2, sender.Snapshot.PacketsSent);
        Assert.Equal(0, sender.Snapshot.DroppedPackets);
    }

    private static AudioProtocolV3Packet Packet(uint sequence, ulong stream, byte[] payload) =>
        new(sequence, stream, 48000, 2, 1, payload);

    private sealed class IdentityConverter : IAudioFormatConverter
    {
        public int Convert(ReadOnlySpan<byte> source, AudioFormat sourceFormat, Span<byte> destination, AudioFormat destinationFormat)
        {
            source.CopyTo(destination);
            return source.Length / destinationFormat.BytesPerFrame;
        }
    }

    private sealed class TestMemoryOwner(int size) : IMemoryOwner<byte>
    {
        private byte[]? _buffer = new byte[size];
        public Memory<byte> Memory => _buffer ?? throw new ObjectDisposedException(nameof(TestMemoryOwner));
        public void Dispose() => _buffer = null;
    }
}
