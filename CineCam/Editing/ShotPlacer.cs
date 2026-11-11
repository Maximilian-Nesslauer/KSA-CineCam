using CineCam.Director;

namespace CineCam.Editing;

/// <param name="Duration">The duration that the new shot must get, or null to keep its own.</param>
/// <param name="InPreRoll">True when the new shot starts while the pre-roll loop runs, so the next pass of the loop takes the picture back.</param>
/// <param name="Delay">Seconds by which the shots after the new shot start later, because the time until the next shot is shorter than the shortest duration.</param>
public readonly record struct ShotPlacement(int Index, double? Duration, bool InPreRoll, double Delay = 0.0);

/// <summary>
/// The timeline shows the shot with the latest start, so a new shot takes over the picture at its start
/// wherever it is in the list. Its place still matters, because a shot that starts "after" the one before
/// it follows the new shot, and a pre-roll loop accepts only its own shots before the shot that waits for
/// the loop event. Nothing here touches the game.
/// </summary>
public static class ShotPlacer
{
    /// <summary>Take seconds, or null when the trigger still waits.</summary>
    public static double? StartOf(Trigger trigger, IReadOnlyDictionary<string, double> seen, IReadOnlyDictionary<string, string> aliases)
    {
        switch (trigger.Kind)
        {
            case TriggerKind.Time:
                return trigger.Time;
            case TriggerKind.Event:
                return EventName.TryResolve(trigger.Event, aliases, out EventName name, out _)
                    && seen.TryGetValue(name.Canonical, out double at) ? at + trigger.Delay : null;
            default:
                return null;
        }
    }

    /// <summary>0 for no loop or for a take with errors.</summary>
    public static int LoopShots(Take take, IReadOnlyDictionary<string, string> aliases)
    {
        if (take.PreRoll is not { Loop: true })
            return 0;
        try
        {
            return new TimelineRunner(take, aliases).LoopShots;
        }
        catch (ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The shot goes directly after the shot that shows at <paramref name="start"/>, and never before the shot
    /// that ends the pre-roll loop. When the next shot starts "after" the one before it, the new shot gets the
    /// duration until that start, so the shots after it keep their times.
    /// </summary>
    /// <param name="starts">Take seconds, one entry per shot, as the running timeline or the replay plan fixed
    /// them. When they are null or of another length, the shot goes after <paramref name="selected"/>.</param>
    /// <param name="selected">The selected shot, or -1.</param>
    public static ShotPlacement Place(Take take, double start, IReadOnlyList<double?>? starts, int loopShots, int selected)
    {
        int count = take.Shots.Count;
        loopShots = Math.Clamp(loopShots, 0, count);
        int first = loopShots > 0 ? Math.Min(loopShots + 1, count) : 0;
        if (starts == null || starts.Count != count)
        {
            int after = selected >= 0 && selected < count ? selected + 1 : count;
            return new ShotPlacement(Math.Max(after, first), null, false);
        }

        // The loop shots move with each pass, so they never decide the place.
        int running = -1;
        for (int i = loopShots; i < count; i++)
        {
            if (starts[i] is not double s || !double.IsFinite(s) || s > start + 1e-9)
                continue;
            if (running < 0 || s >= starts[running]!.Value)
                running = i;
        }

        int index = Math.Max(running + 1, first);
        bool inPreRoll = loopShots > 0 && (starts[loopShots] is not double wait || start < wait - 1e-9);

        // A shot "after" whose start still waits, for example behind the shot that ends a live pre-roll
        // loop, must stay behind the shot before it, so the new shot goes after that chain.
        while (index < count && take.Shots[index].At.Kind == TriggerKind.After && starts[index] == null)
            index++;

        // The duration is not rounded, so the shots after the new shot keep their exact starts.
        double? duration = null;
        double delay = 0.0;
        if (index < count && take.Shots[index].At.Kind == TriggerKind.After
            && starts[index] is double next && double.IsFinite(next) && next > start + 1e-9)
        {
            duration = Math.Max(TimelineLayout.MinDuration, next - start);
            delay = duration.Value - (next - start);
        }

        return new ShotPlacement(index, duration, inPreRoll, delay);
    }

    /// <summary>
    /// A pause can change the take while the run goes on with its own copy, so the starts come from the take
    /// itself and not from the timeline of the run. Null for a take with errors.
    /// </summary>
    public static double?[]? StartsAt(Take take, double seconds, IReadOnlyDictionary<string, double> seen, IReadOnlyDictionary<string, string> aliases)
    {
        try
        {
            var runner = new TimelineRunner(take, aliases);
            runner.Update(seconds, seen);
            return runner.Starts.Shots;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// While the edited take has the shot timing of the run, these are the starts that the run fixed, because
    /// a fresh runner cannot know them, as the run starts a shot with a negative delay at the prediction of its
    /// event. A take that the pause changed gets the starts of <see cref="StartsAt"/>.
    /// </summary>
    public static double?[]? StartsInPause(Take take, IReadOnlyDictionary<string, string> aliases, Take running,
        IReadOnlyDictionary<string, string> runningAliases, double?[] runningStarts, double seconds, IReadOnlyDictionary<string, double> seen)
    {
        if (runningStarts.Length == take.Shots.Count && SameShotTiming(take, running) && SameAliases(aliases, runningAliases))
            return (double?[])runningStarts.Clone();
        return StartsAt(take, seconds, seen, aliases);
    }

    private static bool SameShotTiming(Take a, Take b)
    {
        if (a.Shots.Count != b.Shots.Count || (a.PreRoll == null) != (b.PreRoll == null)
            || a.PreRoll is { } p && (p.Loop != b.PreRoll!.Loop || !string.Equals(p.Until, b.PreRoll.Until, StringComparison.Ordinal)))
            return false;
        for (int i = 0; i < a.Shots.Count; i++)
        {
            Trigger x = a.Shots[i].At, y = b.Shots[i].At;
            if (a.Shots[i].Duration != b.Shots[i].Duration || x.Kind != y.Kind || x.Time != y.Time || x.Delay != y.Delay
                || !string.Equals(x.Event, y.Event, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool SameAliases(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        ReferenceEquals(a, b) || a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out string? value) && string.Equals(value, pair.Value, StringComparison.Ordinal));
}
