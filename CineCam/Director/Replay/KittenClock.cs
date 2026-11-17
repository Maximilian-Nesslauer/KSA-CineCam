namespace CineCam.Director.Replay;

/// <summary>
/// The animation time of one kitten in a replay. The game advances the animation of a kitten with the
/// simulation speed, which is 0 while a replay is open, so the replay advances it with the recording time.
/// The first step after the kitten shows is the whole recording time, so the phase of a looping animation
/// is the recording time, the same in every render that starts with new kittens. A step back in time gives
/// no step, so the animation goes on from there after a backward seek.
/// </summary>
public sealed class KittenClock
{
    /// <summary>The longest step that the damped parts of the kitten pose (expressions, condition) get at once.</summary>
    public const double MaxDampingStep = 0.1;

    private double? _last;

    /// <summary>The step of the animation to the recording time <paramref name="time"/>, 0 or more.</summary>
    public double Advance(double time)
    {
        if (!double.IsFinite(time))
            return 0.0;
        double step = _last is double last ? time - last : time;
        _last = time;
        return Math.Max(0.0, step);
    }
}
