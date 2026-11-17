namespace CineCam.Director.Sound;

/// <summary>
/// Mixes the speaker channels of the game sound to two channels. The channel order is the order of
/// the FMOD speaker modes: front left, front right, center, low frequency, surround left, surround
/// right, back left, back right, then the four top speakers.
/// </summary>
public static class SoundDownmix
{
    private const float Side = 0.70710677f;

    /// <summary>Writes <paramref name="frames"/> stereo frames into the target, without allocation.</summary>
    public static void ToStereo(ReadOnlySpan<float> source, int channels, int frames, Span<float> target)
    {
        if (channels < 1)
            throw new ArgumentOutOfRangeException(nameof(channels));
        if (source.Length < frames * channels || target.Length < frames * 2)
            throw new ArgumentException("The buffers are too short for the frames.");
        switch (channels)
        {
            case 1:
                for (int i = 0; i < frames; i++)
                    target[2 * i] = target[2 * i + 1] = source[i];
                return;
            case 2:
                source[..(frames * 2)].CopyTo(target);
                return;
        }

        for (int i = 0; i < frames; i++)
        {
            ReadOnlySpan<float> frame = source.Slice(i * channels, channels);
            float left, right;
            switch (channels)
            {
                case 4:
                    left = frame[0] + Side * frame[2];
                    right = frame[1] + Side * frame[3];
                    break;
                case 5:
                    left = frame[0] + Side * (frame[2] + frame[3]);
                    right = frame[1] + Side * (frame[2] + frame[4]);
                    break;
                case 6:
                    left = frame[0] + Side * (frame[2] + frame[4]);
                    right = frame[1] + Side * (frame[2] + frame[5]);
                    break;
                case 8:
                    left = frame[0] + Side * (frame[2] + frame[4] + frame[6]);
                    right = frame[1] + Side * (frame[2] + frame[5] + frame[7]);
                    break;
                case 12:
                    left = frame[0] + Side * (frame[2] + frame[4] + frame[6] + frame[8] + frame[10]);
                    right = frame[1] + Side * (frame[2] + frame[5] + frame[7] + frame[9] + frame[11]);
                    break;
                default:
                    float sum = 0f;
                    for (int c = 0; c < channels; c++)
                        sum += frame[c];
                    left = right = sum / channels;
                    break;
            }

            target[2 * i] = left;
            target[2 * i + 1] = right;
        }
    }
}
