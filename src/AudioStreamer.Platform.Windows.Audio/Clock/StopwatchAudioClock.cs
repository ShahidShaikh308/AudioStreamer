using System.Diagnostics;
using AudioStreamer.Engine.Contracts;

namespace AudioStreamer.Platform.Windows.Audio.Clock;

/// <summary>Monotonic QPC-derived clock. Values are local to this process and never serialized.</summary>
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
