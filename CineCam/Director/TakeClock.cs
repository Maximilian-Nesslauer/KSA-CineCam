namespace CineCam.Director;

/// <summary>
/// The clock of a real-time take. It counts real seconds and stops while the take is paused, so a
/// pause leaves no gap in the take time.
/// </summary>
public sealed class TakeClock
{
    private readonly double _start;
    private double _pausedTotal;
    private double? _pauseStart;

    public TakeClock(double nowSeconds)
    {
        _start = nowSeconds;
    }

    public bool IsPaused => _pauseStart.HasValue;

    public int PauseCount { get; private set; }

    /// <summary>Real seconds.</summary>
    public double LastPauseSeconds { get; private set; }

    /// <summary>The take time, which never goes back.</summary>
    public double Seconds(double nowSeconds)
    {
        double end = _pauseStart ?? nowSeconds;
        return Math.Max(end - _start - _pausedTotal, 0.0);
    }

    public double PausedSeconds(double nowSeconds) =>
        _pausedTotal + (_pauseStart.HasValue ? Math.Max(nowSeconds - _pauseStart.Value, 0.0) : 0.0);

    public bool Pause(double nowSeconds)
    {
        if (_pauseStart.HasValue)
            return false;
        _pauseStart = nowSeconds;
        PauseCount++;
        return true;
    }

    /// <summary>Returns the real length of the pause, or 0 when the clock was not paused.</summary>
    public double Resume(double nowSeconds)
    {
        if (_pauseStart is not double started)
            return 0.0;
        double length = Math.Max(nowSeconds - started, 0.0);
        _pausedTotal += length;
        _pauseStart = null;
        LastPauseSeconds = length;
        return length;
    }
}
