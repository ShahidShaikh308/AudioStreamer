namespace AudioStreamer.Audio.Transport.Transport;

/// <summary>Tracks v3 sequence freshness within a stream ID, including modulo-2^32 wrap.</summary>
public sealed class AudioSequenceTracker
{
    private uint _lastSequence;
    private ulong _streamId;
    private bool _initialized;

    public bool TryAccept(uint sequence, ulong streamId, out uint missing)
    {
        missing = 0;
        if (streamId == 0) return false;
        if (!_initialized || streamId != _streamId)
        {
            _initialized = true;
            _streamId = streamId;
            _lastSequence = sequence;
            return true;
        }
        var distance = unchecked(sequence - _lastSequence);
        if (distance == 0 || distance >= 0x80000000) return false;
        missing = distance - 1;
        _lastSequence = sequence;
        return true;
    }
}
