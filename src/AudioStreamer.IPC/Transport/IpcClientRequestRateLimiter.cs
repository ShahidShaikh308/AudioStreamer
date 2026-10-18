namespace AudioStreamer.IPC.Transport;

internal sealed class IpcClientRequestRateLimiter
{
    private readonly TokenBucket _reads;
    private readonly TokenBucket _mutations;

    public IpcClientRequestRateLimiter(IpcServerOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var clock = timeProvider ?? TimeProvider.System;
        _reads = new TokenBucket(options.ReadRateLimitCapacity, options.ReadRateLimitTokensPerSecond, clock);
        _mutations = new TokenBucket(options.MutationRateLimitCapacity, options.MutationRateLimitTokensPerSecond, clock);
    }

    public bool TryConsume(string messageType) =>
        (IpcRequestClassifier.IsMutation(messageType) ? _mutations : _reads).TryConsume();

    private sealed class TokenBucket
    {
        private readonly object _sync = new();
        private readonly int _capacity;
        private readonly int _tokensPerSecond;
        private readonly TimeProvider _clock;
        private double _tokens;
        private long _lastTimestamp;

        public TokenBucket(int capacity, int tokensPerSecond, TimeProvider clock)
        {
            _capacity = capacity;
            _tokensPerSecond = tokensPerSecond;
            _clock = clock;
            _tokens = capacity;
            _lastTimestamp = clock.GetTimestamp();
        }

        public bool TryConsume()
        {
            lock (_sync)
            {
                var now = _clock.GetTimestamp();
                var elapsed = _clock.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
                if (elapsed > 0)
                    _tokens = Math.Min(_capacity, _tokens + elapsed * _tokensPerSecond);
                _lastTimestamp = now;
                if (_tokens < 1) return false;
                _tokens -= 1;
                return true;
            }
        }
    }
}

internal static class IpcRequestClassifier
{
    public static bool IsMutation(string messageType) => messageType is
        "UpdateSettings" or "StartNode" or "StopNode" or "StartStream" or "StopStream" or
        "Pause" or "Resume" or "RestartDiscovery" or "SelectAudioDevices" or "BeginUpdateCheck" or
        "Subscribe" or "Unsubscribe";
}
