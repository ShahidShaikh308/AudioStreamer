using System.Buffers;
using System.Net;
using System.Net.Sockets;
using AudioStreamer.Audio.Transport.Protocol;

namespace AudioStreamer.Audio.Transport.Transport;

/// <summary>Owns a cancellable UDP receive loop and bounded parsed-packet queue.</summary>
public sealed class AudioUdpReceiver : IAsyncDisposable
{
    private readonly IPEndPoint _bind;
    private readonly AudioReceiveQueue _queue;
    private readonly object _routeGate = new();
    private readonly Dictionary<IPAddress, AudioReceiveQueue> _routes = [];
    private readonly int _queueCapacity;
    private readonly TimeSpan _bufferDuration;
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private TaskCompletionSource<bool>? _bound;
    private Socket? _socket;
    private long _packets, _bytes, _invalid, _dropped, _socketErrors, _reconnects;
    private long _lastPacketUtcTicks;
    private int _started, _disposed;

    public AudioUdpReceiver(IPEndPoint bind, int queueCapacity, TimeSpan bufferDuration)
    {
        ArgumentNullException.ThrowIfNull(bind);
        if (bind.Port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(bind));
        _bind = bind;
        _queueCapacity = queueCapacity;
        _bufferDuration = bufferDuration;
        _queue = new AudioReceiveQueue(queueCapacity, bufferDuration);
    }

    public AudioReceiveQueue Queue => _queue;
    public IPEndPoint? BoundEndpoint => Volatile.Read(ref _socket)?.LocalEndPoint as IPEndPoint;
    public AudioUdpReceiverSnapshot Snapshot
    {
        get
        {
            AudioReceiveQueueSnapshot[] queued;
            lock (_routeGate) queued = _routes.Values.Select(route => route.Snapshot).Append(_queue.Snapshot).ToArray();
            var lastTicks = Interlocked.Read(ref _lastPacketUtcTicks);
            return new(Interlocked.Read(ref _packets), Interlocked.Read(ref _bytes), Interlocked.Read(ref _invalid),
                Interlocked.Read(ref _dropped) + queued.Sum(snapshot => snapshot.DroppedPackets), Interlocked.Read(ref _socketErrors),
                Interlocked.Read(ref _reconnects), TimeSpan.FromTicks(queued.Sum(snapshot => snapshot.BufferedDuration.Ticks)),
                queued.Sum(snapshot => snapshot.PacketCount),
                lastTicks == 0 ? null : new DateTimeOffset(lastTicks, TimeSpan.Zero));
        }
    }

    /// <summary>Registers one receiving route per remote source address; v3 does not carry a route identifier.</summary>
    public AudioReceiveQueue RegisterSource(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_routeGate)
        {
            if (_routes.ContainsKey(address)) throw new InvalidOperationException("A receive route is already registered for this peer address.");
            var queue = new AudioReceiveQueue(_queueCapacity, _bufferDuration);
            _routes.Add(address, queue);
            return queue;
        }
    }

    public void UpdateSource(IPAddress previousAddress, IPAddress currentAddress, AudioReceiveQueue queue)
    {
        ArgumentNullException.ThrowIfNull(previousAddress);
        ArgumentNullException.ThrowIfNull(currentAddress);
        ArgumentNullException.ThrowIfNull(queue);
        lock (_routeGate)
        {
            if (!_routes.TryGetValue(previousAddress, out var current) || !ReferenceEquals(current, queue))
                throw new InvalidOperationException("The receive route is no longer registered.");
            if (previousAddress.Equals(currentAddress)) return;
            if (_routes.ContainsKey(currentAddress)) throw new InvalidOperationException("A receive route is already registered for the new peer address.");
            _routes.Remove(previousAddress);
            _routes.Add(currentAddress, queue);
        }
    }

    public async ValueTask UnregisterSourceAsync(IPAddress address, AudioReceiveQueue queue)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(queue);
        var removed = false;
        lock (_routeGate)
        {
            if (_routes.TryGetValue(address, out var current) && ReferenceEquals(current, queue))
            { _routes.Remove(address); removed = true; }
        }
        if (removed) await queue.DisposeAsync().ConfigureAwait(false);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _bound = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _lifetime = new CancellationTokenSource();
        _worker = ReceiveLoopAsync(_lifetime.Token);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Start();
        try
        {
            await (_bound?.Task ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 0) != 0)
        {
            _lifetime?.Cancel();
            Interlocked.Exchange(ref _socket, null)?.Dispose();
        }

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
        AudioReceiveQueue[] routes;
        lock (_routeGate) { routes = _routes.Values.ToArray(); _routes.Clear(); }
        foreach (var route in routes) await route.DisposeAsync().ConfigureAwait(false);
        await _queue.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(AudioProtocolV3Codec.MaximumDatagramSize + 1);
        var backoff = TimeSpan.FromMilliseconds(250);
        EndPoint remote = _bind.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var socket = new Socket(_bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                    Interlocked.Exchange(ref _socket, socket)?.Dispose();
                    socket.Bind(_bind);
                    _bound?.TrySetResult(true);
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var received = await socket.ReceiveFromAsync(buffer.AsMemory(), SocketFlags.None,
                            remote, cancellationToken).ConfigureAwait(false);
                        Interlocked.Add(ref _bytes, received.ReceivedBytes);
                        try
                        {
                            var packet = AudioProtocolV3Codec.Parse(buffer.AsSpan(0, received.ReceivedBytes));
                            var source = (IPEndPoint)received.RemoteEndPoint;
                            AudioReceiveQueue target;
                            lock (_routeGate)
                            {
                                if (_routes.Count == 0) target = _queue;
                                else if (!_routes.TryGetValue(source.Address, out target!))
                                { Interlocked.Increment(ref _dropped); continue; }
                            }
                            Interlocked.Increment(ref _packets);
                            Interlocked.Exchange(ref _lastPacketUtcTicks, DateTimeOffset.UtcNow.UtcDateTime.Ticks);
                            try { _ = target.Offer(packet); }
                            catch (ObjectDisposedException) { Interlocked.Increment(ref _dropped); }
                        }
                        catch (AudioProtocolException) { Interlocked.Increment(ref _invalid); }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (SocketException) when (!cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _socketErrors);
                    Interlocked.Increment(ref _reconnects);
                    await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                    backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 5000));
                }
                finally { Interlocked.Exchange(ref _socket, null)?.Dispose(); }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}

public sealed record AudioUdpReceiverSnapshot(long PacketsReceived, long BytesReceived, long InvalidPackets,
    long DroppedPackets, long SocketErrors, long Reconnects, TimeSpan BufferedDuration, int QueueDepth,
    DateTimeOffset? LastPacketReceivedAt);
