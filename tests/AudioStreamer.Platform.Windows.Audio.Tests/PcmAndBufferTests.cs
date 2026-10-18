using System.Buffers;
using System.Buffers.Binary;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Platform.Windows.Audio.Buffers;
using AudioStreamer.Platform.Windows.Audio.Clock;
using AudioStreamer.Platform.Windows.Audio.Diagnostics;
using AudioStreamer.Platform.Windows.Audio.Formats;
using Xunit;

namespace AudioStreamer.Platform.Windows.Audio.Tests;

public sealed class PcmAndBufferTests
{
    [Fact]
    public void ArrayPoolOwner_ReusesStorageAndTracksExactlyOneReturn()
    {
        var backing = new ReusingBytePool();
        var pool = new ArrayAudioBufferPool(maximumRentBytes: 1024, maximumOutstanding: 1, pool: backing);
        var first = pool.Rent(128);
        first.Memory.Span[0] = 17;
        first.Dispose();
        first.Dispose();
        var second = pool.Rent(128);

        Assert.Same(backing.LastReturned, MemoryMarshalTryGetArray(second.Memory));
        Assert.Equal(0, second.Memory.Span[0]); // Returned audio memory is cleared before reuse.
        Assert.Throws<InvalidOperationException>(() => pool.Rent(4));
        second.Dispose();
        var snapshot = pool.GetSnapshot();
        Assert.Equal(2, snapshot.Rents);
        Assert.Equal(2, snapshot.Returns);
        Assert.Equal(0, snapshot.Outstanding);
    }

    [Fact]
    public void AudioFrame_ValidatesAlignmentAndReportsDuration()
    {
        var format = new AudioFormat(48000, 2, PcmSampleFormat.Int16);
        var owner = new ArrayAudioBufferPool().Rent(1920);
        using var frame = new AudioFrame(owner, 1920, format, 480, 1234);

        Assert.Equal(TimeSpan.FromMilliseconds(10), frame.Duration);
        Assert.Equal(1920, frame.Data.Length);
        var invalidOwner = new ArrayAudioBufferPool().Rent(8);
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AudioFrame(invalidOwner, 3, format, 1, 0));
        }
        finally { invalidOwner.Dispose(); }
    }

    [Fact]
    public void Converter_ConvertsInt16MonoToFloatStereoAndResamples()
    {
        var converter = new PcmFormatConverter();
        var input = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(2, 2), short.MaxValue);
        var output = new byte[16];
        var frames = converter.Convert(input, new AudioFormat(44100, 1, PcmSampleFormat.Int16), output,
            new AudioFormat(48000, 2, PcmSampleFormat.Float32));

        Assert.Equal(2, frames);
        var leftFirst = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(0, 4)));
        var rightFirst = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(4, 4)));
        Assert.Equal(0f, leftFirst);
        Assert.Equal(leftFirst, rightFirst);
        var leftLast = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(8, 4)));
        Assert.InRange(leftLast, 0.9f, 1f);
    }

    [Fact]
    public void Converter_RejectsMisalignedPcmAndInsufficientDestination()
    {
        var converter = new PcmFormatConverter();
        var mono16 = new AudioFormat(48000, 1, PcmSampleFormat.Int16);
        Assert.Throws<ArgumentException>(() => converter.Convert(new byte[3], mono16, new byte[4], mono16));
        Assert.Throws<ArgumentException>(() => converter.Convert(new byte[4], mono16, new byte[2], mono16));
    }

    [Fact]
    public void Mixer_SumsAndSaturatesWithoutChangingInputs()
    {
        var mixer = new PcmMixer();
        float[] left = [0.75f, -0.75f];
        float[] right = [0.75f, -0.75f];
        float[] output = new float[2];

        Assert.Equal(2, mixer.Mix(left, right, output));
        Assert.Equal(new float[] { 1f, -1f }, output);
        Assert.Equal(new float[] { 0.75f, -0.75f }, left);
    }

    [Fact]
    public void SpscRing_IsBoundedAndPreservesWraparoundOrder()
    {
        var ring = new SpscByteRingBuffer(8);
        Assert.True(ring.TryWrite([1, 2, 3, 4, 5, 6]));
        Span<byte> first = stackalloc byte[4];
        Assert.Equal(4, ring.Read(first));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, first.ToArray());
        Assert.True(ring.TryWrite([7, 8, 9, 10, 11, 12]));
        Assert.False(ring.TryWrite([13]));
        Span<byte> second = stackalloc byte[8];
        Assert.Equal(8, ring.Read(second));
        Assert.Equal(new byte[] { 5, 6, 7, 8, 9, 10, 11, 12 }, second.ToArray());
    }

    [Fact]
    public void StopwatchClock_UsesMonotonicTicksAndCalculatesDrift()
    {
        var clock = new StopwatchAudioClock();
        var first = clock.GetTimestampTicks();
        var second = clock.GetTimestampTicks();

        Assert.True(clock.GetElapsed(first, second) >= TimeSpan.Zero);
        Assert.Equal(1000d, clock.CalculateDriftPartsPerMillion(1000, 1001, 48000), 3);
        Assert.Equal(-1000d, clock.CalculateDriftPartsPerMillion(1000, 999, 48000), 3);
    }

    [Fact]
    public void BufferPool_RejectsUnboundedOutstandingRents()
    {
        var pool = new ArrayAudioBufferPool(maximumRentBytes: 64, maximumOutstanding: 2);
        var first = pool.Rent(16);
        var second = pool.Rent(16);
        Assert.Throws<InvalidOperationException>(() => pool.Rent(16));
        first.Dispose();
        second.Dispose();
    }

    private static byte[] MemoryMarshalTryGetArray(Memory<byte> memory) =>
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)memory, out var segment)
            ? segment.Array!
            : throw new InvalidOperationException("Expected an array-backed memory block.");

    private sealed class ReusingBytePool : ArrayPool<byte>
    {
        private byte[]? _available;
        public byte[]? LastReturned { get; private set; }
        public override byte[] Rent(int minimumLength)
        {
            if (_available is { } buffer && buffer.Length >= minimumLength)
            {
                _available = null;
                return buffer;
            }
            return new byte[Math.Max(minimumLength, 128)];
        }
        public override void Return(byte[] array, bool clearArray = false)
        {
            if (clearArray) Array.Clear(array, 0, array.Length);
            LastReturned = array;
            _available = array;
        }
    }
}
