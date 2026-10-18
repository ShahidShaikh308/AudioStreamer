using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using AudioStreamer.AudioAgent.Contracts;

namespace AudioStreamer.AudioAgent.Transport;

public sealed class NamedPipeAgentTransport(AgentTransportOptions options) : IAgentTransport
{
    public async ValueTask<IAgentConnection> ConnectAsync(AgentChannel channel, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", options.GetPipeName(channel), PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new StreamAgentConnection(channel, pipe);
        }
        catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
    }
}

public sealed class NamedPipeAgentServer(AgentTransportOptions options) : IAgentServer
{
    private readonly CancellationTokenSource _lifetime = new();

    public async ValueTask<IAgentConnection> AcceptAsync(AgentChannel channel, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var access = PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance;
        security.AddAccessRule(new PipeAccessRule(new NTAccount(options.ServiceIdentity), access, AccessControlType.Allow));
        if (WindowsIdentity.GetCurrent().User is { } currentUser)
            security.AddAccessRule(new PipeAccessRule(currentUser, access, AccessControlType.Allow));
        var pipe = NamedPipeServerStreamAcl.Create(options.GetPipeName(channel), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
        try
        {
            await pipe.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
            return new StreamAgentConnection(channel, pipe);
        }
        catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class StreamAgentConnection : IAgentConnection
{
    private readonly Stream _stream;
    private readonly Channel<PendingWrite> _writes = System.Threading.Channels.Channel.CreateBounded<PendingWrite>(new BoundedChannelOptions(16)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly CancellationTokenSource _writerLifetime = new();
    private readonly Task _writer;
    private int _disposed;
    public StreamAgentConnection(AgentChannel channel, Stream stream)
    {
        Channel = channel;
        _stream = stream;
        // Pipe writes can be triggered by an IPC request carrying a caller execution context.
        // Keep the pipe I/O on a worker created without flowing that context, so a caller's
        // restricted impersonation token cannot break service-to-agent writes.
        using (ExecutionContext.SuppressFlow())
            _writer = Task.Run(WriteLoopAsync);
    }

    public AgentChannel Channel { get; }
    public bool IsConnected
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            try { return _stream is PipeStream pipe ? pipe.IsConnected : true; }
            catch (ObjectDisposedException) { return false; }
            catch (IOException) { return false; }
        }
    }

    public async ValueTask SendAsync(AgentFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Channel != Channel) throw new InvalidDataException("Frame channel does not match connection channel.");
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _writes.Writer.WriteAsync(new PendingWrite(frame, cancellationToken, completion), cancellationToken).ConfigureAwait(false);
        await completion.Task.ConfigureAwait(false);
    }

    private async Task WriteLoopAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (var pending in _writes.Reader.ReadAllAsync(_writerLifetime.Token).ConfigureAwait(false))
            {
                if (pending.CancellationToken.IsCancellationRequested)
                {
                    pending.Completion.TrySetCanceled(pending.CancellationToken);
                    continue;
                }

                using var writeLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                    pending.CancellationToken, _writerLifetime.Token);
                try
                {
                    await AgentProtocolCodec.WriteAsync(_stream, pending.Frame, writeLifetime.Token).ConfigureAwait(false);
                    pending.Completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    pending.Completion.TrySetException(exception);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (_writerLifetime.IsCancellationRequested) { }
        catch (Exception exception) { failure = exception; }
        finally
        {
            failure ??= new IOException("Audio Agent channel closed before the frame was written.");
            while (_writes.Reader.TryRead(out var pending)) pending.Completion.TrySetException(failure);
            _writes.Writer.TryComplete(failure);
        }
    }

    public ValueTask<AgentFrame> ReceiveAsync(CancellationToken cancellationToken) => AgentProtocolCodec.ReadAsync(_stream, Channel, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _writes.Writer.TryComplete();
        await _writerLifetime.CancelAsync().ConfigureAwait(false);
        try { await _stream.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { await _writer.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            finally { _writerLifetime.Dispose(); }
        }
    }

    private sealed record PendingWrite(AgentFrame Frame, CancellationToken CancellationToken,
        TaskCompletionSource Completion);
}
