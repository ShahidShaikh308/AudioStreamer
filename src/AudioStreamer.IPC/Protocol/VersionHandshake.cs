using AudioStreamer.IPC.Contracts;
using AudioStreamer.IPC.Dispatch;
using AudioStreamer.IPC.Transport;
using System.Collections.Immutable;

namespace AudioStreamer.IPC.Protocol;

public sealed class VersionHandshake(IIpcHandshakeInfoProvider infoProvider)
{
    public async ValueTask<(bool Accepted, Guid ConnectionEpoch, IpcEnvelope Response)> NegotiateAsync(
        IpcEnvelope request, IpcClientIdentity identity, CancellationToken cancellationToken)
    {
        if (request.Kind != "request" || request.MessageType != "Hello" || request.RequestId is null || request.RequestId == Guid.Empty)
            return Reject(request, IpcErrors.InvalidRequest, "The first message must be a correlated Hello request.");

        HelloRequest hello;
        try { hello = IpcSerializer.DeserializePayload<HelloRequest>(request.Payload); }
        catch (IpcProtocolException exception) { return Reject(request, exception.Code, exception.SafeMessage); }

        if (hello.SupportedVersions.IsDefault || hello.SupportedVersions.Length is < 1 or > IpcProtocol.MaximumHelloVersions ||
            string.IsNullOrWhiteSpace(hello.ClientVersion) || hello.ClientVersion.Length > 64 ||
            hello.ProcessId <= 0 || hello.RequestedRole != "interactive")
            return Reject(request, IpcErrors.InvalidField, "Hello request contains invalid version, client, process, or role fields.");

        var selected = hello.SupportedVersions
            .Where(version => version.Major == IpcProtocol.MajorVersion && version.Minor >= 0 && version.Minor <= IpcProtocol.MinorVersion)
            .OrderByDescending(version => version.Minor)
            .FirstOrDefault();
        if (selected is null)
            return Reject(request, IpcErrors.IpcVersionUnsupported, "No compatible IPC schema version is available.");

        var server = await infoProvider.GetServerInfoAsync(cancellationToken).ConfigureAwait(false);
        var epoch = Guid.NewGuid();
        var payload = new HelloResult
        {
            SelectedVersion = selected,
            ServiceVersion = server.ServiceVersion,
            EngineVersion = server.EngineVersion,
            MinimumSupportedClientVersion = server.MinimumClientVersion,
            Capabilities = server.Capabilities.ToImmutableArray(),
            ConnectionEpoch = epoch,
            ServiceState = server.ServiceState,
            NodeState = server.NodeState
        };
        return (true, epoch, IpcEnvelopeFactory.Response("HelloResult", request.RequestId!.Value, epoch, payload, selected));
    }

    private static (bool Accepted, Guid ConnectionEpoch, IpcEnvelope Response) Reject(IpcEnvelope request, string code, string message)
    {
        var epoch = Guid.Empty;
        var id = request.RequestId.GetValueOrDefault();
        var payload = new ErrorResponse("error", new IpcError { Code = code, Message = message });
        return (false, epoch, IpcEnvelopeFactory.Response("HelloResult", id, epoch, payload));
    }
}
