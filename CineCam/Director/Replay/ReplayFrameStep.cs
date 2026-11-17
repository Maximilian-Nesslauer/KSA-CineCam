namespace CineCam.Director.Replay;

/// <summary>
/// The step of the recording time from one rendered frame to the next, for the effects that the game
/// advances by the last sim step. Every call in the same frame gets the same step, because the game
/// advances such an effect only once per frame. The step is never negative, and the first frame after
/// a jump (a seek, a scrub or the start of a render) gets 0.
/// </summary>
public sealed class ReplayFrameStep
{
    private ulong _frame;
    private bool _started;
    private double _time = double.NaN;
    private double _step;

    /// <summary>The time moved without play, so the next frame starts from it.</summary>
    public void Jump(double time) => _time = time;

    public double Step(ulong frame, double time)
    {
        if (_started && frame == _frame)
            return _step;
        _started = true;
        _frame = frame;
        double step = time - _time;
        _step = double.IsFinite(step) && step > 0 ? step : 0;
        _time = time;
        return _step;
    }
}
