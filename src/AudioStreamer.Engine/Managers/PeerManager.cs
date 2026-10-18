using System.Collections.Immutable;
using System.Net;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Engine.Runtime;

namespace AudioStreamer.Engine.Managers;

/// <summary>Owns the bounded peer registry and protocol endpoint metadata discovered by the Engine Control Coordinator.</summary>
internal sealed class PeerManager
{
    private readonly EngineEventBus _events;
    private readonly TimeProvider _clock;
    private readonly int _maximumPeers;
    private readonly TimeSpan _expiration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, PeerEntry> _peers = [];

    public PeerManager(EngineEventBus events, EngineOptions options, TimeProvider clock)
    {
        _events = events;
        _clock = clock;
        _maximumPeers = options.MaximumPeers;
        _expiration = options.EffectivePeerExpiration;
    }

    public async Task<EngineResult<ImmutableArray<PeerSnapshot>>> GetPeersAsync(EngineStatus status, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var changed = RemoveExpired();
            var snapshots = Snapshot();
            if (changed) await PublishAsync(snapshots, status, cancellationToken).ConfigureAwait(false);
            return EngineResult<ImmutableArray<PeerSnapshot>>.Success(snapshots);
        }
        finally { _gate.Release(); }
    }

    public async Task<EngineResult<ImmutableArray<PeerSnapshot>>> UpsertAsync(Guid nodeId, string displayName,
        EngineStatus status, CancellationToken cancellationToken)
    {
        if (nodeId == Guid.Empty || string.IsNullOrWhiteSpace(displayName) || displayName.Length > 80)
            return EngineResult<ImmutableArray<PeerSnapshot>>.Failure(EngineErrorCode.InvalidArgument, "peers", "Peer identity is invalid.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RemoveExpired();
            if (!_peers.ContainsKey(nodeId) && _peers.Count >= _maximumPeers)
                return EngineResult<ImmutableArray<PeerSnapshot>>.Failure(EngineErrorCode.ResourceBusy, "peers", "Peer registry capacity is full.", true);
            _peers[nodeId] = new PeerEntry(displayName.Trim(), _clock.GetTimestamp(), null, null, null, null, null, null, null, null, null);
            var snapshot = Snapshot();
            await PublishAsync(snapshot, status, cancellationToken).ConfigureAwait(false);
            return EngineResult<ImmutableArray<PeerSnapshot>>.Success(snapshot);
        }
        finally { _gate.Release(); }
    }

    public async Task UpsertControlPeerAsync(EngineControlPeer peer, EngineStatus status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (peer.EngineNodeId == Guid.Empty || string.IsNullOrWhiteSpace(peer.Name) || peer.Name.Length > 80)
            throw new ArgumentException("Peer identity is invalid.");
        ArgumentNullException.ThrowIfNull(peer.AudioEndpoint);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RemoveExpired();
            if (_peers.Any(entry => entry.Value.ProtocolNodeId == peer.ProtocolNodeId && entry.Key != peer.EngineNodeId))
                throw new InvalidOperationException("Control peer identity mapping collision detected.");
            if (!_peers.ContainsKey(peer.EngineNodeId) && _peers.Count >= _maximumPeers)
                throw new InvalidOperationException("Peer registry capacity is full.");
            _peers[peer.EngineNodeId] = new PeerEntry(peer.Name.Trim(), peer.LastSeenTimestamp, peer.AudioEndpoint,
                peer.ControlEndpoint, peer.Platform, peer.Capabilities, peer.ProtocolNodeId, peer.EngineNodeId,
                peer.Status, peer.LastSeenUtc, peer.ProtocolVersion);
            await PublishAsync(Snapshot(), status, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<EngineControlPeer?> FindControlPeerAsync(ulong protocolNodeId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _peers.Values.FirstOrDefault(peer => peer.ProtocolNodeId == protocolNodeId)?.ToControlPeer(); }
        finally { _gate.Release(); }
    }

    public async Task<ImmutableArray<EngineControlPeer>> GetControlPeersAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _peers.Values.Where(peer => peer.ProtocolNodeId.HasValue).Select(peer => peer.ToControlPeer()).OrderBy(peer => peer.ProtocolNodeId).ToImmutableArray(); }
        finally { _gate.Release(); }
    }

    public async Task<ImmutableArray<EngineControlPeer>> ExpireControlPeersAsync(EngineStatus status, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.GetTimestamp();
            var expired = _peers.Where(pair => pair.Value.ProtocolNodeId.HasValue && _clock.GetElapsedTime(pair.Value.LastSeen, now) >= _expiration).ToArray();
            if (expired.Length == 0) return [];
            foreach (var entry in expired) _peers.Remove(entry.Key);
            await PublishAsync(Snapshot(), status, cancellationToken).ConfigureAwait(false);
            return expired.Select(entry => entry.Value.ToControlPeer()).ToImmutableArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<IPEndPoint?> GetAudioEndpointAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _peers.TryGetValue(nodeId, out var peer) && peer.AudioEndpoint is { } endpoint
                ? new IPEndPoint(IPAddress.Parse(endpoint.Address), endpoint.Port)
                : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RemoveAsync(Guid nodeId, EngineStatus status, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_peers.Remove(nodeId)) return false;
            await PublishAsync(Snapshot(), status, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(EngineStatus status, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_peers.Count == 0) return;
            _peers.Clear();
            await PublishAsync(ImmutableArray<PeerSnapshot>.Empty, status, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private bool RemoveExpired()
    {
        var now = _clock.GetTimestamp();
        var expired = _peers.Where(pair => pair.Value.ProtocolNodeId is null && _clock.GetElapsedTime(pair.Value.LastSeen, now) >= _expiration)
            .Select(pair => pair.Key).ToArray();
        foreach (var nodeId in expired) _peers.Remove(nodeId);
        return expired.Length > 0;
    }

    private ImmutableArray<PeerSnapshot> Snapshot() => _peers
        .OrderBy(pair => pair.Key)
        .Select(pair => new PeerSnapshot(pair.Key, pair.Value.DisplayName))
        .ToImmutableArray();

    private async ValueTask PublishAsync(
    ImmutableArray<PeerSnapshot> peers,
    EngineStatus status,
    CancellationToken cancellationToken)
    {
        await _events.PublishAsync(
            "peers",
            "PeersChanged",
            status,
            new PeersChangedPayload(peers),
            cancellationToken);
    }

    private sealed record PeerEntry(string DisplayName, long LastSeen, ControlTransportEndpoint? AudioEndpoint,
        ControlTransportEndpoint? ControlEndpoint, string? Platform, ushort? Capabilities, ulong? ProtocolNodeId,
        Guid? EngineNodeId, byte? Status, DateTimeOffset? LastSeenUtc, string? ProtocolVersion)
    {
        public EngineControlPeer ToControlPeer() => new(ProtocolNodeId!.Value, EngineNodeId!.Value, DisplayName,
            Platform ?? "unknown", Capabilities ?? 0, AudioEndpoint!, ControlEndpoint!, Status ?? 0,
            LastSeenUtc ?? DateTimeOffset.MinValue, LastSeen, ProtocolVersion ?? "Control/1");
    }
}
