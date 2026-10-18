using System.Net;
using System.Threading.Channels;
using AudioStreamer.Control.Protocol;
using AudioStreamer.Engine;
using AudioStreamer.Engine.Configuration;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Engine.Managers;
using AudioStreamer.Engine.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AudioStreamer.Engine.Tests;

public sealed class ControlCoordinatorTests
{
    private static readonly byte[] ControllerSecret = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
    [Fact]
    public async Task StartStreamingRejectsRequestsBeforeEngineStartup()
    {
        var controlTransport = new FakeControlTransport();
        var controlOptions = new ControlPlaneOptions(new ControlTransportOptions("0.0.0.0", 55050, 64), 5006,
            TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2), "test");
        var logger = NullLogger<Engine>.Instance;
        var components = EngineComposition.Create(new MemoryConfigurationStore(), logger,
            new EngineOptions(), new UnavailableAudioDeviceCatalog(), TimeProvider.System,
            new FakeAudioSessionRuntime(), controlTransport, controlOptions, NullLoggerFactory.Instance);
        await using var engine = new Engine(components, logger);

        var result = await engine.StartStreamingAsync(new StreamRequest(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(EngineErrorCode.InvalidState, result.Error!.Code);
        await controlTransport.DisposeAsync();
    }

    [Fact]
    public async Task Start_AdvertisesControlV1HelloThroughTransport()
    {
        await using var fixture = await Fixture.StartAsync();

        var helloBytes = await fixture.Transport.WaitForSendAsync(bytes =>
            ControlProtocolV1Codec.Parse(bytes) is HelloMessage);

        var hello = Assert.IsType<HelloMessage>(ControlProtocolV1Codec.Parse(helloBytes));
        Assert.Equal(ControlNodeIdentity.ToProtocolId(fixture.LocalNodeId), hello.NodeId);
        Assert.Equal((ushort)5006, hello.ReceivePort);
    }

    [Fact]
    public async Task HeartbeatLoop_SchedulesControlV1Heartbeat()
    {
        await using var fixture = await Fixture.StartAsync();

        var heartbeatBytes = await fixture.Transport.WaitForSendAsync(bytes =>
            ControlProtocolV1Codec.Parse(bytes) is HeartbeatMessage, TimeSpan.FromSeconds(3));

        Assert.Equal(ControlNodeStatus.Online,
            Assert.IsType<HeartbeatMessage>(ControlProtocolV1Codec.Parse(heartbeatBytes)).Status);
    }

    [Fact]
    public async Task HeartbeatLoop_ExpiresPeersAndRemovesThemFromEngineRegistry()
    {
        var clock = new ManualMonotonicTimeProvider();
        await using var fixture = await Fixture.StartAsync(peerTimeout: TimeSpan.FromMilliseconds(600), clock);
        var peerAdded = WaitForEventAsync(fixture.Engine, "PeerAdded");
        var peerRemoved = WaitForEventAsync(fixture.Engine, "PeerRemoved");
        await fixture.Transport.InjectAsync(new HelloMessage(0x1234, "Peer", "test",
            ControlCapability.Sender | ControlCapability.Receiver, 5006), "127.0.0.2");
        await peerAdded;
        clock.Advance(TimeSpan.FromMilliseconds(601));
        await peerRemoved;

        Assert.Empty((await fixture.Engine.GetControlPeersAsync(CancellationToken.None)).Value!);
        var statistics = await fixture.Engine.GetControlStatisticsAsync(CancellationToken.None);
        Assert.True(statistics.IsSuccess);
        Assert.True(statistics.Value!.PeerTimeouts > 0);
    }

    [Fact]
    public async Task StartStreaming_RoutesControlCommandAndWaitsForCorrelatedPeerStatus()
    {
        await using var fixture = await Fixture.StartAsync();
        const ulong remoteId = 0x1234;
        var remoteTask = WaitForPeerAsync(fixture.Engine, remoteId);
        await fixture.Transport.InjectAsync(new HelloMessage(remoteId, "Receiver", "test",
            ControlCapability.Receiver | ControlCapability.Playback, 5006), "127.0.0.2");
        var remote = await remoteTask;

        var startTask = fixture.Engine.StartStreamingAsync(
            new StreamRequest(fixture.LocalNodeId, remote.EngineNodeId), CancellationToken.None);
        var commandBytes = await fixture.Transport.WaitForSendAsync(bytes =>
            ControlProtocolV1Codec.Parse(bytes) is StreamCommandMessage { IsStart: true }, TimeSpan.FromSeconds(3));
        var command = Assert.IsType<StreamCommandMessage>(ControlProtocolV1Codec.Parse(commandBytes));
        await fixture.Transport.InjectAsync(new StreamStatusMessage(command.SenderNodeId, command.ReceiverNodeId,
            ControlStreamState.Running, TransactionId: command.TransactionId), "127.0.0.2");

        var result = await startTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1, fixture.AudioRuntime.StartCount);
        Assert.Equal(1, (await fixture.Engine.GetControlStatisticsAsync(CancellationToken.None)).Value!.CurrentSessions);
    }

    [Fact]
    public async Task AuthenticatedControllerCommand_IsAcceptedFromHiddenController()
    {
        await using var fixture = await Fixture.StartAsync(controllerSecret: Convert.ToHexString(ControllerSecret));
        const ulong remoteId = 0x1234;
        var remoteTask = WaitForPeerAsync(fixture.Engine, remoteId);
        await fixture.Transport.InjectAsync(new HelloMessage(remoteId, "Sender", "android",
            ControlCapability.Sender, 5006), "127.0.0.2");
        _ = await remoteTask;
        var localProtocolId = ControlNodeIdentity.ToProtocolId(fixture.LocalNodeId);
        var nonce = Enumerable.Range(1, AuthenticatedControllerCommandCodec.NonceLength).Select(static value => (byte)value).ToArray();
        var command = new AuthenticatedControllerCommandMessage(true, 0x9876, remoteId, localProtocolId, 55, nonce);
        var bytes = AuthenticatedControllerCommandCodec.Serialize(command, ControllerSecret);

        await fixture.Transport.InjectRawAsync(bytes, "127.0.0.3");
        _ = await fixture.Transport.WaitForSendAsync(payload =>
            ControlProtocolV1Codec.Parse(payload) is StreamStatusMessage { State: ControlStreamState.Running, TransactionId: 55 });
        Assert.Equal(1, fixture.AudioRuntime.StartCount);
    }

    [Fact]
    public async Task LegacyNodeCommandStillRequiresKnownPeerAddress()
    {
        await using var fixture = await Fixture.StartAsync();
        const ulong remoteId = 0x1234;
        var remoteTask = WaitForPeerAsync(fixture.Engine, remoteId);
        await fixture.Transport.InjectAsync(new HelloMessage(remoteId, "Sender", "android",
            ControlCapability.Sender, 5006), "127.0.0.2");
        _ = await remoteTask;

        await fixture.Transport.InjectAsync(new StreamCommandMessage(true, remoteId,
            ControlNodeIdentity.ToProtocolId(fixture.LocalNodeId), 1), "127.0.0.3");
        _ = await fixture.Transport.WaitForSendAsync(payload =>
            ControlProtocolV1Codec.Parse(payload) is StreamStatusMessage { State: ControlStreamState.Failed, ErrorCode: 4 });

        Assert.Equal(0, fixture.AudioRuntime.StartCount);
    }

    [Fact]
    public async Task Stop_QuiescesCoordinatorAndStopsTransportDuringEngineShutdown()
    {
        await using var fixture = await Fixture.StartAsync();

        var result = await fixture.Engine.StopAsync(CancellationToken.None);
        var goodbye = await fixture.Transport.WaitForSendAsync(bytes =>
            ControlProtocolV1Codec.Parse(bytes) is GoodbyeMessage, TimeSpan.FromSeconds(2));

        Assert.True(result.IsSuccess);
        Assert.IsType<GoodbyeMessage>(ControlProtocolV1Codec.Parse(goodbye));
        Assert.Equal(1, fixture.Transport.StopCount);
    }

    [Fact]
    public async Task StopAndRestart_DoesNotAdvertiseStaleStreamingRoute()
    {
        await using var fixture = await Fixture.StartAsync();
        const ulong remoteId = 0x1234;
        var remoteTask = WaitForPeerAsync(fixture.Engine, remoteId);
        await fixture.Transport.InjectAsync(new HelloMessage(remoteId, "Receiver", "test",
            ControlCapability.Receiver | ControlCapability.Playback, 5006), "127.0.0.2");
        var remote = await remoteTask;

        var startTask = fixture.Engine.StartStreamingAsync(
            new StreamRequest(fixture.LocalNodeId, remote.EngineNodeId), CancellationToken.None);
        var commandBytes = await fixture.Transport.WaitForSendAsync(bytes =>
            ControlProtocolV1Codec.Parse(bytes) is StreamCommandMessage { IsStart: true });
        var command = Assert.IsType<StreamCommandMessage>(ControlProtocolV1Codec.Parse(commandBytes));
        await fixture.Transport.InjectAsync(new StreamStatusMessage(command.SenderNodeId, command.ReceiverNodeId,
            ControlStreamState.Running, TransactionId: command.TransactionId), "127.0.0.2");
        Assert.True((await startTask.WaitAsync(TimeSpan.FromSeconds(3))).IsSuccess);

        Assert.True((await fixture.Engine.StopAsync(CancellationToken.None)).IsSuccess);
        _ = await fixture.Transport.WaitForSendAsync(bytes => ControlProtocolV1Codec.Parse(bytes) is GoodbyeMessage);
        fixture.Transport.DrainSends();

        Assert.True((await fixture.Engine.StartAsync(CancellationToken.None)).IsSuccess);
        var heartbeat = await fixture.Transport.WaitForSendAsync(bytes =>
            ControlProtocolV1Codec.Parse(bytes) is HeartbeatMessage);

        Assert.Equal(ControlNodeStatus.Online,
            Assert.IsType<HeartbeatMessage>(ControlProtocolV1Codec.Parse(heartbeat)).Status);
    }

    private static async Task<ControlPeerSnapshot> WaitForPeerAsync(Engine engine, ulong protocolId)
    {
        await WaitForEventAsync(engine, "PeerAdded");
        var result = await engine.GetControlPeerAsync(protocolId, CancellationToken.None);
        Assert.True(result.IsSuccess);
        return Assert.IsType<ControlPeerSnapshot>(result.Value);
    }

    private static async Task WaitForEventAsync(Engine engine, string expectedKind)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var engineEvent in engine.SubscribeAsync(new EventSubscription(), timeout.Token).ConfigureAwait(false))
            if (engineEvent.Kind == expectedKind) return;
        throw new InvalidOperationException($"Engine event '{expectedKind}' was not published.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(Engine engine, FakeControlTransport transport, FakeAudioSessionRuntime audioRuntime, Guid localNodeId)
        {
            Engine = engine;
            Transport = transport;
            AudioRuntime = audioRuntime;
            LocalNodeId = localNodeId;
        }

        public Engine Engine { get; }
        public FakeControlTransport Transport { get; }
        public FakeAudioSessionRuntime AudioRuntime { get; }
        public Guid LocalNodeId { get; }

        public static async Task<Fixture> StartAsync(TimeSpan? peerTimeout = null, TimeProvider? clock = null,
            string? controllerSecret = null)
        {
            var controlTransport = new FakeControlTransport();
            var audioRuntime = new FakeAudioSessionRuntime();
            var heartbeat = TimeSpan.FromMilliseconds(250);
            var expiration = peerTimeout ?? TimeSpan.FromSeconds(3);
            var controlOptions = new ControlPlaneOptions(new ControlTransportOptions("0.0.0.0", 55050, 64), 5006,
                heartbeat, TimeSpan.FromSeconds(2), "test", ControllerSecretHex: controllerSecret);
            var logger = NullLogger<Engine>.Instance;
            var components = EngineComposition.Create(new MemoryConfigurationStore(), logger,
                new EngineOptions(PeerExpiration: expiration), new UnavailableAudioDeviceCatalog(), clock ?? TimeProvider.System,
                audioRuntime, controlTransport, controlOptions, NullLoggerFactory.Instance);
            var engine = new Engine(components, logger);
            var started = await engine.StartAsync(CancellationToken.None);
            Assert.True(started.IsSuccess, started.Error?.Message);
            var snapshot = await engine.GetSnapshotAsync(CancellationToken.None);
            Assert.True(snapshot.IsSuccess);
            return new Fixture(engine, controlTransport, audioRuntime, snapshot.Value!.NodeId);
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            await Transport.DisposeAsync();
        }
    }

    private sealed class ManualMonotonicTimeProvider : TimeProvider
    {
        private long _offset;
        public override long TimestampFrequency => global::System.Diagnostics.Stopwatch.Frequency;
        public override long GetTimestamp() => global::System.Diagnostics.Stopwatch.GetTimestamp() + Interlocked.Read(ref _offset);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _offset,
            (long)(duration.TotalSeconds * TimestampFrequency));
    }

    private sealed class FakeControlTransport : IControlTransport
    {
        private readonly Channel<ControlTransportEvent> _events = Channel.CreateBounded<ControlTransportEvent>(1024);
        private readonly Channel<byte[]> _sent = Channel.CreateUnbounded<byte[]>();
        private int _running;
        private int _stopCount;

        public int StopCount => Volatile.Read(ref _stopCount);
        public IAsyncEnumerable<ControlTransportEvent> ReadEventsAsync(CancellationToken cancellationToken) =>
            _events.Reader.ReadAllAsync(cancellationToken);
        public Task StartAsync(ControlTransportOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref _running, 1);
            return Task.CompletedTask;
        }
        public async Task QueueAsync(ReadOnlyMemory<byte> payload, ControlTransportEndpoint destination,
            CancellationToken cancellationToken)
        {
            _ = await SendAsync(payload, destination, cancellationToken);
        }
        public async Task<int> SendAsync(ReadOnlyMemory<byte> payload, ControlTransportEndpoint destination,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = payload.ToArray();
            await _sent.Writer.WriteAsync(bytes, cancellationToken);
            await _events.Writer.WriteAsync(new ControlDatagramSent(bytes.Length), cancellationToken);
            return bytes.Length;
        }
        public bool TrySend(ReadOnlyMemory<byte> payload, ControlTransportEndpoint destination)
        {
            var bytes = payload.ToArray();
            return _sent.Writer.TryWrite(bytes) && _events.Writer.TryWrite(new ControlDatagramSent(bytes.Length));
        }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _running, 0) != 0) Interlocked.Increment(ref _stopCount);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            _sent.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
        public Task InjectAsync(ControlMessage message, string sourceAddress) => _events.Writer.WriteAsync(
            new ControlDatagramReceived(ControlProtocolV1Codec.Serialize(message),
                new ControlTransportEndpoint(sourceAddress, 55050))).AsTask();
        public Task InjectRawAsync(byte[] bytes, string sourceAddress) => _events.Writer.WriteAsync(
            new ControlDatagramReceived(bytes, new ControlTransportEndpoint(sourceAddress, 55050))).AsTask();
        public async Task<byte[]> WaitForSendAsync(Func<byte[], bool> predicate, TimeSpan? timeout = null)
        {
            using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
            try
            {
                while (await _sent.Reader.WaitToReadAsync(cancellation.Token))
                {
                    while (_sent.Reader.TryRead(out var bytes))
                        if (predicate(bytes)) return bytes;
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            throw new TimeoutException("Expected Control datagram was not sent.");
        }

        public void DrainSends()
        {
            while (_sent.Reader.TryRead(out _)) { }
        }
    }

    private sealed class FakeAudioSessionRuntime : IAudioSessionRuntime
    {
        public int StartCount { get; private set; }
        public Task<EngineResult<bool>> StartAsync(StreamRequest request, Guid localNodeId, IPEndPoint? audioEndpoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            return Task.FromResult(EngineResult<bool>.Success(true));
        }
        public Task<EngineResult<bool>> StopAsync(StreamRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(EngineResult<bool>.Success(true));
        public Task<EngineResult<bool>> UpdateEndpointAsync(StreamRequest request, IPEndPoint audioEndpoint,
            CancellationToken cancellationToken) => Task.FromResult(EngineResult<bool>.Success(true));
        public Task<EngineResult<bool>> PauseAsync(StreamRequest request, bool isSender, CancellationToken cancellationToken) =>
            Task.FromResult(EngineResult<bool>.Success(true));
        public Task<EngineResult<bool>> ResumeAsync(StreamRequest request, bool isSender, CancellationToken cancellationToken) =>
            Task.FromResult(EngineResult<bool>.Success(true));
    }

    private sealed class MemoryConfigurationStore : IConfigurationStore
    {
        public EngineSettings? Settings { get; private set; }
        public ValueTask<EngineSettings?> LoadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Settings);
        public ValueTask SaveAsync(EngineSettings settings, CancellationToken cancellationToken)
        {
            Settings = settings;
            return ValueTask.CompletedTask;
        }
    }
}
