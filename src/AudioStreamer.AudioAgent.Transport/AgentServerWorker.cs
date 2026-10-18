using System.Text.Json;
using System.Diagnostics;
using System.Security.Principal;
using AudioStreamer.AudioAgent.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.AudioAgent.Transport;

public sealed class AgentServerWorker(IAgentServer server, IAgentAudioCommandHandler audioCommands, IHostApplicationLifetime lifetime,
    ILogger<AgentServerWorker> logger) : BackgroundService
{
    private readonly Guid _instanceId = Guid.NewGuid();

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        RunChannelAsync(AgentChannel.Control, stoppingToken),
        RunChannelAsync(AgentChannel.CapturePcm, stoppingToken),
        RunChannelAsync(AgentChannel.PlaybackPcm, stoppingToken));

    private async Task RunChannelAsync(AgentChannel channel, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await server.AcceptAsync(channel, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Audio Agent channel connected. Channel={Channel}", channel);
                if (channel == AgentChannel.Control) await RunControlAsync(connection, cancellationToken).ConfigureAwait(false);
                else if (channel == AgentChannel.CapturePcm)
                    await audioCommands.RunCaptureChannelAsync(connection, cancellationToken).ConfigureAwait(false);
                else await audioCommands.RunPlaybackChannelAsync(connection, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Audio Agent channel disconnected. Channel={Channel}", channel);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (InvalidDataException exception) { logger.LogError(exception, "Audio Agent protocol error. Channel={Channel}", channel); }
            catch (IOException exception) { logger.LogInformation(exception, "Audio Agent client disconnected. Channel={Channel}", channel); }
            catch (Exception exception) { logger.LogError(exception, "Audio Agent channel failed. Channel={Channel}", channel); }
        }
    }

    private async Task RunControlAsync(IAgentConnection connection, CancellationToken cancellationToken)
    {
        var hello = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        ValidateVersion(hello);
        if (hello.MessageType != AgentMessageType.Hello) throw new InvalidDataException("Expected AgentHello.");
        var request = JsonSerializer.Deserialize<AgentHello>(hello.Payload.Span) ?? throw new InvalidDataException("Invalid AgentHello payload.");
        if (request.MinimumVersion < 1 || request.MaximumVersion < request.MinimumVersion ||
            AgentProtocol.CurrentVersion < request.MinimumVersion || AgentProtocol.CurrentVersion > request.MaximumVersion)
        {
            logger.LogError("Audio Agent handshake rejected. MinimumVersion={MinimumVersion} MaximumVersion={MaximumVersion}", request.MinimumVersion, request.MaximumVersion);
            await SendAsync(connection, AgentMessageType.Error, new AgentError("version-mismatch", "No supported Audio Agent protocol version.", false), cancellationToken).ConfigureAwait(false);
            return;
        }
        var version = AgentProtocol.CurrentVersion;
        var identity = CreateIdentity(version, typeof(AgentServerWorker).Assembly.GetName().Version?.ToString() ?? "unknown");
        await SendAsync(connection, AgentMessageType.Ready, identity, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Audio Agent handshake accepted. InstanceId={InstanceId} ProtocolVersion={ProtocolVersion} SessionId={SessionId} ProcessId={ProcessId}", _instanceId, version, identity.SessionId, identity.ProcessId);
        audioCommands.AttachControlConnection(connection, cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            ValidateVersion(frame);
            if (frame.MessageType == AgentMessageType.Heartbeat)
            {
                await connection.SendAsync(frame with { Payload = JsonSerializer.SerializeToUtf8Bytes(new AgentHeartbeat(DateTimeOffset.UtcNow)) }, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (frame.MessageType == AgentMessageType.Shutdown)
            {
                await SendAsync(connection, AgentMessageType.Ready, CreateIdentity(AgentProtocol.CurrentVersion, "shutting-down"), cancellationToken, frame.CorrelationId).ConfigureAwait(false);
                lifetime.StopApplication();
                return;
            }
            if (frame.MessageType is AgentMessageType.DeviceListRequest or AgentMessageType.SelectionRequest or
                AgentMessageType.CaptureStart or AgentMessageType.CaptureStop or AgentMessageType.CapturePause or
                AgentMessageType.CaptureResume or AgentMessageType.PlaybackStart or AgentMessageType.PlaybackStop or
                AgentMessageType.PlaybackPause or AgentMessageType.PlaybackResume)
            {
                try
                {
                    var response = await audioCommands.HandleControlAsync(frame, cancellationToken).ConfigureAwait(false);
                    if (response is not null)
                        await connection.SendAsync(response with { CorrelationId = frame.CorrelationId }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Audio Agent audio operation failed. MessageType={MessageType} CorrelationId={CorrelationId}", frame.MessageType, frame.CorrelationId);
                    await SendAsync(connection, AgentMessageType.Error,
                        new AgentError("audio-operation-failed", "The audio operation could not be completed.", true), cancellationToken, frame.CorrelationId).ConfigureAwait(false);
                }
                continue;
            }
            logger.LogWarning("Audio Agent received unsupported command. MessageType={MessageType}", frame.MessageType);
            await SendAsync(connection, AgentMessageType.Error, new AgentError("unsupported-command", "Command is not available in this milestone.", false), cancellationToken).ConfigureAwait(false);
        }
    }

    private static ValueTask SendAsync<T>(IAgentConnection connection, AgentMessageType type, T payload, CancellationToken token, Guid? correlationId = null) =>
        connection.SendAsync(new AgentFrame(AgentProtocol.CurrentVersion, connection.Channel, type, 0, correlationId ?? Guid.NewGuid(),
            JsonSerializer.SerializeToUtf8Bytes(payload)), token);

    private static void ValidateVersion(AgentFrame frame)
    {
        if (frame.Version != AgentProtocol.CurrentVersion) throw new InvalidDataException($"Unsupported Audio Agent protocol version {frame.Version}.");
    }

    private AgentReady CreateIdentity(int version, string agentVersion)
    {
        using var process = Process.GetCurrentProcess();
        return new AgentReady(version, _instanceId, agentVersion, process.SessionId,
            WindowsIdentity.GetCurrent().User?.Value, process.Id);
    }
}
