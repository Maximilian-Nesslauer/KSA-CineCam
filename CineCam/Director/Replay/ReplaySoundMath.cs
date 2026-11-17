namespace CineCam.Director.Replay;

/// <summary>The sim speed rule of a game sound, in the order of the game enum SimSpeedRule.</summary>
public enum SoundSpeedRule { None, Ignore, All, OnlyLower, OnlyHigher }

/// <summary>What the engine sounds of a replay do in a frame.</summary>
public enum ReplaySoundMode
{
    /// <summary>No sound: the setting is off, a render runs, or the replay plays faster than the game plays sound.</summary>
    Off,

    /// <summary>The sounds hold where they are, as the replay does.</summary>
    Paused,

    Play,
}

/// <summary>The rules of the engine sounds of a replay, which follow the recording time as the game sounds follow the sim speed.</summary>
public static class ReplaySoundMath
{
    /// <summary>GameAudio.MAX_SIM_SPEED_FOR_SOUND: above this sim speed the game plays no sound.</summary>
    public const double MaxRate = 10.0;

    /// <summary>Vehicle.AudibleDistance in meters.</summary>
    public const double AudibleDistance = 131072.0;

    /// <param name="rate">Recording seconds per real second, 0 while paused.</param>
    public static ReplaySoundMode Mode(bool enabled, bool rendering, bool playing, double rate)
    {
        if (!enabled || rendering || !double.IsFinite(rate) || rate > MaxRate)
            return ReplaySoundMode.Off;
        return playing && rate > 0.0 ? ReplaySoundMode.Play : ReplaySoundMode.Paused;
    }

    /// <summary>
    /// The pitch factor that ChannelWrapper.HandleSimSpeedPitch gives a sound at this sim speed, with the
    /// replay rate in place of the sim speed, so a replay at the speed of the flight sounds like the flight.
    /// </summary>
    public static float SpeedPitch(SoundSpeedRule rule, double rate)
    {
        if (rule == SoundSpeedRule.Ignore)
            return 1f;
        float pitch = 1f;
        if (rate >= 1.0 && rule is SoundSpeedRule.All or SoundSpeedRule.OnlyHigher)
            pitch = MathF.Min(1f + (float)rate / 5f, 3f);
        else if (rate < 1.0 && rule is SoundSpeedRule.All or SoundSpeedRule.OnlyLower)
            pitch = MathF.Max((float)rate, 0.25f);
        return rate == 0.0 ? 0f : pitch;
    }

    public static bool IsBurning(ReplayNozzle frame) =>
        frame.DutyCycle > 0 && frame.ExhaustPressure > 0 && frame.ExhaustVelocity > 0;

    /// <summary>
    /// The thrust fraction of a nozzle at a recording time, linear between the two samples around it
    /// while both burn. A recording without the throttle gives 1 for a burning nozzle.
    /// </summary>
    public static float Throttle(IReadOnlyList<ReplaySample> samples, double time, int nozzle)
    {
        if (samples.Count == 0 || nozzle < 0)
            return 0f;
        int lo = 0, hi = samples.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (samples[mid].Time <= time)
                lo = mid + 1;
            else
                hi = mid;
        }

        int before = Math.Max(lo - 1, 0);
        float a = ThrottleOf(samples[before], nozzle);
        if (lo >= samples.Count || lo == 0 || a <= 0f)
            return a;
        float b = ThrottleOf(samples[lo], nozzle);
        if (b <= 0f)
            return a;
        double span = samples[lo].Time - samples[before].Time;
        double share = span > 0.0 ? Math.Clamp((time - samples[before].Time) / span, 0.0, 1.0) : 0.0;
        return (float)(a + (b - a) * share);
    }

    private static float ThrottleOf(ReplaySample sample, int nozzle)
    {
        if (nozzle >= sample.Nozzles.Length || !IsBurning(sample.Nozzles[nozzle]))
            return 0f;
        float throttle = sample.Nozzles[nozzle].Throttle ?? 1f;
        return Math.Clamp(throttle, 0f, 1f);
    }
}
