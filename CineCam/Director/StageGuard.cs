namespace CineCam.Director;

public enum StageVerdict
{
    Fire,

    /// <summary>Keep the action and ask again in a later frame.</summary>
    Wait,

    /// <summary>Drop the action and log the reason.</summary>
    Skip,
}

public readonly record struct StageDecision(StageVerdict Verdict, string Reason);

/// <summary>
/// The staging key fires the next sequence in order, so the guard checks the wanted number and holds
/// back a second key while the first one is still in the input queue. Without that, two actions, or an
/// action and an autopilot, in one frame could fire two sequences at once.
/// </summary>
public sealed class StageGuard
{
    /// <summary>Seconds after which a key that never changed the active sequence counts as lost.</summary>
    public const double PendingTimeoutSeconds = 2.0;

    /// <summary>Seconds that an action waits for an earlier sequence to fire before it gives up.</summary>
    public const double EarlierSequenceTimeoutSeconds = 10.0;

    private int? _pending;
    private double _pendingSince;
    private double? _earlierSince;

    /// <param name="wanted">Null for any sequence.</param>
    /// <param name="next">Below 1 when no sequence is left.</param>
    /// <param name="now">A time in seconds that does not go back.</param>
    public StageDecision Decide(int? wanted, int next, int active, double now)
    {
        if (_pending is int pending)
        {
            if (active >= pending || now - _pendingSince > PendingTimeoutSeconds || next != pending)
                _pending = null;
            else
                return new StageDecision(StageVerdict.Wait, $"the key for sequence {pending} is still queued");
        }

        if (next < 1)
        {
            _earlierSince = null;
            return new StageDecision(StageVerdict.Skip, "no sequence is left to fire");
        }

        if (wanted.HasValue && next < wanted.Value)
        {
            // An autopilot often stages the earlier sequence a few frames later, so the action waits.
            _earlierSince ??= now;
            if (now - _earlierSince.Value <= EarlierSequenceTimeoutSeconds)
                return new StageDecision(StageVerdict.Wait, $"sequence {next} has not fired yet, so sequence {wanted.Value} waits");
            _earlierSince = null;
            return new StageDecision(StageVerdict.Skip,
                $"the next sequence stayed {next} for {EarlierSequenceTimeoutSeconds:0} s, so sequence {wanted.Value} did not fire");
        }

        _earlierSince = null;
        if (wanted.HasValue && next != wanted.Value)
            return new StageDecision(StageVerdict.Skip,
                $"the next sequence is {next}, not {wanted.Value}, so the staging key would fire the wrong one");

        _pending = next;
        _pendingSince = now;
        return new StageDecision(StageVerdict.Fire, $"fires sequence {next}");
    }

    public void Reset()
    {
        _pending = null;
        _earlierSince = null;
    }
}
