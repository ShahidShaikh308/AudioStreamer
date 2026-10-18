using AudioStreamer.Engine.Contracts;

namespace AudioStreamer.Platform.Windows.Audio.Formats;

/// <summary>Allocation-free saturating sum for equal-length float PCM spans.</summary>
public sealed class PcmMixer : IAudioMixer
{
    public int Mix(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<float> destination, float leftGain = 1, float rightGain = 1)
    {
        var count = Math.Min(Math.Min(left.Length, right.Length), destination.Length);
        for (var index = 0; index < count; index++)
            destination[index] = Math.Clamp((left[index] * leftGain) + (right[index] * rightGain), -1f, 1f);
        return count;
    }
}
