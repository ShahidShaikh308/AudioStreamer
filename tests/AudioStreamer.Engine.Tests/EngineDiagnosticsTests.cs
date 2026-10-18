using System.Net;
using AudioStreamer.Engine;
using AudioStreamer.Engine.Configuration;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Engine.Managers;
using AudioStreamer.Engine.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AudioStreamer.Engine.Tests;

public sealed class EngineDiagnosticsTests
{
    [Fact]
    public async Task DiagnosticsBeforeStartupReturnsIdleDefaults()
    {
        await using var engine = CreateEngine();

        var result = await engine.GetDiagnosticsAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(EngineLifecycleState.Stopped, result.Value!.Status.State);
        Assert.Equal("Stopped", result.Value.Health);
        Assert.Null(result.Value.CpuPercent);
        Assert.True(result.Value.WorkingSetBytes > 0);
        Assert.Equal(0, result.Value.Statistics!["packets_sent"]);
        Assert.Equal(0, result.Value.Statistics["latency_samples"]);
    }

    [Fact]
    public async Task DiagnosticsAfterStartupAndWhileIdleHasNoAudioRequirements()
    {
        await using var engine = CreateEngine();
        Assert.True((await engine.StartAsync(CancellationToken.None)).IsSuccess);

        var result = await engine.GetDiagnosticsAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(EngineLifecycleState.Running, result.Value!.Status.State);
        Assert.Equal("Healthy", result.Value.Health);
        Assert.Equal(0, result.Value.Statistics!["packets_received"]);
        Assert.Equal(0, result.Value.Statistics["queue_depth_frames"]);
        Assert.Null(result.Value.CpuPercent);
        Assert.True(result.Value.WorkingSetBytes > 0);
    }

    [Fact]
    public async Task DiagnosticsWhileStreamingDoesNotRequirePerformanceOrLatencySamples()
    {
        var logger = NullLogger<Engine>.Instance;
        var components = EngineComposition.Create(new MemoryConfigurationStore(), logger, new EngineOptions(),
            new UnavailableAudioDeviceCatalog(), TimeProvider.System, new SuccessfulAudioRuntime());
        await using var engine = new Engine(components, logger);
        Assert.True((await engine.StartAsync(CancellationToken.None)).IsSuccess);
        var localNodeId = (await engine.GetSnapshotAsync(CancellationToken.None)).Value!.NodeId;
        var route = new StreamRequest(localNodeId, Guid.NewGuid());
        var stream = await components.SessionManager.StartAsync(route, localNodeId, null,
            (await engine.GetStatusAsync(CancellationToken.None)).Value!, CancellationToken.None);
        Assert.True(stream.IsSuccess);

        var result = await engine.GetDiagnosticsAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(EngineLifecycleState.Running, result.Value!.Status.State);
        Assert.Equal(0, result.Value.Statistics!["latency_samples"]);
        Assert.Equal(0, result.Value.Statistics["packet_missing"]);
    }

    [Fact]
    public async Task DiagnosticsAfterStoppingReturnsStoppedStateAndDefaults()
    {
        await using var engine = CreateEngine();
        Assert.True((await engine.StartAsync(CancellationToken.None)).IsSuccess);
        Assert.True((await engine.StopAsync(CancellationToken.None)).IsSuccess);

        var result = await engine.GetDiagnosticsAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(EngineLifecycleState.Stopped, result.Value!.Status.State);
        Assert.Equal("Stopped", result.Value.Health);
        Assert.Equal(0, result.Value.Statistics!["queue_depth_packets"]);
    }

    [Fact]
    public async Task DiagnosticsAfterEngineTransportShutdownReturnsFinalSnapshot()
    {
        var engine = CreateEngine();
        Assert.True((await engine.StartAsync(CancellationToken.None)).IsSuccess);
        await engine.DisposeAsync();

        var result = await engine.GetDiagnosticsAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(EngineLifecycleState.Stopped, result.Value!.Status.State);
        Assert.Equal("Stopped", result.Value.Health);
        Assert.Null(result.Value.CpuPercent);
        Assert.True(result.Value.WorkingSetBytes > 0);
        Assert.NotNull(result.Value.Statistics);
    }

    [Fact]
    public async Task CpuAndMemoryMetricsCanBeUnavailableIndependently()
    {
        await using var engine = CreateEngine(diagnosticsMetrics: new FixedDiagnosticsMetricsProvider(null, null));

        var result = await engine.GetDiagnosticsAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.CpuPercent);
        Assert.Null(result.Value.WorkingSetBytes);
        Assert.NotNull(result.Value.Statistics);
    }

    [Fact]
    public async Task DiagnosticsManagerContainsMetricAndDisposedEventBusExceptions()
    {
        var options = new EngineOptions();
        var events = new EngineEventBus(Guid.NewGuid(), options);
        await events.DisposeAsync();
        var manager = new DiagnosticsManager(events, options, EngineVersionProvider.Current,
            new ThrowingDiagnosticsMetricsProvider(), NullLogger<DiagnosticsManager>.Instance);

        var snapshot = await manager.SampleAsync(new EngineStatus(EngineLifecycleState.Running, 1), CancellationToken.None);

        Assert.Null(snapshot.CpuPercent);
        Assert.Null(snapshot.WorkingSetBytes);
        Assert.Equal("Healthy", snapshot.Health);
    }

    [Fact]
    public async Task DiagnosticsDuringEngineStopWaitsForStableStoppedSnapshot()
    {
        var runtime = new BlockingStopAudioRuntime();
        var logger = NullLogger<Engine>.Instance;
        var components = EngineComposition.Create(new MemoryConfigurationStore(), logger, new EngineOptions(),
            new UnavailableAudioDeviceCatalog(), TimeProvider.System, runtime);
        await using var engine = new Engine(components, logger);
        Assert.True((await engine.StartAsync(CancellationToken.None)).IsSuccess);
        var nodeId = (await engine.GetSnapshotAsync(CancellationToken.None)).Value!.NodeId;
        var route = new StreamRequest(nodeId, Guid.NewGuid());
        Assert.True((await components.SessionManager.StartAsync(route, nodeId, null,
            (await engine.GetStatusAsync(CancellationToken.None)).Value!, CancellationToken.None)).IsSuccess);

        var stopping = engine.StopAsync(CancellationToken.None);
        await runtime.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var diagnostics = engine.GetDiagnosticsAsync(CancellationToken.None);
        runtime.AllowStop.TrySetResult();

        Assert.True((await stopping).IsSuccess);
        var result = await diagnostics;
        Assert.True(result.IsSuccess);
        Assert.Equal(EngineLifecycleState.Stopped, result.Value!.Status.State);
    }

    [Fact]
    public async Task DiagnosticsDuringEngineStartupWaitsForStableRunningSnapshot()
    {
        var store = new BlockingConfigurationStore();
        var logger = NullLogger<Engine>.Instance;
        var components = EngineComposition.Create(store, logger, new EngineOptions(),
            new UnavailableAudioDeviceCatalog(), TimeProvider.System);
        await using var engine = new Engine(components, logger);

        var starting = engine.StartAsync(CancellationToken.None);
        await store.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var diagnostics = engine.GetDiagnosticsAsync(CancellationToken.None);
        store.AllowLoad.TrySetResult();

        Assert.True((await starting).IsSuccess);
        var result = await diagnostics;
        Assert.True(result.IsSuccess);
        Assert.Equal(EngineLifecycleState.Running, result.Value!.Status.State);
    }

    [Fact]
    public async Task DiagnosticsRacingEngineDisposalReturnsAvailableFinalSnapshots()
    {
        var engine = CreateEngine();
        Assert.True((await engine.StartAsync(CancellationToken.None)).IsSuccess);
        var reads = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => engine.GetDiagnosticsAsync(CancellationToken.None)))
            .ToArray();
        var disposal = engine.DisposeAsync().AsTask();

        var results = await Task.WhenAll(reads);
        await disposal;

        Assert.All(results, result =>
        {
            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Value);
            Assert.NotNull(result.Value!.Statistics);
        });
    }

    [Fact]
    public async Task RepeatedDiagnosticsRequestsAlwaysReturnSnapshots()
    {
        await using var engine = CreateEngine();

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var result = await engine.GetDiagnosticsAsync(CancellationToken.None);
            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Value);
            Assert.False(result.Value!.Capabilities.IsDefault);
            Assert.NotNull(result.Value.Statistics);
        }
    }

    [Fact]
    public async Task DiagnosticsHonorsCancellationWhileWaitingForEngineLifecycleGate()
    {
        await using var engine = CreateEngine();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.GetDiagnosticsAsync(cancellation.Token));
    }

    private static Engine CreateEngine(IAudioSessionRuntime? audioRuntime = null,
        IDiagnosticsMetricsProvider? diagnosticsMetrics = null)
    {
        var logger = NullLogger<Engine>.Instance;
        var components = EngineComposition.Create(new MemoryConfigurationStore(), logger, new EngineOptions(),
            new UnavailableAudioDeviceCatalog(), TimeProvider.System, audioRuntime,
            diagnosticsMetrics: diagnosticsMetrics);
        return new Engine(components, logger);
    }

    private sealed class MemoryConfigurationStore : IConfigurationStore
    {
        public ValueTask<EngineSettings?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<EngineSettings?>(null);
        }

        public ValueTask SaveAsync(EngineSettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingConfigurationStore : IConfigurationStore
    {
        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowLoad { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<EngineSettings?> LoadAsync(CancellationToken cancellationToken)
        {
            LoadStarted.TrySetResult();
            await AllowLoad.Task.WaitAsync(cancellationToken);
            return null;
        }

        public ValueTask SaveAsync(EngineSettings settings, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class SuccessfulAudioRuntime : IAudioSessionRuntime
    {
        public Task<EngineResult<bool>> StartAsync(StreamRequest request, Guid localNodeId, IPEndPoint? audioEndpoint,
            CancellationToken cancellationToken) => Task.FromResult(EngineResult<bool>.Success(true));

        public Task<EngineResult<bool>> StopAsync(StreamRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(EngineResult<bool>.Success(true));

        public Task<EngineResult<bool>> UpdateEndpointAsync(StreamRequest request, IPEndPoint audioEndpoint,
            CancellationToken cancellationToken) => Task.FromResult(EngineResult<bool>.Success(true));

        public Task<EngineResult<bool>> PauseAsync(StreamRequest request, bool isSender,
            CancellationToken cancellationToken) => Task.FromResult(EngineResult<bool>.Success(true));

        public Task<EngineResult<bool>> ResumeAsync(StreamRequest request, bool isSender,
            CancellationToken cancellationToken) => Task.FromResult(EngineResult<bool>.Success(true));
    }

    private sealed class FixedDiagnosticsMetricsProvider(TimeSpan? cpuTime, long? workingSet) : IDiagnosticsMetricsProvider
    {
        public TimeSpan? GetCpuTime() => cpuTime;
        public long? GetWorkingSetBytes() => workingSet;
    }

    private sealed class ThrowingDiagnosticsMetricsProvider : IDiagnosticsMetricsProvider
    {
        public TimeSpan? GetCpuTime() => throw new InvalidOperationException("CPU provider is unavailable.");
        public long? GetWorkingSetBytes() => throw new ArgumentException("Memory provider failed.");
    }

    private sealed class BlockingStopAudioRuntime : IAudioSessionRuntime
    {
        public TaskCompletionSource StopStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<EngineResult<bool>> StartAsync(StreamRequest request, Guid localNodeId, IPEndPoint? audioEndpoint,
            CancellationToken cancellationToken) => Task.FromResult(EngineResult<bool>.Success(true));

        public async Task<EngineResult<bool>> StopAsync(StreamRequest request, CancellationToken cancellationToken)
        {
            StopStarted.TrySetResult();
            await AllowStop.Task.WaitAsync(cancellationToken);
            return EngineResult<bool>.Success(true);
        }

        public Task<EngineResult<bool>> UpdateEndpointAsync(StreamRequest request, IPEndPoint audioEndpoint,
            CancellationToken cancellationToken) => Task.FromResult(EngineResult<bool>.Success(true));

        public Task<EngineResult<bool>> PauseAsync(StreamRequest request, bool isSender,
            CancellationToken cancellationToken) => Task.FromResult(EngineResult<bool>.Success(true));

        public Task<EngineResult<bool>> ResumeAsync(StreamRequest request, bool isSender,
            CancellationToken cancellationToken) => Task.FromResult(EngineResult<bool>.Success(true));
    }
}
