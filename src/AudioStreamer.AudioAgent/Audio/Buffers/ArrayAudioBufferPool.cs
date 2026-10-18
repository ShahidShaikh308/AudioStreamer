using System.Buffers;
using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.AudioAgent.Contracts.Audio;

namespace AudioStreamer.AudioAgent.Audio.Buffers;

/// <summary>Agent-owned bounded ArrayPool-backed PCM memory owners.</summary>
public sealed class ArrayAudioBufferPool : IAudioBufferPool
{
    private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Shared;
    private readonly int _maximumRentBytes;
    private readonly int _maximumOutstanding;
    private long _rents, _returns, _outstanding, _bytesRented;

    public ArrayAudioBufferPool(int maximumRentBytes = 1_048_576, int maximumOutstanding = 256)
    {
        if (maximumRentBytes is < 1 or > 16_777_216) throw new ArgumentOutOfRangeException(nameof(maximumRentBytes));
        if (maximumOutstanding is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumOutstanding));
        _maximumRentBytes = maximumRentBytes;
        _maximumOutstanding = maximumOutstanding;
    }

    public IMemoryOwner<byte> Rent(int minimumBytes)
    {
        if (minimumBytes is < 1 || minimumBytes > _maximumRentBytes) throw new ArgumentOutOfRangeException(nameof(minimumBytes));
        if (Interlocked.Increment(ref _outstanding) > _maximumOutstanding)
        {
            Interlocked.Decrement(ref _outstanding);
            throw new InvalidOperationException("Audio frame buffer pool capacity is exhausted.");
        }
        byte[] buffer;
        try { buffer = _pool.Rent(minimumBytes); }
        catch { Interlocked.Decrement(ref _outstanding); throw; }
        Interlocked.Increment(ref _rents);
        Interlocked.Add(ref _bytesRented, minimumBytes);
        return new Owner(this, buffer);
    }

    public AudioBufferPoolSnapshot GetSnapshot() => new(Interlocked.Read(ref _rents), Interlocked.Read(ref _returns),
        Interlocked.Read(ref _outstanding), Interlocked.Read(ref _bytesRented));

    private sealed class Owner(ArrayAudioBufferPool pool, byte[] buffer) : IMemoryOwner<byte>
    {
        private byte[]? _buffer = buffer;
        public Memory<byte> Memory => (_buffer ?? throw new ObjectDisposedException(nameof(Owner))).AsMemory();
        public void Dispose()
        {
            var returned = Interlocked.Exchange(ref _buffer, null);
            if (returned is null) return;
            pool._pool.Return(returned, clearArray: true);
            Interlocked.Increment(ref pool._returns);
            Interlocked.Decrement(ref pool._outstanding);
        }
    }
}
