using System.Diagnostics;
using System.Collections.Generic;
using AudioStreamer.IPC.Contracts;
using AudioStreamer.IPC.Protocol;
using AudioStreamer.IPC.Transport;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.IPC.Dispatch;

public sealed class RequestDispatcher(IIpcRequestHandler handler, IIpcHandshakeInfoProvider infoProvider,
    IpcServerOptions options, IpcServerState serverState, ILogger<RequestDispatcher> logger)
{
    internal async ValueTask<IpcEnvelope> DispatchAsync(IpcEnvelope request, Guid connectionEpoch,
        IpcClientIdentity identity, IpcClientSubscription subscription, IpcClientRequestRateLimiter rateLimiter,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var requestId = request.RequestId.GetValueOrDefault();
        var requestTag = requestId == Guid.Empty ? "unavailable" : requestId.ToString("N")[..8].ToUpperInvariant();
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = requestTag });
        string resultCode = "ok";
        try
        {
            ValidateEnvelope(request, connectionEpoch);
            if (!rateLimiter.TryConsume(request.MessageType))
            {
                resultCode = IpcErrors.RateLimited;
                return Failure(request, connectionEpoch, IpcErrors.RateLimited,
                    "IPC request rate limit was exceeded.", retryable: true);
            }
            if (serverState.IsStopping && request.MessageType is ("UpdateSettings" or "StartNode" or "StopNode" or
                "StartStream" or "StopStream" or "Pause" or "Resume" or "RestartDiscovery"))
                return Failure(request, connectionEpoch, IpcErrors.ServiceStopping, "Service is stopping and cannot accept this mutation.");
            var timeout = GetTimeout(request.MessageType);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            IpcHandlerResponse response;
            switch (request.MessageType)
            {
                case "Ping":
                case "Heartbeat":
                    if (request.MessageType == "Ping") _ = IpcSerializer.DeserializePayload<PingPayload>(request.Payload);
                    else
                    {
                        _ = IpcSerializer.DeserializePayload<EmptyPayload>(request.Payload);
                        subscription.MarkHeartbeat();
                    }
                    var serverInfo = await infoProvider.GetServerInfoAsync(timeoutSource.Token).ConfigureAwait(false);
                    response = new(request.MessageType == "Ping" ? "PingResult" : "HeartbeatAck",
                        new IpcOperationResult<PingResult>
                        {
                            Result = "ok",
                            Data = new PingResult(DateTimeOffset.UtcNow, serverInfo.ServiceState)
                        });
                    break;
                case "Subscribe":
                    var subscribe = IpcSerializer.DeserializePayload<SubscribeRequest>(request.Payload);
                    IReadOnlyList<string>? requestedKinds = subscribe.EventKinds.IsDefaultOrEmpty ? null : subscribe.EventKinds;
                    if (!subscription.Subscribe(requestedKinds))
                    {
                        resultCode = IpcErrors.InvalidField;
                        return Failure(request, connectionEpoch, IpcErrors.InvalidField, "Subscription filter exceeds its bounds.");
                    }
                    response = new("SubscribeResult", new IpcOperationResult<SubscriptionResult>
                    {
                        Result = "ok",
                        Data = new SubscriptionResult(true)
                    });
                    break;
                case "Unsubscribe":
                    subscription.Unsubscribe();
                    response = new("UnsubscribeResult", new IpcOperationResult<SubscriptionResult>
                    {
                        Result = "ok",
                        Data = new SubscriptionResult(false)
                    });
                    break;
                default:
                    response = await handler.HandleAsync(request.MessageType, request.Payload, identity, timeoutSource.Token)
                        .ConfigureAwait(false);
                    break;
            }

            resultCode = response.ResultCode;
            return IpcEnvelopeFactory.Response(response.MessageType, requestId, connectionEpoch, response.Payload);
        }
        catch (IpcProtocolException exception)
        {
            resultCode = exception.Code;
            return Failure(request, connectionEpoch, exception.Code, exception.SafeMessage);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            resultCode = IpcErrors.OperationTimedOut;
            return Failure(request, connectionEpoch, IpcErrors.OperationTimedOut, "IPC request exceeded its allowed duration.", retryable: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            resultCode = IpcErrors.InternalError;
            logger.LogError(exception,
                "IPC request handler failed. RequestId={RequestId} MessageType={MessageType} ExceptionMessage={ExceptionMessage} ExceptionStackTrace={ExceptionStackTrace}",
                requestId, request.MessageType, exception.Message, exception.StackTrace);
            return Failure(request, connectionEpoch, IpcErrors.InternalError, "Service could not complete the request.", retryable: true);
        }
        finally
        {
            logger.LogInformation("IPC request completed. RequestId={RequestId} MessageType={MessageType} Result={ResultCode} ElapsedMs={ElapsedMs}",
                requestId, request.MessageType, resultCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private static void ValidateEnvelope(IpcEnvelope request, Guid connectionEpoch)
    {
        if (request.SchemaVersion is null || request.Kind != "request" || request.RequestId is null || request.RequestId == Guid.Empty ||
            request.Payload.ValueKind == System.Text.Json.JsonValueKind.Undefined || request.ConnectionEpoch != connectionEpoch ||
            request.SchemaVersion.Major != IpcProtocol.MajorVersion ||
            request.SchemaVersion.Minor > IpcProtocol.MinorVersion || string.IsNullOrWhiteSpace(request.MessageType) ||
            request.MessageType.Length > 64)
            throw new IpcProtocolException(IpcErrors.InvalidRequest, "Request envelope is invalid for this connection.");
    }

    private TimeSpan GetTimeout(string command) => command switch
    {
        "UpdateSettings" => options.MutationTimeout,
        "StartNode" or "StopNode" or "StartStream" or "StopStream" or "Pause" or "Resume" or "RestartDiscovery" => options.LifecycleTimeout,
        _ => options.ReadRequestTimeout
    };

    private static IpcEnvelope Failure(IpcEnvelope request, Guid epoch, string code, string message, bool retryable = false) =>
        IpcEnvelopeFactory.Response(request.MessageType + "Result", request.RequestId.GetValueOrDefault(), epoch,
            new ErrorResponse("error", new IpcError { Code = code, Message = message, Retryable = retryable }));
}
