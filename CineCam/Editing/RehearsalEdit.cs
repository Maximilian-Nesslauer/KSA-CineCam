using CineCam.Director;

namespace CineCam.Editing;

/// <summary>Edits to a take while it is paused. Nothing here touches the game.</summary>
public static class RehearsalEdit
{
    /// <summary>
    /// The sim speed that a pause gives back when it ends. A sim that was stopped at the pause, for example
    /// behind an open menu, has no usable speed, so the last speed of the take counts.
    /// </summary>
    public static double SpeedToRemember(double current, double? lastWritten, double? before)
    {
        if (current > 0.0)
            return current;
        if (lastWritten is > 0.0)
            return lastWritten.Value;
        if (before is > 0.0)
            return before.Value;
        return 1.0;
    }

    /// <summary>
    /// The latest event seen so far plus the seconds since it, or a take time when no event was seen. An
    /// event gets the name of an alias of the file when there is one.
    /// </summary>
    public static Trigger TriggerHere(IReadOnlyDictionary<string, double> seen,
        IReadOnlyDictionary<string, string> aliases, double takeTime)
    {
        string? best = null;
        double bestTime = double.NegativeInfinity;
        foreach (var pair in seen)
        {
            if (pair.Value <= takeTime + 1e-9 && pair.Value >= bestTime)
            {
                best = pair.Key;
                bestTime = pair.Value;
            }
        }

        if (best == null)
            return Trigger.AtTime(Math.Max(Math.Round(takeTime, 1), 0.0));

        double delay = Math.Max(Math.Round(takeTime - bestTime, 1), 0.0);
        return Trigger.OnEvent(NameFor(best, aliases), delay);
    }

    public static int AddSpeedKey(Take take, Trigger trigger, double to, double ramp)
    {
        take.Speed.Add(new SpeedKey
        {
            At = trigger,
            To = Math.Clamp(to, 0.0, 1000.0),
            Ramp = Math.Clamp(ramp, 0.0, 3600.0),
            Ease = Ease.InOut,
        });
        return take.Speed.Count - 1;
    }

    public static int AddStageAction(Take take, Trigger trigger, int? sequence)
    {
        take.Actions.Add(new TakeAction { At = trigger, Kind = ActionKind.Stage, Sequence = sequence is > 0 ? sequence : null });
        return take.Actions.Count - 1;
    }

    /// <summary>The ramp stays as it is.</summary>
    public static bool MoveSpeedKey(Take take, int index, Trigger trigger, double to)
    {
        if (index < 0 || index >= take.Speed.Count)
            return false;
        SpeedKey key = take.Speed[index];
        key.At = trigger;
        key.To = Math.Clamp(to, 0.0, 1000.0);
        return true;
    }

    // Uses the alias of the file, so the trigger reads like the others of the file.
    private static string NameFor(string canonical, IReadOnlyDictionary<string, string> aliases)
    {
        foreach (string alias in aliases.Keys.OrderBy(a => a, StringComparer.Ordinal))
        {
            if (EventName.TryResolve(alias, aliases, out EventName name, out _)
                && string.Equals(name.Canonical, canonical, StringComparison.Ordinal))
                return alias;
        }

        return canonical;
    }
}
