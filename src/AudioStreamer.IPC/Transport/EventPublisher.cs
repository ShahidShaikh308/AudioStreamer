using AudioStreamer.IPC.Contracts;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.IPC.Transport;

/// <summary>Transport fan-out only. Engine events are supplied by the Service bridge; no runtime state is stored here.</summary>
public sealed class EventPublisher(ConnectionManager connections, ILogger<EventPublisher> logger)
{
    private long _sequence;

    public long CurrentSequence => Interlocked.Read(ref _sequence);

    public ValueTask PublishAsync<T>(string messageType, T payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sequence = Interlocked.Increment(ref _sequence);
        var disconnected = false;
        foreach (var session in connections.Sessions)
        {
            if (!session.Subscription.Accepts(messageType)) continue;
            var envelope = IpcEnvelopeFactory.Event(messageType, sequence, session.ConnectionEpoch, payload);
            if (!session.TryQueueEvent(envelope))
            {
                disconnected = true;
                session.Abort("Outbound event queue capacity was exceeded.");
            }
        }

        if (disconnected)
            logger.LogWarning("An IPC subscriber exceeded its bounded event queue and was disconnected.");
        return ValueTask.CompletedTask;
    }
}
