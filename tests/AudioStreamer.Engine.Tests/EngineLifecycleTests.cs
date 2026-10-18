using System.Net;
using AudioStreamer.Engine;
using AudioStreamer.Engine.Configuration;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Engine.Managers;
using AudioStreamer.Engine.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AudioStreamer.Engine.Tests;

public sealed class EngineLifecycleTests
{
    [Fact]
    public async Task Start_IsIdempotent_AndLoadsStableNodeIdentity()
    {
        var store = new MemoryConfigurationStore();
        await using var engine = new Engine(store, NullLogger<Engine>.Instance);

        var first = await engine.StartAsync(CancellationToken.None);
        var second = await engine.StartAsync(CancellationToken.None);
        var snapshot = await engine.GetSnapshotAsync(CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.Equal(EngineLifecycleState.Running, first.Value!.State);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value.State, second.Value!.State);
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(store.Settings!.NodeId, snapshot.Value!.NodeId);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task Stop_IsIdempotent_AndEngineCanStartAgain()
    {
        var store = new MemoryConfigurationStore();
        await using var engine = new Engine(store, NullLogger<Engine>.Instance);

        Assert.True((await engine.StartAsync(CancellationToken.None)).IsSuccess);
        var stopped = await engine.StopAsync(CancellationToken.None);
        var stoppedAgain = await engine.StopAsync(CancellationToken.None);
        var restarted = await engine.StartAsync(CancellationToken.None);
        var snapshot = await engine.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(EngineLifecycleState.Stopped, stopped.Value!.State);
        Assert.Equal(EngineLifecycleState.Stopped, stoppedAgain.Value!.State);
        Assert.Equal(EngineLifecycleState.Running, restarted.Value!.State);
        Assert.Equal(store.Settings!.NodeId, snapshot.Value!.NodeId);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task Start_HonorsCancellationBeforeAcquiringLifecycle()
    {
        await using var engine = new Engine(new MemoryConfigurationStore(), NullLogger<Engine>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.StartAsync(cancellation.Token));
        var status = await engine.GetStatusAsync(CancellationToken.None);
        Assert.Equal(EngineLifecycleState.Stopped, status.Value!.State);
    }

    [Fact]
    public async Task Stop_CompletesCleanupEvenWhenHostWaitTokenIsCanceled()
    {
        await using var engine = new Engine(new MemoryConfigurationStore(), NullLogger<Engine>.Instance);
        await engine.StartAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var stopped = await engine.StopAsync(cancellation.Token);

        Assert.True(stopped.IsSuccess);
        Assert.Equal(EngineLifecycleState.Stopped, stopped.Value!.State);
    }

    [Fact]
    public async Task UpdateSettings_UsesRevisionCheckAndPersistsBeforePublishing()
    {
        var store = new MemoryConfigurationStore();
        await using var engine = new Engine(store, NullLogger<Engine>.Instance);
        await engine.StartAsync(CancellationToken.None);

        var update = await engine.UpdateSettingsAsync(new SettingsPatch(1, "Living Room"), CancellationToken.None);
        var stale = await engine.UpdateSettingsAsync(new SettingsPatch(1, "Stale"), CancellationToken.None);

        Assert.True(update.IsSuccess);
        Assert.Equal("Living Room", store.Settings!.NodeName);
        Assert.Equal(2, update.Value!.Revision);
        Assert.Equal(EngineErrorCode.ConfigurationConflict, stale.Error!.Code);
    }

    [Fact]
    public async Task Dispose_ReportsFailedSessionCleanupAfterReleasingEngineInfrastructure()
    {
        var logger = NullLogger<Engine>.Instance;
        var runtime = new FailingStopRuntime();
        var components = EngineComposition.Create(new MemoryConfigurationStore(), logger, new EngineOptions(),
            new UnavailableAudioDeviceCatalog(), TimeProvider.System, runtime);
        var engine = new Engine(components, logger);
        var started = await engine.StartAsync(CancellationToken.None);
        var nodeId = (await engine.GetSnapshotAsync(CancellationToken.None)).Value!.NodeId;
        var request = new StreamRequest(nodeId, Guid.NewGuid());

        Assert.True(started.IsSuccess);
        Assert.True((await components.SessionManager.StartAsync(request, nodeId, null, started.Value!,
            CancellationToken.None)).IsSuccess);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.DisposeAsync().AsTask());

        Assert.Contains(nameof(EngineErrorCode.ShutdownTimedOut), exception.Message);
        Assert.True(runtime.StopCalled);
    }

    private sealed class MemoryConfigurationStore : IConfigurationStore
    {
        public EngineSettings? Settings { get; private set; }
        public int SaveCount { get; private set; }

        public ValueTask<EngineSettings?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Settings);
        }

        public ValueTask SaveAsync(EngineSettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Settings = settings;
            SaveCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingStopRuntime : IAudioSessionRuntime
    {
        public bool StopCalled { get; private set; }

        public Task<EngineResult<bool>> StartAsync(StreamRequest request, Guid localNodeId, IPEndPoint? audioEndpoint,
            CancellationToken cancellationToken) => Task.FromResult(EngineResult<bool>.Success(true));

        public Task<EngineResult<bool>> StopAsync(StreamRequest request, CancellationToken cancellationToken)
        {
            StopCalled = true;
            return Task.FromResult(EngineResult<bool>.Failure(EngineErrorCode.TransportUnavailable,
                "audio-transport", "Runtime refused to stop."));
        }

        public Task<EngineResult<bool>> UpdateEndpointAsync(StreamRequest request, IPEndPoint audioEndpoint,
            CancellationToken cancellationToken) => Task.FromResult(EngineResult<bool>.Success(true));

        public Task<EngineResult<bool>> PauseAsync(StreamRequest request, bool isSender,
            CancellationToken cancellationToken) => Task.FromResult(EngineResult<bool>.Success(true));

        public Task<EngineResult<bool>> ResumeAsync(StreamRequest request, bool isSender,
            CancellationToken cancellationToken) => Task.FromResult(EngineResult<bool>.Success(true));
    }
}
