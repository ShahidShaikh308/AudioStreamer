using System.Collections.Concurrent;

namespace AudioStreamer.IPC.Transport;

/// <summary>Owns the bounded set of connected IPC sessions for the server lifetime.</summary>
public sealed class ConnectionManager
{
    private readonly ConcurrentDictionary<Guid, ClientSession> _sessions = new();

    public IReadOnlyCollection<ClientSession> Sessions => _sessions.Values.ToArray();
    public int Count => _sessions.Count;

    internal bool TryAdd(ClientSession session) => _sessions.TryAdd(session.ConnectionId, session);
    internal bool Remove(Guid connectionId, out ClientSession? session) => _sessions.TryRemove(connectionId, out session);
}
