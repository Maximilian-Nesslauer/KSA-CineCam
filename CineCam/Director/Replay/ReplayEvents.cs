namespace CineCam.Director.Replay;

/// <summary>
/// Times are recording seconds. Altitude and speed events depend on the values that a take waits for,
/// so they are not recorded but found here from the samples.
/// </summary>
public static class ReplayEvents
{
    /// <summary>
    /// A file of version 1 has no events, and its markers name the lift-off "Lift-off" and each counted
    /// ignition "Ignition" plus the count suffix of the event name.
    /// </summary>
    public static List<ReplayEvent> FromMarkers(IEnumerable<ReplayMarker> markers)
    {
        var events = new List<ReplayEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ReplayMarker marker in markers.OrderBy(m => m.Time))
        {
            string? name = marker.Label == "Lift-off" ? "liftoff"
                : marker.Label.StartsWith("Ignition", StringComparison.Ordinal) ? "ignition" + marker.Label["Ignition".Length..]
                : null;
            if (name != null && EventName.TryParse(name, out EventName parsed, out _) && parsed.Canonical == name && seen.Add(name))
                events.Add(new ReplayEvent(marker.Time, name));
        }

        return events;
    }

    /// <summary>Each name appears once, at its first time.</summary>
    public static List<ReplayEvent> ForTake(ReplayFlight flight, IEnumerable<EventName> watched, double bodyRadius)
    {
        var result = new List<ReplayEvent>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (ReplayEvent item in flight.Events.OrderBy(e => e.Time))
        {
            if (names.Add(item.Name))
                result.Add(item);
        }

        foreach (EventName name in watched)
        {
            if (!name.IsPredictable || !names.Add(name.Canonical))
                continue;
            double? time = name.Kind == EventKind.Altitude
                ? Crossing(flight, sample => Altitude(sample, bodyRadius), name.Value)
                : Crossing(flight, Speed, name.Value);
            if (time is double t)
                result.Add(new ReplayEvent(t, name.Canonical));
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }

    /// <summary>The first recording time at which the followed vehicle reaches the target, interpolated between samples.</summary>
    public static double? Crossing(ReplayFlight flight, Func<ReplaySample, double> value, double target)
    {
        ReplaySample? previous = null;
        double previousValue = 0.0;
        foreach (ReplaySample sample in ReplayTimeline.FollowedSamples(flight))
        {
            double current = value(sample);
            if (current >= target)
            {
                if (previous == null || current <= previousValue)
                    return sample.Time;
                double share = Math.Clamp((target - previousValue) / (current - previousValue), 0.0, 1.0);
                return previous.Time + (sample.Time - previous.Time) * share;
            }

            previous = sample;
            previousValue = current;
        }

        return null;
    }

    /// <summary>Barometric altitude, or the distance of the model origin above the mean radius in a file of version 1.</summary>
    public static double Altitude(ReplaySample sample, double bodyRadius) =>
        sample.State is ReplayState state ? state.Altitude : sample.Position.Length - bodyRadius;

    /// <summary>Surface speed, which a file of version 1 stores as the velocity of the air.</summary>
    public static double Speed(ReplaySample sample) =>
        sample.State is ReplayState state ? state.Speed : sample.AirVelocity.Length;
}
