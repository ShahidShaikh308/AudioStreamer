using System.Collections.Immutable;
using System.Net;
using AudioStreamer.Engine;
using AudioStreamer.Engine.Configuration;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Engine.Managers;
using AudioStreamer.Engine.Runtime;
using Xunit;

namespace AudioStreamer.Engine.Tests;

public sealed class EngineManagerTests
{
    [Fact]
    public async Task EventBus_PreservesSequenceAndStateStoreProjectsLatestState()
    {
        var bus = new EngineEventBus(Guid.NewGuid(), new EngineOptions());
        await using var state = new StateStore(bus, Guid.NewGuid());
        var identity = state.Current.EngineInstanceId;
        var node = new NodeManager(bus);
        var settings = EngineSettings.CreateDefault("Test Node");

        await node.StartAsync(settings, CancellationToken.None);
        await state.WaitForSequenceAsync(bus.CurrentSequence, CancellationToken.None);

        Assert.Equal(EngineLifecycleState.Running, state.Current.Status.State);
        Assert.Equal(settings.NodeId, state.Current.NodeId);
        Assert.Equal(bus.CurrentSequence, state.Current.EventWatermark);
        Assert.NotEqual(Guid.Empty, identity);
        await node.DisposeAsync();
        await bus.DisposeAsync();
    }

    [Fact]
    public async Task SessionManager_EnforcesTransitionsAndExposesImmutableSnapshot()
    {
        var bus = new EngineEventBus(Guid.NewGuid(), new EngineOptions());
        var manager = new SessionManager(bus);
        var request = new StreamRequest(Guid.NewGuid(), Guid.NewGuid());
        var status = new EngineStatus(EngineLifecycleState.Running, 1);

        var invalid = await manager.ApplyStateAsync(request, StreamSessionState.Streaming, status, CancellationToken.None);
        var negotiating = await manager.ApplyStateAsync(request, StreamSessionState.Negotiating, status, CancellationToken.None);
        var preparing = await manager.ApplyStateAsync(request, StreamSessionState.Preparing, status, CancellationToken.None);
        var streaming = await manager.ApplyStateAsync(request, StreamSessionState.Streaming, status, CancellationToken.None);
        var stopping = await manager.ApplyStateAsync(request, StreamSessionState.Stopping, status, CancellationToken.None);
        var stopped = await manager.ApplyStateAsync(request, StreamSessionState.Stopped, status, CancellationToken.None);

        Assert.Equal(EngineErrorCode.InvalidState, invalid.Error!.Code);
        Assert.True(negotiating.IsSuccess);
        Assert.True(preparing.IsSuccess);
        Assert.Equal("Streaming", streaming.Value!.State);
        Assert.True(stopping.IsSuccess);
        Assert.Equal("Stopped", stopped.Value!.State);
        Assert.Single(manager.GetSnapshot());
        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PeerManager_EnforcesCapacityAndExpiresUsingMonotonicClock()
    {
        var clock = new TestClock();
        var options = new EngineOptions(MaximumPeers: 1, PeerExpiration: TimeSpan.FromSeconds(30));
        var bus = new EngineEventBus(Guid.NewGuid(), options);
        var manager = new PeerManager(bus, options, clock);
        var status = new EngineStatus(EngineLifecycleState.Running, 1);
        var firstId = Guid.NewGuid();

        Assert.True((await manager.UpsertAsync(firstId, "Peer", status, CancellationToken.None)).IsSuccess);
        Assert.Equal(EngineErrorCode.ResourceBusy,
            (await manager.UpsertAsync(Guid.NewGuid(), "Overflow", status, CancellationToken.None)).Error!.Code);
        clock.Advance(TimeSpan.FromSeconds(31));
        var expired = await manager.GetPeersAsync(status, CancellationToken.None);

        Assert.Empty(expired.Value!);
        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PeerManager_OwnsControlMetadataAndExpiresControlPeers()
    {
        var clock = new TestClock();
        var options = new EngineOptions(MaximumPeers: 2, PeerExpiration: TimeSpan.FromSeconds(30));
        var bus = new EngineEventBus(Guid.NewGuid(), options);
        var manager = new PeerManager(bus, options, clock);
        var status = new EngineStatus(EngineLifecycleState.Running, 1);
        var peer = new EngineControlPeer(42, Guid.NewGuid(), "Studio", "windows", 3,
            new ControlTransportEndpoint("127.0.0.1", 5006), new ControlTransportEndpoint("127.0.0.1", 55050), 1,
            DateTimeOffset.UtcNow, clock.GetTimestamp());

        await manager.UpsertControlPeerAsync(peer, status, CancellationToken.None);
        var found = await manager.FindControlPeerAsync(42, CancellationToken.None);
        Assert.Equal(peer.AudioEndpoint, found!.AudioEndpoint);
        Assert.Single(await manager.GetControlPeersAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(31));
        var expired = await manager.ExpireControlPeersAsync(status, CancellationToken.None);

        Assert.Single(expired);
        Assert.Empty(await manager.GetControlPeersAsync(CancellationToken.None));
        await bus.DisposeAsync();
    }

    [Fact]
    public async Task DeviceManager_UsesInjectedPlatformCatalogAndValidatesSelection()
    {
        var bus = new EngineEventBus(Guid.NewGuid(), new EngineOptions());
        var catalog = new TestCatalog();
        var manager = new DeviceManager(catalog, bus);
        var status = new EngineStatus(EngineLifecycleState.Running, 1);
        var devices = await manager.EnumerateAsync(status, CancellationToken.None);

        Assert.True(devices.IsSuccess);
        Assert.Single(devices.Value!);
        Assert.True((await manager.SelectAsync(new AudioDeviceSelection("endpoint-1", null), CancellationToken.None)).IsSuccess);
        var missing = await manager.SelectAsync(new AudioDeviceSelection("missing", null), CancellationToken.None);
        Assert.Equal(EngineErrorCode.NotFound, missing.Error!.Code);
        await bus.DisposeAsync();
    }

    [Fact]
    public async Task Engine_RejectsInvalidRouteAndReportsUnavailableCapabilities()
    {
        await using var engine = new Engine(new TestConfigurationStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger<AudioStreamer.Engine.Engine>.Instance);
        await engine.StartAsync(CancellationToken.None);

        var streaming = await engine.StartStreamingAsync(new StreamRequest(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        var discovery = await engine.RestartDiscoveryAsync(CancellationToken.None);
        var devices = await engine.GetAudioDevicesAsync(CancellationToken.None);

        Assert.Equal(EngineErrorCode.InvalidArgument, streaming.Error!.Code);
        Assert.Equal(EngineErrorCode.CapabilityUnavailable, discovery.Error!.Code);
        Assert.Equal(EngineErrorCode.CapabilityUnavailable, devices.Error!.Code);
    }

    private sealed class TestClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }

    private sealed class TestCatalog : IAudioDeviceCatalog
    {
        private static readonly ImmutableArray<AudioDeviceInfo> Devices =
            [new AudioDeviceInfo("endpoint-1", "Test Device", "capture", true, true)];
        public ValueTask<EngineResult<ImmutableArray<AudioDeviceInfo>>> EnumerateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(EngineResult<ImmutableArray<AudioDeviceInfo>>.Success(Devices));
        public ValueTask<EngineResult<bool>> SelectAsync(AudioDeviceSelection selection, CancellationToken cancellationToken) =>
            ValueTask.FromResult(EngineResult<bool>.Success(true));
    }

    private sealed class TestConfigurationStore : IConfigurationStore
    {
        private EngineSettings? _settings;
        public ValueTask<EngineSettings?> LoadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_settings);
        public ValueTask SaveAsync(EngineSettings settings, CancellationToken cancellationToken)
        {
            _settings = settings;
            return ValueTask.CompletedTask;
        }
    }
}
