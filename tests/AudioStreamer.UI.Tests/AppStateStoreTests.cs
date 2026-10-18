using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using AudioStreamer.IPC.Contracts;
using AudioStreamer.UI.Core.Connection;
using AudioStreamer.UI.Core.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AudioStreamer.UI.Tests;

public sealed class AppStateStoreTests
{
    [Fact]
    public void Peer_and_session_events_update_the_shared_snapshot()
    {
        var store = new AppStateStore(NullLogger<AppStateStore>.Instance);
        var sender = Guid.NewGuid();
        var receiver = Guid.NewGuid();
        var peer = PeerEvent(sender, "Sender");

        store.Apply(Event("PeerAdded", peer));
        store.Apply(Event("SessionsChanged", ImmutableArray.Create(new IpcStreamSnapshot(sender, receiver, "Streaming"))));

        Assert.Contains(store.Snapshot.Peers, item => item.NodeId == sender);
        Assert.Equal("Online", Assert.Single(store.Snapshot.Peers).Status);
        Assert.Contains(store.Snapshot.Streams, item => item.SenderNodeId == sender && item.ReceiverNodeId == receiver);

        store.Apply(Event("PeerRemoved", peer));
        Assert.Empty(store.Snapshot.Peers);
    }

    [Fact]
    public void Reconnect_snapshot_replaces_the_old_projection()
    {
        var store = new AppStateStore(NullLogger<AppStateStore>.Instance);
        var oldNode = Guid.NewGuid();
        var newNode = Guid.NewGuid();
        var oldPeer = Peer(oldNode, "Old");
        var newPeer = Peer(newNode, "New");
        store.Synchronize(Snapshot(oldPeer));

        store.Synchronize(Snapshot(newPeer));

        Assert.Equal(new[] { newNode }, store.Snapshot.Peers.Select(peer => peer.NodeId));
        Assert.Empty(store.Snapshot.Streams);
    }

    [Fact]
    public void Statistics_events_update_the_shared_metrics()
    {
        var store = new AppStateStore(NullLogger<AppStateStore>.Instance);
        var counters = ImmutableDictionary<string, long>.Empty.Add("packet_missing", 4);
        var updates = 0;
        store.Changed += _ => updates++;

        store.Apply(Event("StatisticsChanged", new StatisticsPayload(DateTimeOffset.UtcNow, counters)));
        store.Apply(Event("StatisticsChanged", new StatisticsPayload(DateTimeOffset.UtcNow, counters)));

        Assert.Equal(4, store.Snapshot.Statistics["packet_missing"]);
        Assert.Equal(1, updates);
    }

    [Fact]
    public void Snapshot_watermark_discards_queued_older_events()
    {
        var store = new AppStateStore(NullLogger<AppStateStore>.Instance);
        var stalePeerId = Guid.NewGuid();
        store.Synchronize(ServiceSnapshot.Empty with
        {
            Node = new NodeSnapshot(Guid.NewGuid(), "Local", new StatusSnapshot("Running", 1, null), 0, 12)
        });

        store.Apply(Event("PeerAdded", PeerEvent(stalePeerId, "Stale"), sequence: 12));

        Assert.Empty(store.Snapshot.Peers);
    }

    [Fact]
    public void Event_newer_than_snapshot_is_not_lost_to_a_late_snapshot_response()
    {
        var store = new AppStateStore(NullLogger<AppStateStore>.Instance);
        var peerId = Guid.NewGuid();
        store.Apply(Event("PeerAdded", PeerEvent(peerId, "Current"), sequence: 8));

        store.Synchronize(ServiceSnapshot.Empty with
        {
            Node = new NodeSnapshot(Guid.NewGuid(), "Local", new StatusSnapshot("Running", 1, null), 0, 7)
        });

        Assert.Equal(peerId, Assert.Single(store.Snapshot.Peers).NodeId);
    }

    [Fact]
    public void Snapshot_with_same_watermark_does_not_overwrite_a_newly_applied_event()
    {
        var store = new AppStateStore(NullLogger<AppStateStore>.Instance);
        var peerId = Guid.NewGuid();
        store.Synchronize(ServiceSnapshot.Empty with
        {
            Node = new NodeSnapshot(Guid.NewGuid(), "Local", new StatusSnapshot("Running", 1, null), 0, 7)
        });
        store.Apply(Event("PeerAdded", PeerEvent(peerId, "Current"), sequence: 8));

        store.Synchronize(ServiceSnapshot.Empty with
        {
            Node = new NodeSnapshot(Guid.NewGuid(), "Local", new StatusSnapshot("Running", 1, null), 0, 8)
        });

        Assert.Equal(peerId, Assert.Single(store.Snapshot.Peers).NodeId);
    }

    [Fact]
    public void New_connection_epoch_accepts_a_reset_event_watermark()
    {
        var store = new AppStateStore(NullLogger<AppStateStore>.Instance);
        store.Synchronize(ServiceSnapshot.Empty with
        {
            Node = new NodeSnapshot(Guid.NewGuid(), "Old", new StatusSnapshot("Running", 1, null), 0, 80)
        });

        store.BeginConnectionEpoch();
        store.Synchronize(ServiceSnapshot.Empty with
        {
            Node = new NodeSnapshot(Guid.NewGuid(), "Restarted", new StatusSnapshot("Running", 1, null), 0, 2)
        });

        Assert.Equal("Restarted", store.Snapshot.Node?.NodeName);
    }

    [Fact]
    public void Settings_update_replaces_only_the_shared_settings_projection()
    {
        var store = new AppStateStore(NullLogger<AppStateStore>.Instance);
        var peer = Peer(Guid.NewGuid(), "Peer");
        var settings = new SettingsSnapshot(1, 2, Guid.NewGuid(), "Renamed");
        store.Synchronize(Snapshot(peer));

        store.ApplySettings(settings);

        Assert.Equal("Renamed", store.Snapshot.Settings?.NodeName);
        Assert.Equal(peer.NodeId, Assert.Single(store.Snapshot.Peers).NodeId);
    }

    [Fact]
    public void Reapplying_identical_settings_does_not_notify_the_ui_twice()
    {
        var store = new AppStateStore(NullLogger<AppStateStore>.Instance);
        var settings = new SettingsSnapshot(1, 2, Guid.NewGuid(), "Node");
        var updates = 0;
        store.Changed += _ => updates++;

        store.ApplySettings(settings);
        store.ApplySettings(settings);

        Assert.Equal(1, updates);
    }

    [Fact]
    public void Busy_peer_remains_visible_and_available_for_capable_routes()
    {
        var store = new AppStateStore(NullLogger<AppStateStore>.Instance);
        var connection = new IpcConnectionCoordinator(NullLoggerFactory.Instance,
            NullLogger<IpcConnectionCoordinator>.Instance, store);
        var state = new RuntimeStateViewModel(connection, store, new ImmediateDispatcher(),
            NullLogger<RuntimeStateViewModel>.Instance);
        var sessions = new SessionsViewModel(state, NullLogger<SessionsViewModel>.Instance);
        var peerId = Guid.NewGuid();
        store.Apply(Event("PeerUpdated", PeerEvent(peerId, "Busy device", capabilities: 12, status: 2)));

        Assert.Equal("Busy", Assert.Single(store.Snapshot.Peers).Status);
        Assert.Contains(sessions.Senders, peer => peer.NodeId == peerId);
        Assert.Contains(sessions.Receivers, peer => peer.NodeId == peerId);
    }

    [Fact]
    public void Offline_peer_is_removed_from_snapshot_projection()
    {
        var store = new AppStateStore(NullLogger<AppStateStore>.Instance);
        var peerId = Guid.NewGuid();
        store.Synchronize(Snapshot(Peer(peerId, "Peer")));

        store.Apply(Event("PeerUpdated", PeerEvent(peerId, "Peer", status: 0)));

        Assert.Empty(store.Snapshot.Peers);
    }

    [Fact]
    public void Session_projection_contains_only_active_states_and_normalizes_startup()
    {
        var store = new AppStateStore(NullLogger<AppStateStore>.Instance);
        var sender = Guid.NewGuid();
        var receiver = Guid.NewGuid();
        store.Synchronize(ServiceSnapshot.Empty with
        {
            Streams =
            [
                new IpcStreamSnapshot(sender, receiver, "Negotiating"),
                new IpcStreamSnapshot(sender, Guid.NewGuid(), "Preparing"),
                new IpcStreamSnapshot(sender, Guid.NewGuid(), "Recovering"),
                new IpcStreamSnapshot(sender, Guid.NewGuid(), "Running"),
                new IpcStreamSnapshot(sender, Guid.NewGuid(), "Streaming"),
                new IpcStreamSnapshot(sender, Guid.NewGuid(), "Stopping"),
                new IpcStreamSnapshot(sender, Guid.NewGuid(), "Paused"),
                new IpcStreamSnapshot(sender, Guid.NewGuid(), "Failed"),
                new IpcStreamSnapshot(sender, Guid.NewGuid(), "Stopped")
            ]
        });

        Assert.Equal(new[] { "Starting", "Starting", "Starting", "Running", "Streaming", "Stopping" },
            store.Snapshot.Streams.Select(session => session.State));
    }

    [Fact]
    public void Repeated_snapshot_reuses_already_normalized_immutable_collections()
    {
        var store = new AppStateStore(NullLogger<AppStateStore>.Instance);
        var peers = ImmutableArray.Create(Peer(Guid.NewGuid(), "Peer"));
        var streams = ImmutableArray.Create(new IpcStreamSnapshot(Guid.NewGuid(), Guid.NewGuid(), "Streaming"));
        var snapshot = ServiceSnapshot.Empty with { Peers = peers, Streams = streams };

        store.Synchronize(snapshot);
        store.Synchronize(snapshot);

        Assert.Same(ImmutableCollectionsMarshal.AsArray(peers), ImmutableCollectionsMarshal.AsArray(store.Snapshot.Peers));
        Assert.Same(ImmutableCollectionsMarshal.AsArray(streams), ImmutableCollectionsMarshal.AsArray(store.Snapshot.Streams));
    }

    [Fact]
    public void Streaming_android_peer_remains_available_in_sender_and_receiver_choices()
    {
        var store = new AppStateStore(NullLogger<AppStateStore>.Instance);
        var connection = new IpcConnectionCoordinator(NullLoggerFactory.Instance,
            NullLogger<IpcConnectionCoordinator>.Instance, store);
        var state = new RuntimeStateViewModel(connection, store, new ImmediateDispatcher(),
            NullLogger<RuntimeStateViewModel>.Instance);
        var sessions = new SessionsViewModel(state, NullLogger<SessionsViewModel>.Instance);
        store.Synchronize(ServiceSnapshot.Empty with
        {
            Node = new NodeSnapshot(Guid.NewGuid(), "Windows", new StatusSnapshot("Running", 1, null), 0, 0)
        });
        var androidId = Guid.NewGuid();
        store.Apply(Event("PeerUpdated", PeerEvent(androidId, "Android", capabilities: 12, status: 3, platform: "Android")));

        Assert.Equal("Streaming", Assert.Single(store.Snapshot.Peers).Status);
        Assert.Contains(sessions.Senders, peer => peer.NodeId == androidId);
        Assert.Contains(sessions.Receivers, peer => peer.NodeId == androidId);
    }

    [Fact]
    public void Stopped_session_is_removed_from_event_and_snapshot_projections()
    {
        var store = new AppStateStore(NullLogger<AppStateStore>.Instance);
        var sender = Guid.NewGuid();
        var receiver = Guid.NewGuid();
        store.Synchronize(ServiceSnapshot.Empty with
        {
            Streams = [new IpcStreamSnapshot(sender, receiver, "Streaming")]
        });

        store.Apply(Event("SessionStopped", new IpcSessionEvent(sender, receiver, "Stopping", "Stopped")));

        Assert.Empty(store.Snapshot.Streams);
        store.Synchronize(ServiceSnapshot.Empty with
        {
            Streams = [new IpcStreamSnapshot(sender, receiver, "Stopped")]
        });
        Assert.Empty(store.Snapshot.Streams);
    }

    [Fact]
    public void Running_session_is_restored_from_reconnect_snapshot()
    {
        var store = new AppStateStore(NullLogger<AppStateStore>.Instance);
        var sender = Guid.NewGuid();
        var receiver = Guid.NewGuid();

        store.Synchronize(ServiceSnapshot.Empty with
        {
            Streams = [new IpcStreamSnapshot(sender, receiver, "Streaming")]
        });

        Assert.Equal(new IpcStreamSnapshot(sender, receiver, "Streaming"), Assert.Single(store.Snapshot.Streams));
    }

    private static IpcEnvelope Event<T>(string name, T payload, long? sequence = null) => new()
    {
        SchemaVersion = new IpcSchemaVersion(IpcProtocol.MajorVersion, IpcProtocol.MinorVersion),
        Kind = "event",
        MessageType = name,
        EventSequence = sequence,
        Payload = JsonSerializer.SerializeToElement(payload, IpcProtocol.JsonOptions),
        Timestamp = DateTimeOffset.UtcNow
    };

    private static IpcPeerSnapshot Peer(Guid id, string name) => new(id, id.ToString("N"), name, "Windows", 1,
        "127.0.0.1", 5006, 55050, "Online", DateTimeOffset.UtcNow, "1");

    private static object PeerEvent(Guid id, string name, ushort capabilities = 1, byte status = 1, string platform = "Windows") => new
    {
        ProtocolNodeId = 1UL,
        EngineNodeId = id,
        Name = name,
        Platform = platform,
        Capabilities = capabilities,
        Address = "127.0.0.1",
        AudioPort = (ushort)5006,
        ControlPort = 55050,
        Status = status,
        LastSeenUtc = DateTimeOffset.UtcNow,
        LastSeenTimestamp = 1L,
        ProtocolVersion = "1"
    };

    private static ServiceSnapshot Snapshot(IpcPeerSnapshot peer) => ServiceSnapshot.Empty with { Peers = [peer] };

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool TryEnqueue(Action action) { action(); return true; }
    }
}
