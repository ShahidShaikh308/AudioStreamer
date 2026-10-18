using System.Diagnostics;
using AudioStreamer.AudioAgent.Contracts.Audio;

namespace AudioStreamer.AudioAgent.Audio.Clock;

/// <summary>Agent-local monotonic clock backed by the standard .NET Stopwatch.</summary>
public sealed class StopwatchAudioClock : IAudioClock
{
    public long GetTimestampTicks() => Stopwatch.GetTimestamp();
    public TimeSpan GetElapsed(long earlierTimestampTicks, long laterTimestampTicks) =>
        Stopwatch.GetElapsedTime(earlierTimestampTicks, laterTimestampTicks);
    public double CalculateDriftPartsPerMillion(long sourceFrames, long targetFrames, int sampleRate)
    {
        if (sourceFrames <= 0 || targetFrames < 0 || sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceFrames));
        return ((double)targetFrames / sourceFrames - 1d) * 1_000_000d;
    }
}
