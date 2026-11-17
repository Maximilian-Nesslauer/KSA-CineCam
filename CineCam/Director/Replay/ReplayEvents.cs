namespace CineCam.Director.Replay;

/// <summary>
/// Times are recording seconds. Altitude, speed, ground, slow and q events depend on the values that a take
/// waits for, so they are not recorded but found here from the samples. A max Q is recorded, and it is found
/// here in a recording made before CineCam knew it.
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
            string? name = marker.Label == UiText.MessageText("text.cf343cf67ee7", "Lift-off") ? "liftoff"
                : marker.Label.StartsWith(UiText.MessageText("text.ae1c32802c31", "Ignition"), StringComparison.Ordinal) ? UiText.Concat("ignition", marker.Label[UiText.MessageText("text.ae1c32802c31", "Ignition").Length..])
                : null;
            if (name != null && EventName.TryParse(name, out EventName parsed, out _) && parsed.Canonical == name && seen.Add(name))
                events.Add(new ReplayEvent(marker.Time, name));
        }

        return events;
    }

    /// <summary>Each name appears once, at its first time.</summary>
    /// <param name="ground">
    /// The height over the ground at a sample (see <see cref="ReplayWorld.GroundHeight"/>). Without it only the
    /// recorded height counts.
    /// </param>
    public static List<ReplayEvent> ForTake(ReplayFlight flight, IEnumerable<EventName> watched, double bodyRadius,
        Func<ReplaySample, double?>? ground = null)
    {
        ground ??= sample => sample.State?.Ground;
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
            double? time = name.Kind switch
            {
                EventKind.Altitude => Crossing(flight, sample => Altitude(sample, bodyRadius), name.Value),
                EventKind.Ground or EventKind.Slow => Fall(flight, name.Kind == EventKind.Ground ? ground : sample => Speed(sample), name),
                EventKind.Q => Crossing(flight, DynamicPressure, name.Value),
                EventKind.MaxQ => MaxQ(flight, (int)Math.Max(name.Value, 1.0)),
                _ => Crossing(flight, Speed, name.Value),
            };
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

    /// <summary>
    /// The first recording time at which the followed vehicle falls to the target after a sample without ground
    /// contact above <see cref="EventName.ArmLevel"/>, interpolated between samples, as <see cref="EventDetector"/>
    /// finds a ground or slow event. A sample without the value neither arms nor fires it, and a sample without
    /// a state counts as one without contact.
    /// </summary>
    public static double? Fall(ReplayFlight flight, Func<ReplaySample, double?> value, EventName name)
    {
        double target = name.Value;
        bool above = false;
        ReplaySample? previous = null;
        double previousValue = 0.0;
        foreach (ReplaySample sample in ReplayTimeline.FollowedSamples(flight))
        {
            if (value(sample) is not double current)
            {
                previous = null;
                continue;
            }

            if (sample.State?.Has(ReplayStateFlags.Contact) != true && current > name.ArmLevel)
                above = true;
            if (current <= target && above)
            {
                if (previous == null || previousValue <= target)
                    return sample.Time;
                double share = Math.Clamp((previousValue - target) / (previousValue - current), 0.0, 1.0);
                return previous.Time + (sample.Time - previous.Time) * share;
            }

            previous = sample;
            previousValue = current;
        }

        return null;
    }

    /// <summary>
    /// The time of the Nth max Q of the followed vehicle by the rule of <see cref="MaxQTracker"/>, which a live take
    /// and the recorder use too. A recording made with the max Q events has them recorded, so this finds them in an
    /// older recording.
    /// </summary>
    public static double? MaxQ(ReplayFlight flight, int count)
    {
        var tracker = new MaxQTracker();
        int found = 0;
        foreach (ReplaySample sample in ReplayTimeline.FollowedSamples(flight))
        {
            if (tracker.Update(sample.Time, DynamicPressure(sample), sample.State?.Has(ReplayStateFlags.Contact) == true) is var (time, _)
                && ++found == count)
                return time;
        }

        return null;
    }

    /// <summary>
    /// Pascals, from the recorded air density and the velocity over the surface, as Vehicle.ComputeDynamicPressure
    /// computes it from the density at the altitude and the surface speed.
    /// </summary>
    public static double DynamicPressure(ReplaySample sample)
    {
        double speed = sample.AirVelocity.Length;
        return 0.5 * sample.AirDensity * speed * speed;
    }

    /// <summary>Barometric altitude, or the distance of the model origin above the mean radius in a file of version 1.</summary>
    public static double Altitude(ReplaySample sample, double bodyRadius) =>
        sample.State is ReplayState state ? state.Altitude : sample.Position.Length - bodyRadius;

    /// <summary>Surface speed, which a file of version 1 stores as the velocity of the air.</summary>
    public static double Speed(ReplaySample sample) =>
        sample.State is ReplayState state ? state.Speed : sample.AirVelocity.Length;
}
