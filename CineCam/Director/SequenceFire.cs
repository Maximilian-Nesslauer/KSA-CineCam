namespace CineCam.Director;

/// <summary><see cref="Next"/> is the sequence that the staging key would fire next, below 1 when none is left.</summary>
public readonly record struct SequenceSnapshot(int Next, int Active, int Count);

public static class SequenceFire
{
    /// <summary>
    /// The sequence that one call of the staging method fired, or null. The game removes an active
    /// sequence with no parts left inside that call, so a frame sample may never see it active.
    /// </summary>
    public static int? Fired(SequenceSnapshot before, SequenceSnapshot after)
    {
        if (before.Next < 1)
            return null;
        bool changed = before.Next != after.Next || before.Active != after.Active || before.Count != after.Count;
        return changed ? before.Next : null;
    }
}
