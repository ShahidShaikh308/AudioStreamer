namespace AudioStreamer.IPC.Protocol;

/// <summary>Stable transport error identifiers; serialized exceptions are never exposed to clients.</summary>
public static class ProtocolErrors
{
    public const string InvalidFrameLength = "InvalidRequest";
    public const string InvalidJson = "InvalidRequest";
    public const string InvalidEnvelope = "InvalidRequest";
    public const string UnsupportedVersion = "IpcVersionUnsupported";
    public const string Unauthorized = "Unauthorized";
    public const string RequestTimedOut = "OperationTimedOut";
    public const string InternalFailure = "InternalError";
}
