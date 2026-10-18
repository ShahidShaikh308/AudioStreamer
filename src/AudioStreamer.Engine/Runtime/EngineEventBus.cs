using System.Threading.Channels;
using AudioStreamer.Engine.Contracts;

namespace AudioStreamer.Engine.Runtime;

/// <summary>Serializes immutable Engine facts and delivers them through bounded asynchronous subscriptions.</summary>
internal sealed class EngineEventBus : IAsyncDisposable
{
    private readonly Guid _engineInstanceId;
    private readonly int _defaultCapacity;
    private readonly TimeSpan _slowSubscriberTimeout;
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly object _subscriptionsGate = new();
    private readonly List<Subscription> _subscriptions = [];
    private long _sequence;
    private bool _completed;

    public EngineEventBus(Guid engineInstanceId, EngineOptions options)
    {
        _engineInstanceId = engineInstanceId;
        _defaultCapacity = options.DefaultEventCapacity;
        _slowSubscriberTimeout = options.EffectiveEventBackpressureTimeout;
    }

    public long CurrentSequence => Interlocked.Read(ref _sequence);

    public Subscription Subscribe(int capacity = 0, bool critical = false)
    {
        var selectedCapacity = capacity == 0 ? _defaultCapacity : capacity;
        if (selectedCapacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(capacity));
        lock (_subscriptionsGate)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            var subscription = new Subscription(this, selectedCapacity, critical);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    public async ValueTask<EngineDomainEvent> PublishAsync(string source, string kind, EngineStatus status,
        EngineEventPayload payload, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(payload);

        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_subscriptionsGate) ObjectDisposedException.ThrowIf(_completed, this);
            var message = new EngineDomainEvent(_engineInstanceId, Interlocked.Increment(ref _sequence), status.Revision,
                DateTimeOffset.UtcNow, source, kind, status, payload);
            Subscription[] subscribers;
            lock (_subscriptionsGate) subscribers = _subscriptions.ToArray();

            foreach (var subscriber in subscribers)
            {
                try
                {
                    var write = subscriber.Writer.WriteAsync(message, cancellationToken);
                    if (subscriber.IsCritical)
                        await write.ConfigureAwait(false);
                    else
                        await write.AsTask().WaitAsync(_slowSubscriberTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Remove(subscriber, new InvalidOperationException("Event subscriber exceeded its bounded backpressure deadline."));
                }
                catch (ChannelClosedException) { Remove(subscriber, null); }
            }
            return message;
        }
        finally { _publishGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _publishGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Subscription[] subscriptions;
            lock (_subscriptionsGate)
            {
                if (_completed) return;
                _completed = true;
                subscriptions = _subscriptions.ToArray();
                _subscriptions.Clear();
            }
            foreach (var subscription in subscriptions) subscription.Complete();
        }
        finally
        {
            _publishGate.Release();
        }
    }

    private void Remove(Subscription subscription, Exception? error)
    {
        lock (_subscriptionsGate) _subscriptions.Remove(subscription);
        subscription.Complete(error);
    }

    internal sealed class Subscription : IAsyncDisposable
    {
        private readonly EngineEventBus _owner;
        private readonly Channel<EngineDomainEvent> _channel;
        private int _closed;

        internal Subscription(EngineEventBus owner, int capacity, bool critical)
        {
            _owner = owner;
            IsCritical = critical;
            _channel = Channel.CreateBounded<EngineDomainEvent>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
        }

        internal bool IsCritical { get; }
        internal ChannelWriter<EngineDomainEvent> Writer => _channel.Writer;
        public IAsyncEnumerable<EngineDomainEvent> ReadAllAsync(CancellationToken cancellationToken) =>
            _channel.Reader.ReadAllAsync(cancellationToken);

        internal void Complete(Exception? error = null)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0) _channel.Writer.TryComplete(error);
        }

        public ValueTask DisposeAsync()
        {
            _owner.Remove(this, null);
            return ValueTask.CompletedTask;
        }
    }
}
