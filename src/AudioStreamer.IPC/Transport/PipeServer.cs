using System.IO.Pipes;
using AudioStreamer.IPC.Contracts;
using AudioStreamer.IPC.Dispatch;
using Microsoft.Extensions.Logging;
using AudioStreamer.IPC.Protocol;

namespace AudioStreamer.IPC.Transport;

public sealed class PipeServer : IAsyncDisposable
{
    private readonly RequestDispatcher _dispatcher;
    private readonly VersionHandshake _handshake;
    private readonly EventPublisher _events;
    private readonly IpcServerOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PipeServer> _logger;
    private readonly ConnectionManager _connections;
    private readonly IpcServerState _serverState;
    private readonly SemaphoreSlim _clientSlots;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptTask;

    public PipeServer(RequestDispatcher dispatcher, VersionHandshake handshake, EventPublisher events, ConnectionManager connections,
        IpcServerState serverState,
        IpcServerOptions options, ILoggerFactory loggerFactory, ILogger<PipeServer> logger)
    {
        _dispatcher = dispatcher;
        _handshake = handshake;
        _events = events;
        _connections = connections;
        _serverState = serverState;
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _options.Validate();
        _clientSlots = new SemaphoreSlim(_options.MaximumClients, _options.MaximumClients);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_acceptTask is not null) throw new InvalidOperationException("IPC server is already started.");
        cancellationToken.ThrowIfCancellationRequested();
        _lifetime = new CancellationTokenSource();
        _serverState.MarkRunning();
        _logger.LogInformation("Starting local IPC server. PipeName={PipeName}", _options.PipeName);
        _acceptTask = AcceptLoopAsync(_lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifetime is null) return;
        _serverState.BeginStopping();
        await _events.PublishAsync("ServiceStopping", new { State = "stopping" }, CancellationToken.None).ConfigureAwait(false);
        _lifetime.Cancel();
        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        var sessions = _connections.Sessions.ToArray();
        if (sessions.Length > 0)
        {
            var completion = Task.WhenAll(sessions.Select(session => session.Completion));
            var timeout = Task.Delay(_options.ShutdownDrainTimeout, cancellationToken);
            if (await Task.WhenAny(completion, timeout).ConfigureAwait(false) != completion)
            {
                foreach (var session in sessions) session.Abort("Service IPC shutdown deadline elapsed.");
                try { await completion.WaitAsync(_options.ShutdownDrainTimeout).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Some IPC sessions did not finish before the final shutdown deadline.");
                }
            }
            else
            {
                try { await completion.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        }

        _lifetime.Dispose();
        _lifetime = null;
        _acceptTask = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            var slotReserved = false;
            try
            {
                await _clientSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                slotReserved = true;
                pipe = WindowsPipeFactory.CreateServer(_options.PipeName, _options.MaximumClients, _options.PipeBufferSizeBytes);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var session = new ClientSession(Guid.NewGuid(), pipe, _dispatcher, _handshake,
                    _options, _loggerFactory.CreateLogger<ClientSession>(), RemoveSession);
                pipe = null;
                if (!_connections.TryAdd(session)) throw new InvalidOperationException("Connection limit reservation was lost.");
                session.Start();
                slotReserved = false;
                retryDelay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (slotReserved) _clientSlots.Release();
                if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "IPC listener failed; the listener will retry without stopping the Engine.");
                if (slotReserved) _clientSlots.Release();
                if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
                try { await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }
    }

    private void RemoveSession(ClientSession session)
    {
        if (_connections.Remove(session.ConnectionId, out _)) _clientSlots.Release();
    }
}
