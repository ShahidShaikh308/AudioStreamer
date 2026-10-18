using System.Threading.Channels;
using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.AudioAgent.Contracts.Audio;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.AudioAgent;

/// <summary>Bounded, callback-safe handoff for Agent-side audio metadata events.</summary>
public sealed class AgentEventPublisher : IAudioPlatformEventPublisher
{
    public AgentEventPublisher(ILogger<AgentEventPublisher> logger) => ArgumentNullException.ThrowIfNull(logger);

    private readonly Channel<AudioPlatformEvent> _events = Channel.CreateBounded<AudioPlatformEvent>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private long _dropped;

    public long DroppedEvents => Interlocked.Read(ref _dropped);

    public ValueTask PublishAsync(AudioPlatformEvent audioEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_events.Writer.TryWrite(audioEvent))
        {
            Interlocked.Increment(ref _dropped);
        }
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<AudioPlatformEvent> ReadAllAsync(CancellationToken cancellationToken) =>
        _events.Reader.ReadAllAsync(cancellationToken);
}
