using System.Text.Json;
using System.Threading;
using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.AudioAgent.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AudioStreamer.AudioAgent.Tests;

public sealed class AgentProtocolTests
{
    private static readonly AsyncLocal<string?> AmbientContext = new();

    [Fact]
    public async Task FrameCodec_RoundTripsMessageAndPayload()
    {
        var message = new AgentHello(1, 1, Guid.NewGuid());
        var frame = new AgentFrame(1, AgentChannel.Control, AgentMessageType.Hello, 12,
            Guid.NewGuid(), JsonSerializer.SerializeToUtf8Bytes(message));
        await using var stream = new MemoryStream(AgentProtocolCodec.Encode(frame));
        var decoded = await AgentProtocolCodec.ReadAsync(stream, AgentChannel.Control, CancellationToken.None);
        Assert.Equal(frame.Version, decoded.Version);
        Assert.Equal(frame.SequenceNumber, decoded.SequenceNumber);
        Assert.Equal(frame.CorrelationId, decoded.CorrelationId);
        Assert.Equal(message, JsonSerializer.Deserialize<AgentHello>(decoded.Payload.Span));
    }

    [Fact]
    public void PcmCodec_RoundTripsMetadataAndExactBytes()
    {
        var bytes = new byte[] { 0, 1, 2, 3, 254, 255, 7, 8 };
        var metadata = new AgentPcmMetadata(Guid.NewGuid(), 71, 123456789, 48000, 2, "Int16", 2);
        var encoded = AgentProtocolCodec.EncodePcm(metadata, bytes);
        var decoded = AgentProtocolCodec.DecodePcm(encoded);
        Assert.Equal(metadata, decoded.Metadata);
        Assert.Equal(bytes, decoded.Pcm.ToArray());
    }

    [Fact]
    public async Task FramedPcmSerialization_RoundTripsMetadataAndExactBytes()
    {
        var metadata = new AgentPcmMetadata(Guid.NewGuid(), 9, 123, 48000, 2, "Int16", 2);
        var pcm = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var frame = new AgentFrame(AgentProtocol.CurrentVersion, AgentChannel.PlaybackPcm,
            AgentMessageType.PlaybackPcm, metadata.SequenceNumber, Guid.Empty, pcm, metadata);
        await using var stream = new MemoryStream(AgentProtocolCodec.Encode(frame));

        var decodedFrame = await AgentProtocolCodec.ReadAsync(stream, AgentChannel.PlaybackPcm, CancellationToken.None);
        var decodedPcm = AgentProtocolCodec.DecodePcm(decodedFrame.Payload);

        Assert.Equal(metadata, decodedPcm.Metadata);
        Assert.Equal(pcm, decodedPcm.Pcm.ToArray());
    }

    [Fact]
    public void PcmCodec_RejectsMismatchedLengthAndUnsupportedFormat()
    {
        var id = Guid.NewGuid();
        Assert.Throws<InvalidDataException>(() => AgentProtocolCodec.EncodePcm(
            new AgentPcmMetadata(id, 1, 0, 48000, 2, "Int16", 2), new byte[4]));
        Assert.Throws<InvalidDataException>(() => AgentProtocolCodec.EncodePcm(
            new AgentPcmMetadata(id, 1, 0, 96000, 2, "Int16", 1), new byte[4]));
    }

    [Fact]
    public void PcmCodec_RejectsTruncatedFrameAndOversizedPayload()
    {
        Assert.Throws<InvalidDataException>(() => AgentProtocolCodec.DecodePcm(new byte[20]));
        Assert.Throws<InvalidDataException>(() => AgentProtocolCodec.EncodePcm(
            new AgentPcmMetadata(Guid.NewGuid(), 1, 0, 48000, 2, "Float32", 1),
            new byte[AgentProtocol.MaximumPcmPayloadLength + 1]));
    }

    [Fact]
    public async Task Handshake_NegotiatesSupportedVersion()
    {
        var transport = new ScriptedTransport();
        using var client = new AgentClient(transport, new AgentTransportOptions(), NullLogger<AgentClient>.Instance);
        await client.ConnectAsync(CancellationToken.None);
        var ready = await client.HandshakeAsync(CancellationToken.None);
        Assert.True(client.IsConnected);
        Assert.Equal(AgentProtocol.CurrentVersion, ready.NegotiatedVersion);
        await client.DisconnectAsync();
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Handshake_RejectsVersionMismatch()
    {
        var transport = new ScriptedTransport(unsupportedVersion: true);
        using var client = new AgentClient(transport, new AgentTransportOptions(), NullLogger<AgentClient>.Instance);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await client.ConnectAsync(CancellationToken.None));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task HeartbeatTimeout_MarksConnectionUnavailableForSupervisorRecovery()
    {
        var transport = new ScriptedTransport(heartbeatTimeout: true);
        var options = new AgentTransportOptions(HeartbeatInterval: TimeSpan.FromMilliseconds(10),
            HeartbeatTimeout: TimeSpan.FromMilliseconds(40), ReconnectDelay: TimeSpan.FromMilliseconds(10));
        using var client = new AgentClient(transport, options, NullLogger<AgentClient>.Instance);
        await client.ConnectAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);
        await transport.ConnectionDisposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await client.StopAsync(CancellationToken.None);
        Assert.False(client.IsConnected);
        Assert.Equal(3, transport.ConnectCount);
    }

    [Fact]
    public async Task ConnectionDisposal_ReportsDisconnected()
    {
        var connection = new ScriptedConnection(AgentChannel.Control, false);
        Assert.True(connection.IsConnected);
        await connection.DisposeAsync();
        Assert.False(connection.IsConnected);
    }

    [Fact]
    public async Task LocalTransport_ConnectsAndDetectsPeerDisconnect()
    {
        var options = new AgentTransportOptions(PipePrefix: $"AudioStreamer.AudioAgent.Tests.{Guid.NewGuid():N}");
        await using var server = new NamedPipeAgentServer(options);
        var accept = server.AcceptAsync(AgentChannel.Control, CancellationToken.None).AsTask();
        var transport = new NamedPipeAgentTransport(options);
        await using var client = await transport.ConnectAsync(AgentChannel.Control, CancellationToken.None);
        await using var accepted = await accept;
        await client.SendAsync(new AgentFrame(AgentProtocol.CurrentVersion, AgentChannel.Control,
            AgentMessageType.Hello, 1, Guid.NewGuid(), new byte[] { 1, 2, 3 }), CancellationToken.None);
        var received = await accepted.ReceiveAsync(CancellationToken.None);
        Assert.Equal(AgentMessageType.Hello, received.MessageType);
        await client.DisposeAsync();
        await Assert.ThrowsAnyAsync<IOException>(async () => await accepted.ReceiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AgentPipeWrites_DoNotFlowCallerExecutionContext()
    {
        var stream = new ContextObservingStream(AmbientContext);
        await using var connection = new StreamAgentConnection(AgentChannel.Control, stream);
        AmbientContext.Value = "caller-context";

        await connection.SendAsync(new AgentFrame(AgentProtocol.CurrentVersion, AgentChannel.Control,
            AgentMessageType.Heartbeat, 1, Guid.NewGuid(), Array.Empty<byte>()), CancellationToken.None);

        Assert.Null(stream.ContextObservedByWriter);
        AmbientContext.Value = null;
    }

    [Fact]
    public async Task PlaybackPcmSend_DoesNotWaitForAnAcknowledgement()
    {
        var transport = new NoAckPlaybackTransport();
        using var client = new AgentClient(transport, new AgentTransportOptions(), NullLogger<AgentClient>.Instance);
        await client.ConnectAsync(CancellationToken.None);

        var metadata = new AgentPcmMetadata(Guid.NewGuid(), 1, 10, 48000, 2, "Int16", 1);
        await client.WritePlaybackFrameAsync(metadata, new byte[] { 1, 2, 3, 4 }, CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(transport.PlaybackConnection.Sent.Task.IsCompletedSuccessfully);
        await client.DisconnectAsync();
    }

    [Fact]
    public async Task PlaybackChannelDisconnect_MarksClientUnhealthyForSupervisorRecovery()
    {
        var transport = new NoAckPlaybackTransport();
        using var client = new AgentClient(transport, new AgentTransportOptions(), NullLogger<AgentClient>.Instance);
        await client.ConnectAsync(CancellationToken.None);
        transport.PlaybackConnection.Disconnect();

        var metadata = new AgentPcmMetadata(Guid.NewGuid(), 1, 10, 48000, 2, "Int16", 1);
        await Assert.ThrowsAsync<IOException>(() => client.WritePlaybackFrameAsync(metadata,
            new byte[] { 1, 2, 3, 4 }, CancellationToken.None).AsTask());

        Assert.False(client.IsConnected);
        Assert.Equal(3, transport.ConnectCount);
    }

    private sealed class ScriptedTransport(bool unsupportedVersion = false, bool heartbeatTimeout = false) : IAgentTransport
    {
        private int _connectCount;
        public int ConnectCount => Volatile.Read(ref _connectCount);
        public TaskCompletionSource ConnectionDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IAgentConnection> ConnectAsync(AgentChannel channel, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connectCount);
            return ValueTask.FromResult<IAgentConnection>(new ScriptedConnection(channel, heartbeatTimeout,
                unsupportedVersion, ConnectionDisposed));
        }
    }

    private sealed class ContextObservingStream(AsyncLocal<string?> ambientContext) : MemoryStream
    {
        public string? ContextObservedByWriter { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ContextObservedByWriter ??= ambientContext.Value;
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class ScriptedConnection(AgentChannel channel, bool heartbeatTimeout, bool unsupportedVersion = false,
        TaskCompletionSource? disposedSignal = null) : IAgentConnection
    {
        private int _disposed;
        private bool _helloSeen;
        public AgentChannel Channel { get; } = channel;
        public bool IsConnected => Volatile.Read(ref _disposed) == 0;
        public ValueTask SendAsync(AgentFrame frame, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async ValueTask<AgentFrame> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (Channel != AgentChannel.Control)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            }
            if (!_helloSeen)
            {
                _helloSeen = true;
                var version = unsupportedVersion ? (ushort)(AgentProtocol.CurrentVersion + 1) : AgentProtocol.CurrentVersion;
                return new AgentFrame(version, Channel, AgentMessageType.Ready, 0, Guid.NewGuid(),
                    JsonSerializer.SerializeToUtf8Bytes(new AgentReady(AgentProtocol.CurrentVersion, Guid.NewGuid(), "test")));
            }
            if (heartbeatTimeout) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new AgentFrame(AgentProtocol.CurrentVersion, Channel, AgentMessageType.Heartbeat, 0, Guid.NewGuid(),
                JsonSerializer.SerializeToUtf8Bytes(new AgentHeartbeat(DateTimeOffset.UtcNow)));
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            disposedSignal?.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoAckPlaybackTransport : IAgentTransport
    {
        private int _connectCount;
        public int ConnectCount => Volatile.Read(ref _connectCount);
        public NoAckPlaybackConnection PlaybackConnection { get; private set; } = new();

        public ValueTask<IAgentConnection> ConnectAsync(AgentChannel channel, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connectCount);
            if (channel == AgentChannel.PlaybackPcm)
            {
                PlaybackConnection = new NoAckPlaybackConnection();
                return ValueTask.FromResult<IAgentConnection>(PlaybackConnection);
            }
            return ValueTask.FromResult<IAgentConnection>(new ScriptedConnection(channel, heartbeatTimeout: false));
        }
    }

    private sealed class NoAckPlaybackConnection : IAgentConnection
    {
        private int _disposed;
        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AgentChannel Channel => AgentChannel.PlaybackPcm;
        public bool IsConnected => Volatile.Read(ref _disposed) == 0;
        public ValueTask SendAsync(AgentFrame frame, CancellationToken cancellationToken)
        {
            Sent.TrySetResult();
            return ValueTask.CompletedTask;
        }
        public ValueTask<AgentFrame> ReceiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<AgentFrame>(new InvalidOperationException("Playback PCM must not wait for a response."));
        public void Disconnect() => Interlocked.Exchange(ref _disposed, 1);
        public ValueTask DisposeAsync() { Interlocked.Exchange(ref _disposed, 1); return ValueTask.CompletedTask; }
    }
}
