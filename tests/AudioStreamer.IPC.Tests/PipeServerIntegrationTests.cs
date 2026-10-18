using System.IO.Pipes;
using System.Collections.Concurrent;
using AudioStreamer.IPC.Contracts;
using AudioStreamer.IPC.Dispatch;
using AudioStreamer.IPC.Protocol;
using AudioStreamer.IPC.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AudioStreamer.IPC.Tests;

[CollectionDefinition("AudioStreamer named pipe tests", DisableParallelization = true)]
public sealed class IpcPipeCollection
{
    public const string Name = "AudioStreamer named pipe tests";
}

[Collection(IpcPipeCollection.Name)]
public sealed class PipeServerIntegrationTests
{
    [Fact]
    public async Task MultipleClientsCanHandshakeAndIssueConcurrentRequests()
    {
        await using var server = await TestServer.StartAsync();
        await using var first = await TestServer.ConnectClientAsync(server.PipeName);
        await using var second = await TestServer.ConnectClientAsync(server.PipeName);

        var tasks = Enumerable.Range(0, 20).Select(async index =>
        {
            var client = index % 2 == 0 ? first : second;
            var response = await client.RequestAsync("Ping", new PingPayload(), CancellationToken.None);
            Assert.Equal("PingResult", response.MessageType);
            Assert.Equal(client.ConnectionEpoch, response.ConnectionEpoch);
        });

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, server.Connections.Count);
    }

    [Fact]
    public async Task SubscribeReceivesEventsAndUnsubscribeStopsEventPublishing()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await TestServer.ConnectClientAsync(server.PipeName);
        var subscribed = await client.RequestAsync("Subscribe", new SubscribeRequest(), CancellationToken.None);
        Assert.Equal("SubscribeResult", subscribed.MessageType);

        await using var events = client.ReadEventsAsync(CancellationToken.None).GetAsyncEnumerator();
        await server.Events.PublishAsync("NodeStateChanged", new IpcEventPayload("Running", 1, null));
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("NodeStateChanged", events.Current.MessageType);

        await client.RequestAsync("Unsubscribe", new EmptyPayload(), CancellationToken.None);
        var subscription = Assert.Single(server.Connections.Sessions).Subscription;
        Assert.False(subscription.IsSubscribed);
        await server.Events.PublishAsync("NodeStateChanged", new IpcEventPayload("Idle", 2, null));
        Assert.False(subscription.Accepts("NodeStateChanged"));
        Assert.Equal("PingResult", (await client.RequestAsync("Ping", new PingPayload(), CancellationToken.None)).MessageType);
    }

    [Fact]
    public async Task DisconnectAndReconnectCreatesANewConnectionEpoch()
    {
        await using var server = await TestServer.StartAsync();
        Guid firstEpoch;
        await using (var first = await TestServer.ConnectClientAsync(server.PipeName))
        {
            firstEpoch = first.ConnectionEpoch;
            await first.RequestAsync("Ping", new PingPayload(), CancellationToken.None);
            Assert.Equal("GetDiagnosticsResult",
                (await first.RequestAsync("GetDiagnostics", new EmptyPayload(), CancellationToken.None)).MessageType);
        }

        await using var second = await TestServer.ConnectClientAsync(server.PipeName);
        Assert.NotEqual(firstEpoch, second.ConnectionEpoch);
        Assert.Equal("GetDiagnosticsResult",
            (await second.RequestAsync("GetDiagnostics", new EmptyPayload(), CancellationToken.None)).MessageType);
        Assert.Equal("PingResult", (await second.RequestAsync("Ping", new PingPayload(), CancellationToken.None)).MessageType);
    }

    [Fact]
    public async Task RepeatedDiagnosticsRequestsDoNotCloseIpcConnection()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await TestServer.ConnectClientAsync(server.PipeName);

        for (var request = 0; request < 25; request++)
        {
            var response = await client.RequestAsync("GetDiagnostics", new EmptyPayload(), CancellationToken.None);
            Assert.Equal("GetDiagnosticsResult", response.MessageType);
            Assert.Equal(client.ConnectionEpoch, response.ConnectionEpoch);
        }

        Assert.Equal("PingResult", (await client.RequestAsync("Ping", new PingPayload(), CancellationToken.None)).MessageType);
        Assert.Equal(1, server.Connections.Count);
    }

    [Fact]
    public async Task StructuredStartStreamFailureDoesNotDisconnectClient()
    {
        var handler = new FakeHandler { FailStartStream = true };
        await using var server = await TestServer.StartAsync(handler: handler);
        await using var client = await TestServer.ConnectClientAsync(server.PipeName);

        var response = await client.RequestAsync("StartStream", new EmptyPayload(), CancellationToken.None);
        var failure = IpcSerializer.DeserializePayload<IpcOperationResult<System.Text.Json.JsonElement>>(response.Payload);

        Assert.Equal("error", failure.Result);
        Assert.Equal(IpcErrors.StreamStartFailed, failure.Error!.Code);
        Assert.Equal("PingResult", (await client.RequestAsync("Ping", new PingPayload(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5))).MessageType);
        Assert.Equal(1, server.Connections.Count);
    }

    [Fact]
    public async Task ServiceRestartAndRapidReconnectsKeepDiagnosticsAvailable()
    {
        for (var restart = 0; restart < 5; restart++)
        {
            await using var server = await TestServer.StartAsync();
            await using var client = await TestServer.ConnectClientAsync(server.PipeName);

            var diagnostics = await client.RequestAsync("GetDiagnostics", new EmptyPayload(), CancellationToken.None);
            Assert.Equal("GetDiagnosticsResult", diagnostics.MessageType);
            Assert.Equal(client.ConnectionEpoch, diagnostics.ConnectionEpoch);
            Assert.Equal("PingResult", (await client.RequestAsync("Ping", new PingPayload(), CancellationToken.None)).MessageType);
        }
    }

    [Fact]
    public async Task ServerReturnsTimeoutForSlowHandlerAndContinuesServing()
    {
        await using var server = await TestServer.StartAsync(new IpcServerOptions { ReadRequestTimeout = TimeSpan.FromMilliseconds(100) });
        await using var client = await TestServer.ConnectClientAsync(server.PipeName);

        var slow = await client.RequestAsync("Slow", new EmptyPayload(), CancellationToken.None);
        var failure = IpcSerializer.DeserializePayload<ErrorResponse>(slow.Payload);

        Assert.Equal("error", failure.Result);
        Assert.Equal(IpcErrors.OperationTimedOut, failure.Error.Code);
        Assert.Equal("PingResult", (await client.RequestAsync("Ping", new PingPayload(), CancellationToken.None)).MessageType);
    }

    [Fact]
    public async Task ClientCancellationDoesNotDeadlockTheConnection()
    {
        await using var server = await TestServer.StartAsync(new IpcServerOptions { ReadRequestTimeout = TimeSpan.FromSeconds(2) });
        await using var client = await TestServer.ConnectClientAsync(server.PipeName);
        using var cancellation = new CancellationTokenSource();
        var slowRequest = client.RequestAsync("Slow", new EmptyPayload(), cancellation.Token);
        await server.Handler.SlowRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slowRequest);
        Assert.Equal("PingResult", (await client.RequestAsync("Ping", new PingPayload(), CancellationToken.None)).MessageType);
    }

    [Fact]
    public async Task MalformedClientIsClosedWithoutAffectingOtherConnections()
    {
        await using var server = await TestServer.StartAsync();
        await using (var malformed = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await malformed.ConnectAsync(2000, timeout.Token);
            var body = "{broken"u8.ToArray();
            var frame = new byte[sizeof(uint) + body.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
            body.CopyTo(frame, sizeof(uint));

            var disconnectObserved = false;
            try
            {
                await malformed.WriteAsync(frame, timeout.Token);
                await malformed.FlushAsync(timeout.Token);
                var response = new byte[1];
                Assert.Equal(0, await malformed.ReadAsync(response, timeout.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
                disconnectObserved = true;
            }
            catch (IOException)
            {
                // The server may close the pipe while the client write or flush is completing.
                disconnectObserved = true;
            }

            Assert.True(disconnectObserved, "The malformed client was not disconnected.");
        }

        await using var healthy = await TestServer.ConnectClientAsync(server.PipeName);
        Assert.Equal("PingResult", (await healthy.RequestAsync("Ping", new PingPayload(), CancellationToken.None)).MessageType);
    }

    [Fact]
    public async Task RejectsIncompatibleProtocolVersion()
    {
        var provider = new FakeHandler();
        var handshake = new VersionHandshake(provider);
        var request = IpcEnvelopeFactory.Request("Hello", Guid.NewGuid(), Guid.Empty, new HelloRequest
        {
            SupportedVersions = [new IpcSchemaVersion(2, 0)],
            ClientVersion = "test",
            ProcessId = Environment.ProcessId,
            RequestedRole = "interactive"
        });

        var result = await handshake.NegotiateAsync(request, new IpcClientIdentity("S-1-test", "test", false), CancellationToken.None);
        var payload = IpcSerializer.DeserializePayload<ErrorResponse>(result.Response.Payload);

        Assert.False(result.Accepted);
        Assert.Equal(IpcErrors.IpcVersionUnsupported, payload.Error.Code);
    }

    [Fact]
    public async Task HandshakeDeadlineClosesClientsThatNeverSendHello()
    {
        await using var server = await TestServer.StartAsync(new IpcServerOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(100) });
        await using (var idleClient = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await idleClient.ConnectAsync(2000, timeout.Token);
            var read = new byte[1];
            var close = idleClient.ReadAsync(read, timeout.Token).AsTask();
            Assert.Equal(0, await close.WaitAsync(TimeSpan.FromSeconds(3)));
        }

        await using var healthy = await TestServer.ConnectClientAsync(server.PipeName);
        Assert.Equal("PingResult", (await healthy.RequestAsync("Ping", new PingPayload(), CancellationToken.None)).MessageType);
    }

    [Fact]
    public async Task MutationsFromOneClientExecuteInSubmissionOrder()
    {
        var handler = new FakeHandler();
        await using var server = await TestServer.StartAsync(handler: handler);
        await using var client = await TestServer.ConnectClientAsync(server.PipeName);

        var start = client.RequestAsync("StartStream", new EmptyPayload(), CancellationToken.None);
        var stop = client.RequestAsync("StopStream", new EmptyPayload(), CancellationToken.None);
        var readBarrier = client.RequestAsync("ReadProbe", new EmptyPayload(), CancellationToken.None);
        await Task.WhenAll(start, stop, readBarrier).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "StartStream", "StopStream" }, handler.MutationOrder.ToArray());
    }

    [Fact]
    public async Task SettingsMutationsRemainOrderedWhenEarlierMutationIsDelayed()
    {
        var handler = new FakeHandler { DelayFirstSettingsMutation = true };
        await using var server = await TestServer.StartAsync(handler: handler);
        await using var client = await TestServer.ConnectClientAsync(server.PipeName);

        var first = client.RequestAsync("UpdateSettings", new { expectedRevision = 1 }, CancellationToken.None);
        await handler.FirstSettingsMutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = client.RequestAsync("UpdateSettings", new { expectedRevision = 2 }, CancellationToken.None);
        var read = client.RequestAsync("ReadProbe", new EmptyPayload(), CancellationToken.None);

        await read.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new long[] { 1 }, handler.SettingsRevisions.ToArray());
        Assert.False(second.IsCompleted);

        handler.ReleaseFirstSettingsMutation.TrySetResult(true);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new long[] { 1, 2 }, handler.SettingsRevisions.ToArray());
    }

    [Fact]
    public async Task ReadRequestsProceedWhileMutationIsDelayedAndLaterMutationWaits()
    {
        var handler = new FakeHandler { DelayFirstSettingsMutation = true };
        await using var server = await TestServer.StartAsync(handler: handler);
        await using var client = await TestServer.ConnectClientAsync(server.PipeName);

        var first = client.RequestAsync("UpdateSettings", new { expectedRevision = 10 }, CancellationToken.None);
        await handler.FirstSettingsMutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = client.RequestAsync("UpdateSettings", new { expectedRevision = 11 }, CancellationToken.None);
        var read = client.RequestAsync("ReadProbe", new EmptyPayload(), CancellationToken.None);

        Assert.Equal("ReadProbeResult", (await read.WaitAsync(TimeSpan.FromSeconds(3))).MessageType);
        Assert.Equal(1, handler.ReadProbeCount);
        Assert.Equal(new long[] { 10 }, handler.SettingsRevisions.ToArray());

        handler.ReleaseFirstSettingsMutation.TrySetResult(true);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new long[] { 10, 11 }, handler.SettingsRevisions.ToArray());
    }

    private sealed class TestServer : IAsyncDisposable
    {
        private readonly PipeServer _server;
        private TestServer(PipeServer server, ConnectionManager connections, EventPublisher events, FakeHandler handler,
            string pipeName)
        { _server = server; Connections = connections; Events = events; Handler = handler; PipeName = pipeName; }

        public ConnectionManager Connections { get; }
        public EventPublisher Events { get; }
        public FakeHandler Handler { get; }
        public string PipeName { get; }

        public static async Task<TestServer> StartAsync(IpcServerOptions? options = null, FakeHandler? handler = null)
        {
            var pipeName = "AudioStreamer.Test." + Guid.NewGuid().ToString("N");
            options = (options ?? new IpcServerOptions()) with { PipeName = pipeName };
            var connections = new ConnectionManager();
            var events = new EventPublisher(connections, NullLogger<EventPublisher>.Instance);
            var state = new IpcServerState();
            handler ??= new FakeHandler();
            var dispatcher = new RequestDispatcher(handler, handler, options, state, NullLogger<RequestDispatcher>.Instance);
            var server = new PipeServer(dispatcher, new VersionHandshake(handler), events, connections, state,
                options, NullLoggerFactory.Instance, NullLogger<PipeServer>.Instance);
            var testServer = new TestServer(server, connections, events, handler, pipeName);
            await server.StartAsync(CancellationToken.None);
            return testServer;
        }

        public static async Task<PipeClient> ConnectClientAsync(string pipeName)
        {
            var client = new PipeClient(NullLogger<PipeClient>.Instance, new IpcClientOptions { PipeName = pipeName });
            await client.ConnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            return client;
        }

        public async ValueTask DisposeAsync() => await _server.DisposeAsync();
    }

    private sealed class FakeHandler : IIpcRequestHandler, IIpcHandshakeInfoProvider
    {
        private int _readProbeCount;
        private int _settingsMutationCount;
        public ConcurrentQueue<string> MutationOrder { get; } = new();
        public ConcurrentQueue<long> SettingsRevisions { get; } = new();
        public TaskCompletionSource<bool> FirstSettingsMutationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirstSettingsMutation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SlowRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DelayFirstSettingsMutation { get; init; }
        public bool FailStartStream { get; init; }
        public int ReadProbeCount => Volatile.Read(ref _readProbeCount);

        public ValueTask<IpcServerInfo> GetServerInfoAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new IpcServerInfo("test-service", "test-engine", "test-minimum", ["Ping"], "Running", "Idle"));

        public async ValueTask<IpcHandlerResponse> HandleAsync(string messageType, System.Text.Json.JsonElement payload,
            IpcClientIdentity identity, CancellationToken cancellationToken)
        {
            if (messageType == "Slow")
            {
                SlowRequestStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (messageType is "StartStream" or "StopStream") MutationOrder.Enqueue(messageType);
            if (messageType == "UpdateSettings")
            {
                MutationOrder.Enqueue(messageType);
                SettingsRevisions.Enqueue(payload.GetProperty("expectedRevision").GetInt64());
                if (DelayFirstSettingsMutation && Interlocked.Increment(ref _settingsMutationCount) == 1)
                {
                    FirstSettingsMutationStarted.TrySetResult(true);
                    await ReleaseFirstSettingsMutation.Task.WaitAsync(cancellationToken);
                }
            }
            if (messageType == "ReadProbe") Interlocked.Increment(ref _readProbeCount);
            if (messageType == "StartStream" && FailStartStream)
                return new IpcHandlerResponse("StartStreamResult", new IpcOperationResult<object>
                {
                    Result = "error",
                    Error = new IpcError { Code = IpcErrors.StreamStartFailed, Message = "Stream could not be started." }
                }, IpcErrors.StreamStartFailed);
            return new IpcHandlerResponse(messageType + "Result", new IpcOperationResult<string> { Result = "ok", Data = messageType });
        }
    }
}
