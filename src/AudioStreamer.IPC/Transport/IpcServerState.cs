namespace AudioStreamer.IPC.Transport;

/// <summary>Transport availability only; Engine state remains exclusively owned by IEngine.</summary>
public sealed class IpcServerState
{
    private int _stopping;
    public bool IsStopping => Volatile.Read(ref _stopping) != 0;
    internal void BeginStopping() => Interlocked.Exchange(ref _stopping, 1);
    internal void MarkRunning() => Interlocked.Exchange(ref _stopping, 0);
}
