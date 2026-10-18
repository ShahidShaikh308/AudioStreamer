using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using AudioStreamer.Audio.Transport.Protocol;
using AudioStreamer.Engine.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioStreamer.Audio.Transport.Transport;

/// <summary>Bounded asynchronous v3 packet scheduler and UDP sender. One instance owns one sender lane.</summary>
public sealed class AudioUdpSender : IAsyncDisposable
{
    private readonly IAudioFormatConverter _converter;
    private readonly ILogger _logger;
    private IPEndPoint _destination;
    private readonly Channel<AudioProtocolV3Packet> _queue;
    private readonly ulong _streamId;
    private readonly object _sequenceGate = new();
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private uint _sequence;
    private long _sentPackets, _sentBytes, _dropped, _socketErrors;
    private int _started, _disposed;

    public AudioUdpSender(IPEndPoint destination, int queueCapacity, IAudioFormatConverter converter,
        ILogger? logger = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(destination));
        if (queueCapacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        _destination = destination;
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _logger = logger ?? NullLogger.Instance;
        // Keep the optional clock parameter for source compatibility. Packet timing is
        // reconstructed by the receiver from sequence order and PCM format; pacing each
        // MTU-sized datagram with Task.Delay can be quantized to the Windows timer
        // resolution and make this sender slower than the capture producer.
        _queue = Channel.CreateBounded<AudioProtocolV3Packet>(new BoundedChannelOptions(queueCapacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
        Span<byte> id = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(id);
        var streamId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(id);
        _streamId = streamId == 0 ? 1 : streamId;
    }

    public ulong StreamId => _streamId;
    public AudioUdpSenderSnapshot Snapshot => new(Interlocked.Read(ref _sentPackets), Interlocked.Read(ref _sentBytes),
        Interlocked.Read(ref _dropped), Interlocked.Read(ref _socketErrors), _queue.Reader.Count, _streamId);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _lifetime = new CancellationTokenSource();
        _worker = SendLoopAsync(_lifetime.Token);
    }

    public void UpdateDestination(IPEndPoint destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Port is < 1 or > 65535 || destination.AddressFamily != _destination.AddressFamily)
            throw new ArgumentOutOfRangeException(nameof(destination));
        Volatile.Write(ref _destination, new IPEndPoint(destination.Address, destination.Port));
    }

    public bool Enqueue(AudioFrame frame)
    {
        if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _disposed) != 0)
            throw new InvalidOperationException("Audio UDP sender is not running.");
        var source = frame.Data;
        var format = frame.Format;
        ReadOnlyMemory<byte> pcm = source;
        byte[]? converted = null;
        var target = new AudioFormat(format.SampleRate, AudioProtocolV3Codec.StereoChannelCount, PcmSampleFormat.Float32);
        if (format != target)
        {
            converted = ArrayPool<byte>.Shared.Rent(checked(frame.FrameCount * target.BytesPerFrame));
            try
            {
                var frames = _converter.Convert(source.Span, format, converted, target);
                pcm = converted.AsMemory(0, frames * target.BytesPerFrame);
                return EnqueuePayloads(pcm, target.SampleRate);
            }
            finally { ArrayPool<byte>.Shared.Return(converted, clearArray: true); }
        }
        return EnqueuePayloads(pcm, target.SampleRate);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 0) != 0)
            _queue.Writer.TryComplete();

        var worker = _worker;
        if (worker is not null)
            await worker.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (ReferenceEquals(_worker, worker))
        {
            _lifetime?.Dispose();
            _lifetime = null;
            _worker = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
    }

    private bool EnqueuePayloads(ReadOnlyMemory<byte> pcm, int sampleRate)
    {
        var accepted = true;
        lock (_sequenceGate)
        {
            foreach (var payload in AudioProtocolV3Packetizer.Split(pcm))
            {
                var packet = new AudioProtocolV3Packet(_sequence++, _streamId, sampleRate,
                    AudioProtocolV3Codec.StereoChannelCount, AudioProtocolV3Codec.Float32LittleEndianSampleFormat, payload.ToArray());
                if (_queue.Writer.TryWrite(packet)) continue;
                Interlocked.Increment(ref _dropped);
                accepted = false;
            }
        }
        return accepted;
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        Socket? socket = null;
        var datagram = ArrayPool<byte>.Shared.Rent(AudioProtocolV3Codec.MaximumDatagramSize);
        try
        {
            await foreach (var packet in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var destination = Volatile.Read(ref _destination);
                    socket ??= new Socket(destination.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                    var length = AudioProtocolV3Codec.Serialize(packet, datagram);
                    var sent = await socket.SendToAsync(datagram.AsMemory(0, length), SocketFlags.None,
                        destination, cancellationToken).ConfigureAwait(false);
                    if (sent != length) throw new SocketException((int)SocketError.MessageSize);
                    Interlocked.Increment(ref _sentPackets);
                    Interlocked.Add(ref _sentBytes, sent);
                }
                catch (SocketException exception)
                {
                    _logger.LogError(exception,
                        "Audio UDP send failed; sender will preserve its existing retry behavior. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} HResult={HResult} StackTrace={StackTrace} InnerException={InnerException} InnerStackTrace={InnerStackTrace}",
                        exception.GetType().FullName, exception.Message, exception.HResult, exception.StackTrace,
                        exception.InnerException?.ToString(), exception.InnerException?.StackTrace);
                    Interlocked.Increment(ref _socketErrors);
                    socket?.Dispose();
                    socket = null;
                }
            }
        }
        finally { socket?.Dispose(); ArrayPool<byte>.Shared.Return(datagram, clearArray: true); }
    }

}

public sealed record AudioUdpSenderSnapshot(long PacketsSent, long BytesSent, long DroppedPackets,
    long SocketErrors, int QueueDepth, ulong StreamId);
