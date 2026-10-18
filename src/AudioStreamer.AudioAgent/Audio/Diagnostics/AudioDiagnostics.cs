using System.Collections.Immutable;
using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.AudioAgent.Contracts.Audio;

namespace AudioStreamer.AudioAgent.Audio.Diagnostics;

/// <summary>Agent-owned bounded aggregate diagnostics. It stores safe error codes only.</summary>
public sealed class AudioDiagnostics : IAudioDiagnostics, IAudioLatencyProvider
{
    private readonly object _gate = new();
    private readonly Queue<string> _errors = new();
    private string _captureState = "Stopped", _playbackState = "Stopped";
    private long _captured, _played, _dropped, _underruns, _overruns;
    private AudioLatencySnapshot _latency = new(TimeSpan.Zero, TimeSpan.Zero, 0);

    public AudioDiagnosticsSnapshot GetSnapshot()
    {
        lock (_gate) return new(_captureState, _playbackState, Interlocked.Read(ref _captured), Interlocked.Read(ref _played),
            Interlocked.Read(ref _dropped), Interlocked.Read(ref _underruns), Interlocked.Read(ref _overruns), _latency, _errors.ToImmutableArray());
    }
    public AudioLatencySnapshot GetLatency() { lock (_gate) return _latency; }
    internal void SetCaptureState(string value) { lock (_gate) _captureState = value; }
    internal void SetPlaybackState(string value) { lock (_gate) _playbackState = value; }
    internal void AddCaptured(long frames) => Interlocked.Add(ref _captured, frames);
    internal void AddPlayed(long frames) => Interlocked.Add(ref _played, frames);
    internal void AddDropped(long frames) => Interlocked.Add(ref _dropped, frames);
    internal void AddUnderrun() => Interlocked.Increment(ref _underruns);
    internal void AddOverrun() => Interlocked.Increment(ref _overruns);
    internal void SetLatency(AudioLatencySnapshot latency) { lock (_gate) _latency = latency; }
    internal void AddSafeError(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64) return;
        lock (_gate)
        {
            if (_errors.Count == 16) _errors.Dequeue();
            _errors.Enqueue(code);
        }
    }
}
