using System.Collections.Generic;

namespace AudioStreamer.Control.Protocol;

/// <summary>Bounded replay protection for recently authenticated controller datagrams.</summary>
public sealed class ControllerCommandReplayGuard
{
    private const int MaximumEntries = 1024;
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, DateTimeOffset SeenAt)> _order = new();

    /// <returns>false if this controller nonce has already been observed.</returns>
    public bool TryAccept(ulong controllerNodeId, ReadOnlySpan<byte> nonce, DateTimeOffset now)
    {
        if (controllerNodeId == 0 || nonce.Length != AuthenticatedControllerCommandCodec.NonceLength)
            return false;
        var key = $"{controllerNodeId:X16}:{Convert.ToHexString(nonce)}";
        lock (_gate)
        {
            while (_order.TryPeek(out var oldest) &&
                   (now - oldest.SeenAt >= Retention || _seen.Count >= MaximumEntries))
            {
                _order.Dequeue();
                if (_seen.TryGetValue(oldest.Key, out var seenAt) && seenAt == oldest.SeenAt)
                    _seen.Remove(oldest.Key);
            }

            if (_seen.ContainsKey(key)) return false;
            _seen.Add(key, now);
            _order.Enqueue((key, now));
            return true;
        }
    }
}
