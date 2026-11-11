namespace CineCam.Director;

public enum SpeedAction
{
    Wait,

    Write,

    /// <summary>The sim speed is at the target already.</summary>
    Keep,
}

public readonly record struct SpeedDecision(SpeedAction Action, bool ReportAutoWarp, bool ReportOverride);

/// <summary>
/// The take never fights an automatic warp or the pause of a menu, and it reports a speed that
/// someone else changed.
/// </summary>
public static class SpeedPolicy
{
    /// <param name="autoWarp">True while the game moves the time by itself.</param>
    /// <param name="lastWritten">Null when the take wrote no speed yet.</param>
    public static SpeedDecision Decide(bool autoWarp, double current, double? lastWritten, double target)
    {
        if (autoWarp)
            return new SpeedDecision(SpeedAction.Wait, true, false);

        bool overridden = false;
        if (lastWritten is double last)
        {
            // A speed of 0 after a non-zero write is the pause of an open menu. The game gives
            // the speed back when the menu closes.
            if (current == 0.0 && last != 0.0)
                return new SpeedDecision(SpeedAction.Wait, false, false);
            overridden = Math.Abs(current - last) > 1e-6;
        }

        SpeedAction action = Math.Abs(current - target) > 1e-9 ? SpeedAction.Write : SpeedAction.Keep;
        return new SpeedDecision(action, false, overridden);
    }
}
