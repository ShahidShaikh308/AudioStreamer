using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;
using AudioStreamer.Engine.Contracts;

namespace AudioStreamer.Platform.Windows;

/// <summary>Windows UDP adapter for Control Protocol datagrams. Protocol policy stays in Engine.</summary>
public sealed class WindowsControlTransport : IControlTransport
{
    private const int EventQueueCapacity = 1024;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Channel<ControlTransportEvent> _events = Channel.CreateBounded<ControlTransportEvent>(
        new BoundedChannelOptions(EventQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private CancellationTokenSource? _lifetime;
    private Channel<OutboundDatagram>? _outbound;
    private UdpClient? _socket;
    private Task[] _workers = [];
    private bool _networkChangeSubscribed;
    private bool _disposed;
    private int _disposeStarted;

    public IAsyncEnumerable<ControlTransportEvent> ReadEventsAsync(CancellationToken cancellationToken) =>
        _events.Reader.ReadAllAsync(cancellationToken);

    public async Task StartAsync(ControlTransportOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.BindAddress) || options.Port is < 1 or > 65535 ||
            options.OutboundQueueCapacity is < 8 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(options));

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_socket is not null) return;

            var socket = new UdpClient(AddressFamily.InterNetwork);
            try
            {
                socket.EnableBroadcast = true;
                socket.Client.Bind(new IPEndPoint(IPAddress.Parse(options.BindAddress), options.Port));
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            _socket = socket;
            _lifetime = new CancellationTokenSource();
            _outbound = Channel.CreateBounded<OutboundDatagram>(new BoundedChannelOptions(options.OutboundQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            _networkChangeSubscribed = true;
            _workers = [ReceiveLoopAsync(socket, _lifetime.Token), SendLoopAsync(socket, _outbound.Reader, _lifetime.Token)];
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<int> SendAsync(ReadOnlyMemory<byte> payload, ControlTransportEndpoint destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var outbound = _outbound ?? throw new InvalidOperationException("Control transport is not running.");
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await outbound.Writer.WriteAsync(new OutboundDatagram(payload.ToArray(), ToEndPoint(destination), completion),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new ControlTransportException("send", "TransportStopped");
        }
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task QueueAsync(ReadOnlyMemory<byte> payload, ControlTransportEndpoint destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var outbound = _outbound ?? throw new InvalidOperationException("Control transport is not running.");
        try
        {
            await outbound.Writer.WriteAsync(new OutboundDatagram(payload.ToArray(), ToEndPoint(destination), null),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new ControlTransportException("send", "TransportStopped");
        }
    }

    public bool TrySend(ReadOnlyMemory<byte> payload, ControlTransportEndpoint destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var outbound = _outbound;
        if (outbound is null) return false;
        try
        {
            return outbound.Writer.TryWrite(new OutboundDatagram(payload.ToArray(), ToEndPoint(destination), null));
        }
        catch (ChannelClosedException) { return false; }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken; // Once accepted, shutdown owns socket and worker cleanup.
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_socket is null) return;
            if (_networkChangeSubscribed)
            {
                NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
                _networkChangeSubscribed = false;
            }
            _lifetime?.Cancel();
            _outbound?.Writer.TryComplete();
            var outbound = _outbound;
            if (outbound is not null)
                while (outbound.Reader.TryRead(out var pending))
                    pending.Completion?.TrySetException(new ControlTransportException("send", "TransportStopped"));
            var socket = Interlocked.Exchange(ref _socket, null);
            socket?.Dispose();
            try { await Task.WhenAll(_workers).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime?.IsCancellationRequested == true) { }
            catch (ObjectDisposedException) { }
            _workers = [];
            _outbound = null;
            _lifetime?.Dispose();
            _lifetime = null;
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _events.Writer.TryComplete();
        }
        finally
        {
            _lifecycle.Release();
            _lifecycle.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(UdpClient socket, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var datagram = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                await _events.Writer.WriteAsync(new ControlDatagramReceived(datagram.Buffer,
                    new ControlTransportEndpoint(datagram.RemoteEndPoint.Address.ToString(), datagram.RemoteEndPoint.Port)),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await PublishFaultAsync("receive", exception.SocketErrorCode.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await PublishFaultAsync("receive", exception.GetType().Name, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendLoopAsync(UdpClient socket, ChannelReader<OutboundDatagram> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var sent = await socket.SendAsync(item.Payload, item.Destination, cancellationToken).ConfigureAwait(false);
                    if (sent != item.Payload.Length)
                        throw new SocketException((int)SocketError.MessageSize);
                    item.Completion?.TrySetResult(sent);
                    await _events.Writer.WriteAsync(new ControlDatagramSent(sent), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    item.Completion?.TrySetCanceled(cancellationToken);
                    break;
                }
                catch (SocketException exception)
                {
                    item.Completion?.TrySetException(new ControlTransportException("send", exception.SocketErrorCode.ToString()));
                    await PublishFaultAsync("send", exception.SocketErrorCode.ToString(), cancellationToken).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    item.Completion?.TrySetException(new ControlTransportException("send", "SocketClosed"));
                    if (!cancellationToken.IsCancellationRequested)
                        await PublishFaultAsync("send", "SocketClosed", cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private ValueTask PublishFaultAsync(string operation, string errorCode, CancellationToken cancellationToken) =>
        _events.Writer.WriteAsync(new ControlTransportFault(operation, errorCode, true), cancellationToken);

    private void OnNetworkAddressChanged(object? sender, EventArgs args) =>
        _events.Writer.TryWrite(new ControlNetworkAvailabilityChanged());

    private static IPEndPoint ToEndPoint(ControlTransportEndpoint endpoint) =>
        new(IPAddress.Parse(endpoint.Address), endpoint.Port);

    private sealed record OutboundDatagram(byte[] Payload, IPEndPoint Destination, TaskCompletionSource<int>? Completion);
}

