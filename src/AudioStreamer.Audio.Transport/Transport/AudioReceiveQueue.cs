using AudioStreamer.Audio.Transport.Protocol;

namespace AudioStreamer.Audio.Transport.Transport;

/// <summary>Bounded adaptive reorder/playout queue. It owns packet ordering, jitter measurement, and overflow policy.</summary>
public sealed class AudioReceiveQueue : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, QueuedPacket> _packets = [];
    private readonly SemaphoreSlim _available = new(0);
    private readonly int _maximumPackets;
    private readonly TimeSpan _maximumDuration;
    private readonly TimeSpan _minimumLatency;
    private readonly TimeSpan _maximumLatency;
    private readonly TimeSpan _baseLatency;
    private readonly TimeProvider _clock;
    private TimeSpan _buffered;
    private TimeSpan _jitter;
    private TimeSpan _currentLatency;
    private ulong _streamId;
    private uint _expectedSequence;
    private long _firstArrival;
    private long _lastArrival;
    private TimeSpan _lastPacketDuration;
    private long _dropped, _overflows, _duplicates, _reordered, _late, _missing, _recovered, _underruns;
    private bool _initialized, _playoutStarted, _gapObserved, _completed;

    public AudioReceiveQueue(int maximumPackets, TimeSpan maximumDuration, TimeSpan? targetLatency = null,
        TimeSpan? minimumLatency = null, TimeSpan? maximumLatency = null, TimeProvider? clock = null)
    {
        if (maximumPackets is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumPackets));
        if (maximumDuration <= TimeSpan.Zero || maximumDuration > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        _maximumPackets = maximumPackets;
        _maximumDuration = maximumDuration;
        _minimumLatency = minimumLatency ?? TimeSpan.FromMilliseconds(10);
        _maximumLatency = maximumLatency ?? TimeSpan.FromMilliseconds(250);
        _baseLatency = targetLatency ?? TimeSpan.FromMilliseconds(30);
        if (_minimumLatency <= TimeSpan.Zero || _maximumLatency < _minimumLatency || _baseLatency < _minimumLatency || _baseLatency > _maximumLatency)
            throw new ArgumentOutOfRangeException(nameof(targetLatency));
        _currentLatency = _baseLatency;
        _clock = clock ?? TimeProvider.System;
    }

    public AudioReceiveQueueSnapshot Snapshot
    {
        get
        {
            lock (_gate) return new(_packets.Count, _buffered, _dropped, _overflows, _duplicates,
                _reordered, _late, _missing, _recovered, _underruns, _jitter, _currentLatency);
        }
    }

    public bool Offer(AudioProtocolV3Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var duration = TimeSpan.FromSeconds((double)packet.Payload.Length /
            (packet.SampleRate * packet.Channels * AudioProtocolV3Codec.BytesPerSample));
        if (duration > _maximumDuration) { lock (_gate) { _dropped++; _overflows++; } return false; }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            var now = _clock.GetTimestamp();
            if (!_initialized || _streamId != packet.StreamId)
            {
                ClearPackets();
                _initialized = true;
                _playoutStarted = false;
                _streamId = packet.StreamId;
                _expectedSequence = packet.SequenceNumber;
                _firstArrival = now;
                _lastArrival = 0;
                _jitter = TimeSpan.Zero;
                _currentLatency = _baseLatency;
                _gapObserved = false;
            }
            var distance = unchecked(packet.SequenceNumber - _expectedSequence);
            if (distance >= 0x80000000)
            {
                _late++;
                _dropped++;
                return false;
            }
            if (_packets.ContainsKey(packet.SequenceNumber))
            {
                _duplicates++;
                _dropped++;
                return false;
            }
            if (distance > 0) { _reordered++; _gapObserved = true; }
            if (_gapObserved && HasQueuedAfter(distance)) _recovered++;
            UpdateJitter(now, duration);
            while (_packets.Count >= _maximumPackets || _buffered + duration > _maximumDuration)
            {
                if (!RemoveNearestToPlayout()) break;
                _overflows++;
            }
            if (_packets.Count >= _maximumPackets || _buffered + duration > _maximumDuration)
            {
                _dropped++;
                _overflows++;
                return false;
            }
            _packets.Add(packet.SequenceNumber, new QueuedPacket(packet, duration, now));
            _buffered += duration;
            if (_packets.Count == 1) _firstArrival = now;
            _available.Release();
            return true;
        }
    }

    public async ValueTask<AudioProtocolV3Packet?> TakeAsync(CancellationToken cancellationToken)
    {
        var hasWake = false;
        while (true)
        {
            if (!hasWake)
            {
                TimeSpan? idleTimeout;
                lock (_gate) idleTimeout = _playoutStarted ? _currentLatency : null;
                if (idleTimeout is { } timeout)
                {
                    if (!await _available.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
                    {
                        lock (_gate) if (_playoutStarted && _packets.Count == 0) _underruns++;
                        continue;
                    }
                }
                else await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            hasWake = false;
            TimeSpan? delay = null;
            lock (_gate)
            {
                if (_packets.Count == 0)
                {
                    if (_completed) return null;
                    if (_playoutStarted) _underruns++;
                    continue;
                }
                var now = _clock.GetTimestamp();
                var startupElapsed = _clock.GetElapsedTime(_firstArrival, now);
                if (!_playoutStarted && _buffered < _currentLatency && startupElapsed < _currentLatency)
                    delay = _currentLatency - startupElapsed;
                else
                {
                    _playoutStarted = true;
                    if (!_packets.TryGetValue(_expectedSequence, out var next))
                    {
                        var candidate = FindNearestPacket();
                        if (candidate is null) continue;
                        var held = _clock.GetElapsedTime(candidate.Value.Value.ArrivalTimestamp, now);
                        if (held < _currentLatency) delay = _currentLatency - held;
                        else
                        {
                            var missing = unchecked(candidate.Value.Key - _expectedSequence);
                            _missing += missing;
                            _dropped += missing;
                            _expectedSequence = candidate.Value.Key;
                            _gapObserved = false;
                            next = candidate.Value.Value;
                        }
                    }
                    if (delay is null && _packets.Remove(_expectedSequence, out next))
                    {
                        _buffered -= next.Duration;
                        _expectedSequence = unchecked(_expectedSequence + 1);
                        _gapObserved = false;
                        return next.Packet;
                    }
                }
            }
            if (delay is { } wait && wait > TimeSpan.Zero)
            {
                _ = await _available.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
                hasWake = true;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_completed) return ValueTask.CompletedTask;
            _completed = true;
            ClearPackets();
            _available.Release();
        }
        _available.Dispose();
        return ValueTask.CompletedTask;
    }

    private void UpdateJitter(long now, TimeSpan duration)
    {
        if (_lastArrival != 0)
        {
            var arrival = _clock.GetElapsedTime(_lastArrival, now);
            var delta = Math.Abs((arrival - _lastPacketDuration).Ticks);
            _jitter = TimeSpan.FromTicks(_jitter.Ticks + (delta - _jitter.Ticks) / 16);
            var adaptive = _baseLatency + TimeSpan.FromTicks(_jitter.Ticks * 4);
            _currentLatency = adaptive < _minimumLatency ? _minimumLatency : adaptive > _maximumLatency ? _maximumLatency : adaptive;
        }
        _lastArrival = now;
        _lastPacketDuration = duration;
    }

    private KeyValuePair<uint, QueuedPacket>? FindNearestPacket()
    {
        KeyValuePair<uint, QueuedPacket>? nearest = null;
        uint nearestDistance = uint.MaxValue;
        foreach (var item in _packets)
        {
            var distance = unchecked(item.Key - _expectedSequence);
            if (distance < nearestDistance) { nearestDistance = distance; nearest = item; }
        }
        return nearest;
    }

    private bool HasQueuedAfter(uint distance)
    {
        foreach (var sequence in _packets.Keys)
            if (unchecked(sequence - _expectedSequence) > distance &&
                unchecked(sequence - _expectedSequence) < 0x80000000)
                return true;
        return false;
    }

    private bool RemoveNearestToPlayout()
    {
        var nearest = FindNearestPacket();
        if (nearest is null) return false;
        var item = nearest.Value.Value;
        _packets.Remove(nearest.Value.Key);
        _buffered -= item.Duration;
        _dropped++;
        if (nearest.Value.Key == _expectedSequence) _expectedSequence = unchecked(_expectedSequence + 1);
        _ = _available.Wait(0);
        return true;
    }

    private void ClearPackets()
    {
        _packets.Clear();
        _buffered = TimeSpan.Zero;
        while (_available.Wait(0)) { }
    }

    private sealed record QueuedPacket(AudioProtocolV3Packet Packet, TimeSpan Duration, long ArrivalTimestamp);
}

public sealed record AudioReceiveQueueSnapshot(int PacketCount, TimeSpan BufferedDuration, long DroppedPackets,
    long OverflowEvents, long DuplicatePackets = 0, long ReorderedPackets = 0, long LatePackets = 0,
    long MissingPackets = 0, long RecoveredPackets = 0, long Underruns = 0,
    TimeSpan CurrentJitter = default, TimeSpan TargetLatency = default);
