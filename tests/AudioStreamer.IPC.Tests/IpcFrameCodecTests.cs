using System.Buffers.Binary;
using AudioStreamer.IPC.Contracts;
using AudioStreamer.IPC.Protocol;
using Xunit;

namespace AudioStreamer.IPC.Tests;

public sealed class IpcFrameCodecTests
{
    [Fact]
    public async Task RoundTripsEnvelopeAndHandlesFragmentedReads()
    {
        var expected = IpcEnvelopeFactory.Request("Ping", Guid.NewGuid(), Guid.Empty, new PingPayload(DateTimeOffset.UtcNow));
        using var serialized = new MemoryStream();
        await IpcFrameCodec.WriteAsync(serialized, expected, CancellationToken.None);
        serialized.Position = 0;
        await using var fragmented = new FragmentedReadStream(serialized, maximumChunk: 2);

        var actual = await IpcFrameCodec.ReadAsync(fragmented, CancellationToken.None, TimeSpan.FromSeconds(1));

        Assert.NotNull(actual);
        Assert.Equal(expected.RequestId, actual.RequestId);
        Assert.Equal(expected.MessageType, actual.MessageType);
        Assert.Equal(
            IpcSerializer.DeserializePayload<PingPayload>(expected.Payload),
            IpcSerializer.DeserializePayload<PingPayload>(actual.Payload));
    }

    [Fact]
    public async Task RejectsZeroAndOversizedFramesBeforeBodyAllocation()
    {
        foreach (var size in new uint[] { 0, 1, IpcProtocol.MaximumFrameBytes + 1u, uint.MaxValue })
        {
            var header = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(header, size);
            using var input = new MemoryStream(header);
            await Assert.ThrowsAsync<IpcProtocolException>(async () =>
                await IpcFrameCodec.ReadAsync(input, CancellationToken.None, TimeSpan.FromSeconds(1)));
        }
    }

    [Fact]
    public async Task ReportsUnexpectedEndOfStreamInsideFrame()
    {
        var inputBytes = new byte[sizeof(uint) + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(inputBytes, 4);
        using var input = new MemoryStream(inputBytes);

        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await IpcFrameCodec.ReadAsync(input, CancellationToken.None, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task RejectsMalformedJson()
    {
        var bytes = "{broken"u8.ToArray();
        var frame = new byte[sizeof(uint) + bytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)bytes.Length);
        bytes.CopyTo(frame, sizeof(uint));
        using var input = new MemoryStream(frame);

        await Assert.ThrowsAsync<IpcProtocolException>(async () =>
            await IpcFrameCodec.ReadAsync(input, CancellationToken.None, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task RejectsTrailingJsonDocument()
    {
        var bytes = "{}{}"u8.ToArray();
        var frame = new byte[sizeof(uint) + bytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)bytes.Length);
        bytes.CopyTo(frame, sizeof(uint));
        using var input = new MemoryStream(frame);

        await Assert.ThrowsAsync<IpcProtocolException>(async () =>
            await IpcFrameCodec.ReadAsync(input, CancellationToken.None, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ReportsIncompleteFrameDeadlineAsProtocolFailure()
    {
        await using var input = new StalledReadStream();
        var exception = await Assert.ThrowsAsync<IpcProtocolException>(async () =>
            await IpcFrameCodec.ReadAsync(input, CancellationToken.None, TimeSpan.FromMilliseconds(20)));
        Assert.Equal(IpcErrors.InvalidRequest, exception.Code);
    }

    private sealed class FragmentedReadStream(Stream inner, int maximumChunk) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(maximumChunk, buffer.Length)], cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StalledReadStream : Stream
    {
        private bool _firstRead = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_firstRead)
            {
                _firstRead = false;
                SetFirstByte(buffer);
                return 1;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        private static void SetFirstByte(Memory<byte> buffer) => buffer.Span[0] = 1;
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
