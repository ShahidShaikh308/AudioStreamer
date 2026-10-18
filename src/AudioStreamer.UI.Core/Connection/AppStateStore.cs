using System.Collections.Immutable;
using System.Text.Json;
using AudioStreamer.IPC.Contracts;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.UI.Core.Connection;

/// <summary>Owns the UI's single immutable projection of the service runtime.</summary>
public sealed class AppStateStore(ILogger<AppStateStore> logger)
{
    private readonly object _sync = new();
    private ServiceSnapshot _snapshot = ServiceSnapshot.Empty;
    private long _eventWatermark;
    private bool _hasAppliedEventSinceSnapshot;

    public ServiceSnapshot Snapshot
    {
        get { lock (_sync) return _snapshot; }
    }

    public event Action<string?>? Changed;
    public event Action<string>? Notification;

    public void BeginConnectionEpoch()
    {
        lock (_sync)
        {
            _eventWatermark = 0;
            _hasAppliedEventSinceSnapshot = false;
        }
    }

    public void Synchronize(ServiceSnapshot snapshot)
    {
        lock (_sync)
        {
            var watermark = snapshot.Node?.EventWatermark ?? 0;
            if (watermark < _eventWatermark || (_hasAppliedEventSinceSnapshot && watermark <= _eventWatermark)) return;
            _eventWatermark = watermark;
            _hasAppliedEventSinceSnapshot = false;
            _snapshot = snapshot with
            {
                Peers = VisiblePeers(snapshot.Peers),
                Streams = ActiveStreams(snapshot.Streams)
            };
        }
        Changed?.Invoke(null);
    }

    public void ApplySettings(SettingsSnapshot settings)
    {
        lock (_sync)
        {
            if (_snapshot.Settings == settings) return;
            _snapshot = _snapshot with { Settings = settings };
        }
        Changed?.Invoke("SettingsChanged");
    }

    public void Apply(IpcEnvelope message)
    {
        try
        {
            string? notification = null;
            var stateChanged = false;
            lock (_sync)
            {
                if (message.EventSequence is { } sequence && sequence <= _eventWatermark) return;
                var current = _snapshot;
                switch (message.MessageType)
                {
                    case "PeerAdded":
                    case "PeerUpdated":
                        var peerEvent = message.Payload.Deserialize<ControlPeerEventDto>(IpcProtocol.JsonOptions);
                        var peer = peerEvent is null ? null : MapPeer(peerEvent);
                        if (peer is not null)
                        {
                            var existingIndex = -1;
                            for (var index = 0; index < current.Peers.Length; index++)
                                if (current.Peers[index].NodeId == peer.NodeId) { existingIndex = index; break; }
                            if (!IsVisiblePeer(peer.Status))
                            {
                                if (existingIndex >= 0)
                                {
                                    _snapshot = current with { Peers = current.Peers.RemoveAt(existingIndex) };
                                    stateChanged = true;
                                }
                            }
                            else if (existingIndex < 0 || current.Peers[existingIndex] != peer)
                            {
                                var peers = current.Peers.Where(item => item.NodeId != peer.NodeId).Append(peer)
                                    .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToImmutableArray();
                                _snapshot = current with { Peers = peers };
                                stateChanged = true;
                            }
                        }
                        break;
                    case "PeerRemoved":
                        var removedEvent = message.Payload.Deserialize<ControlPeerEventDto>(IpcProtocol.JsonOptions);
                        if (removedEvent is not null && current.Peers.Any(item => item.NodeId == removedEvent.EngineNodeId))
                        {
                            _snapshot = current with { Peers = current.Peers.RemoveAll(item => item.NodeId == removedEvent.EngineNodeId) };
                            stateChanged = true;
                        }
                        break;
                    case "SessionsChanged":
                        var sessions = message.Payload.Deserialize<ImmutableArray<IpcStreamSnapshot>>(IpcProtocol.JsonOptions);
                        var activeSessions = ActiveStreams(sessions);
                        if (!current.Streams.SequenceEqual(activeSessions))
                        {
                            _snapshot = current with { Streams = activeSessions };
                            stateChanged = true;
                        }
                        break;
                    case "SessionCreated":
                    case "SessionPrepared":
                    case "SessionStarted":
                    case "SessionRecovered":
                    case "SessionStopped":
                    case "SessionFailed":
                    case "SessionChanged":
                        var session = message.Payload.Deserialize<IpcSessionEvent>(IpcProtocol.JsonOptions);
                        if (session is not null)
                        {
                            var route = NormalizeSession(new IpcStreamSnapshot(session.SenderNodeId, session.ReceiverNodeId, session.State));
                            var existing = current.Streams.Where(item => item.SenderNodeId != route.SenderNodeId ||
                                item.ReceiverNodeId != route.ReceiverNodeId).ToImmutableArray();
                            var nextStreams = IsActiveSessionState(route.State) ? existing.Add(route) : existing;
                            if (!current.Streams.SequenceEqual(nextStreams))
                            {
                                _snapshot = current with { Streams = nextStreams };
                                stateChanged = true;
                            }
                        }
                        break;
                    case "StatisticsChanged":
                        var statistics = message.Payload.Deserialize<StatisticsPayload>(IpcProtocol.JsonOptions);
                        if (statistics is not null && !StatisticsEqual(current.Statistics, statistics.Counters))
                        {
                            _snapshot = current with { Statistics = statistics.Counters };
                            stateChanged = true;
                        }
                        break;
                    case "NodeStateChanged":
                    case "DiagnosticsChanged":
                        var status = message.Payload.Deserialize<IpcEventPayload>(IpcProtocol.JsonOptions);
                        if (status is not null)
                        {
                            var diagnostics = current.Diagnostics is { } value
                                ? value with { State = status.State, Health = status.State, SafeErrorCode = status.SafeErrorCode }
                                : null;
                            var nextStatus = new StatusSnapshot(status.State, status.Revision, status.SafeErrorCode);
                            var nextNode = current.Node is { } node ? node with { Status = nextStatus } : null;
                            if (nextNode != current.Node || diagnostics != current.Diagnostics)
                            {
                                _snapshot = current with { Node = nextNode, Diagnostics = diagnostics };
                                stateChanged = true;
                            }
                        }
                        break;
                    case "AudioUnderrun":
                    case "AudioOverrun":
                    case "PacketLossDetected":
                    case "PacketRecovered":
                        notification = message.MessageType;
                        break;
                }
                if (message.EventSequence is { } appliedSequence)
                {
                    _eventWatermark = appliedSequence;
                    _hasAppliedEventSinceSnapshot = true;
                }
            }

            if (notification is not null) Notification?.Invoke(notification);
            if (stateChanged && message.MessageType is "PeerAdded" or "PeerUpdated" or "PeerRemoved" or "SessionsChanged" or
                "SessionCreated" or "SessionPrepared" or "SessionStarted" or "SessionRecovered" or "SessionStopped" or
                "SessionFailed" or "SessionChanged" or "StatisticsChanged" or "NodeStateChanged" or "DiagnosticsChanged")
                Changed?.Invoke(message.MessageType);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Ignored an event that did not match its IPC payload. EventType={EventType}", message.MessageType);
        }
    }

    private static IpcPeerSnapshot MapPeer(ControlPeerEventDto peer) => new(
        peer.EngineNodeId,
        peer.ProtocolNodeId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture),
        peer.Name,
        peer.Platform,
        peer.Capabilities,
        peer.Address,
        peer.AudioPort,
        peer.ControlPort,
        peer.Status switch { 1 => "Online", 2 => "Busy", 3 => "Streaming", 4 => "Error", _ => "Offline" },
        peer.LastSeenUtc,
        peer.ProtocolVersion);

    private static ImmutableArray<IpcPeerSnapshot> VisiblePeers(ImmutableArray<IpcPeerSnapshot> peers)
    {
        if (peers.IsDefaultOrEmpty) return [];
        var allVisible = true;
        foreach (var peer in peers)
            if (!IsVisiblePeer(peer.Status)) { allVisible = false; break; }
        if (allVisible) return peers;

        var builder = ImmutableArray.CreateBuilder<IpcPeerSnapshot>(peers.Length);
        foreach (var peer in peers)
            if (IsVisiblePeer(peer.Status)) builder.Add(peer);
        return builder.ToImmutable();
    }

    private static ImmutableArray<IpcStreamSnapshot> ActiveStreams(ImmutableArray<IpcStreamSnapshot> sessions)
    {
        if (sessions.IsDefaultOrEmpty) return [];
        var allActiveAndNormalized = true;
        foreach (var session in sessions)
        {
            if (IsActiveSessionState(session.State) &&
                !session.State.Equals("Negotiating", StringComparison.OrdinalIgnoreCase) &&
                !session.State.Equals("Preparing", StringComparison.OrdinalIgnoreCase) &&
                !session.State.Equals("Recovering", StringComparison.OrdinalIgnoreCase)) continue;
            allActiveAndNormalized = false;
            break;
        }
        if (allActiveAndNormalized) return sessions;

        var builder = ImmutableArray.CreateBuilder<IpcStreamSnapshot>(sessions.Length);
        foreach (var session in sessions)
        {
            var normalized = NormalizeSession(session);
            if (IsActiveSessionState(normalized.State)) builder.Add(normalized);
        }
        return builder.ToImmutable();
    }

    public static bool IsActiveSessionState(string state) => state.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Starting", StringComparison.OrdinalIgnoreCase) || state.Equals("Stopping", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Streaming", StringComparison.OrdinalIgnoreCase);

    private static IpcStreamSnapshot NormalizeSession(IpcStreamSnapshot session) =>
        session.State.Equals("Negotiating", StringComparison.OrdinalIgnoreCase) ||
        session.State.Equals("Preparing", StringComparison.OrdinalIgnoreCase) ||
        session.State.Equals("Recovering", StringComparison.OrdinalIgnoreCase)
            ? session with { State = "Starting" }
            : session;

    private static bool IsVisiblePeer(string status) =>
        !status.Equals("Offline", StringComparison.OrdinalIgnoreCase) &&
        !status.Equals("Removed", StringComparison.OrdinalIgnoreCase) &&
        !status.Equals("Expired", StringComparison.OrdinalIgnoreCase) &&
        !status.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    private static bool StatisticsEqual(ImmutableDictionary<string, long> first, ImmutableDictionary<string, long> second)
    {
        if (ReferenceEquals(first, second)) return true;
        if (first.Count != second.Count) return false;
        foreach (var pair in first)
            if (!second.TryGetValue(pair.Key, out var value) || value != pair.Value) return false;
        return true;
    }

    private sealed record ControlPeerEventDto(ulong ProtocolNodeId, Guid EngineNodeId, string Name, string Platform,
        ushort Capabilities, string Address, ushort AudioPort, int ControlPort, byte Status,
        DateTimeOffset LastSeenUtc, long LastSeenTimestamp, string ProtocolVersion);
}
