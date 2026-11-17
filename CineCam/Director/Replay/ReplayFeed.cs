namespace CineCam.Director.Replay;

public enum FeedKind { Event, Cue }

/// <summary>A flight event or a cue of a plan at its take time. <see cref="Index"/> is the action index of a cue, -1 for an event.</summary>
public sealed record FeedItem(FeedKind Kind, string Name, double TakeSeconds, int Index);

/// <summary>
/// The flight events and the cues that a replay passes when it plays from one take time to the next. Both come
/// from the plan, which knows their exact times before the replay plays, so the play gives each one once, in the
/// order of time, whatever the frame rate.
/// </summary>
public static class ReplayFeed
{
    /// <summary>
    /// The items after <paramref name="from"/> up to and including <paramref name="to"/>, in the order of time
    /// and with the events before the cues of the same time. With <paramref name="includeFrom"/> an item at
    /// <paramref name="from"/> counts too, for a play that starts at the take start, where a live take sees the
    /// events of its first frame.
    /// </summary>
    public static List<FeedItem> Due(ReplayTakePlan plan, double from, double to, bool includeFrom)
    {
        var items = new List<FeedItem>();
        if (!(to >= from))
            return items;
        foreach ((string name, double time) in plan.EventTimes)
        {
            if (Inside(time, from, to, includeFrom))
                items.Add(new FeedItem(FeedKind.Event, name, time, -1));
        }

        AddCues(plan, items, time => Inside(time, from, to, includeFrom));
        return Sorted(items);
    }

    /// <summary>The cues of the plan at or before a take time, in the order of time.</summary>
    public static List<FeedItem> CuesUntil(ReplayTakePlan plan, double time)
    {
        var items = new List<FeedItem>();
        AddCues(plan, items, start => start <= time);
        return Sorted(items);
    }

    private static bool Inside(double time, double from, double to, bool includeFrom) =>
        (includeFrom ? time >= from : time > from) && time <= to;

    // A cue that waits for an event the recording does not have has no start and never fires.
    private static void AddCues(ReplayTakePlan plan, List<FeedItem> items, Func<double, bool> take)
    {
        if (plan.Take is not { } source || plan.Starts is not { } starts)
            return;
        for (int i = 0; i < source.Actions.Count && i < starts.Actions.Length; i++)
        {
            TakeAction action = source.Actions[i];
            if (action.Kind == ActionKind.Cue && starts.Actions[i] is double start && take(start))
                items.Add(new FeedItem(FeedKind.Cue, action.Name, start, i));
        }
    }

    private static List<FeedItem> Sorted(List<FeedItem> items) =>
        items.OrderBy(item => item.TakeSeconds).ThenBy(item => item.Kind).ThenBy(item => item.Index).ThenBy(item => item.Name, StringComparer.Ordinal).ToList();
}
