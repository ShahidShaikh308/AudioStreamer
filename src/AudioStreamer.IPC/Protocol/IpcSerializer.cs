using System.Text.Json;
using AudioStreamer.IPC.Contracts;

namespace AudioStreamer.IPC.Protocol;

public static class IpcSerializer
{
    public static T DeserializePayload<T>(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new IpcProtocolException(IpcErrors.InvalidRequest, "Message payload must be a JSON object.");
        try
        {
            return payload.Deserialize<T>(IpcProtocol.JsonOptions)
                   ?? throw new JsonException("Payload was null.");
        }
        catch (JsonException)
        {
            throw new IpcProtocolException(IpcErrors.InvalidField, "Message payload does not match its schema.");
        }
    }
}
