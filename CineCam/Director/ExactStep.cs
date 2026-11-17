namespace CineCam.Director;

/// <summary>
/// Plans the sim steps of an offline capture so that a picture lands exactly on a known game time.
/// The game lets a sim step grow to at most twice the step before it (Universe.ApplyVehicleSolvers),
/// so one short step in time warp makes the next pictures slow until the step has grown back. The
/// plan therefore spreads the way to the target over two pictures when it can, so no step is shorter
/// than half of the regular step.
/// </summary>
public static class ExactStep
{
    /// <summary>A target closer than this many seconds counts as reached.</summary>
    public const double Reached = 1e-6;

    /// <summary>
    /// Returns the game seconds that the next picture steps, or null for the regular step.
    /// <paramref name="secondsToTarget"/> is the game time from now to the nearest target that is still ahead.
    /// </summary>
    public static double? Next(double regularStep, double secondsToTarget)
    {
        if (!(regularStep > 0.0) || double.IsInfinity(regularStep) || !(secondsToTarget > Reached) || double.IsInfinity(secondsToTarget))
            return null;
        if (secondsToTarget > 2.0 * regularStep)
            return null;
        if (secondsToTarget <= regularStep)
            return secondsToTarget;
        return secondsToTarget * 0.5;
    }

    /// <summary>The nearest target after <paramref name="now"/>, or null.</summary>
    public static double? Nearest(double now, IEnumerable<double> targets)
    {
        double? best = null;
        foreach (double target in targets)
        {
            if (double.IsFinite(target) && target - now > Reached && (best == null || target < best.Value))
                best = target;
        }

        return best;
    }
}
