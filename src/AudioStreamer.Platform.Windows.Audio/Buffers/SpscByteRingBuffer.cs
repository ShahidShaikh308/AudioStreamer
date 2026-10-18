namespace AudioStreamer.Platform.Windows.Audio.Buffers;

/// <summary>Preallocated single-producer/single-consumer ring. The consumer never blocks or allocates.</summary>
internal sealed class SpscByteRingBuffer
{
    private readonly byte[] _buffer;
    private long _readPosition, _writePosition;
    public SpscByteRingBuffer(int capacityBytes)
    {
        if (capacityBytes < 4) throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        _buffer = new byte[capacityBytes];
    }
    public int Capacity => _buffer.Length;
    public int BufferedBytes => (int)Math.Min(Capacity, Volatile.Read(ref _writePosition) - Volatile.Read(ref _readPosition));

    public bool TryWrite(ReadOnlySpan<byte> data)
    {
        var write = _writePosition;
        var read = Volatile.Read(ref _readPosition);
        if (data.Length > Capacity - (write - read)) return false;
        var offset = (int)(write % Capacity);
        var first = Math.Min(data.Length, Capacity - offset);
        data[..first].CopyTo(_buffer.AsSpan(offset, first));
        data[first..].CopyTo(_buffer);
        Volatile.Write(ref _writePosition, write + data.Length);
        return true;
    }

    public int Read(Span<byte> destination)
    {
        var read = _readPosition;
        var write = Volatile.Read(ref _writePosition);
        var count = (int)Math.Min(destination.Length, write - read);
        var offset = (int)(read % Capacity);
        var first = Math.Min(count, Capacity - offset);
        _buffer.AsSpan(offset, first).CopyTo(destination);
        _buffer.AsSpan(0, count - first).CopyTo(destination[first..]);
        Volatile.Write(ref _readPosition, read + count);
        return count;
    }

    public void Clear() => Volatile.Write(ref _readPosition, Volatile.Read(ref _writePosition));
}
