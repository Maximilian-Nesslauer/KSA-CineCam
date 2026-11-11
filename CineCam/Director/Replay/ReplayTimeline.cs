namespace CineCam.Director.Replay;

public static class ReplayTimeline
{
    public static double Duration(ReplayFlight flight) => flight.Actors
        .SelectMany(actor => actor.Segments)
        .SelectMany(segment => segment.Samples)
        .Select(sample => sample.Time)
        .DefaultIfEmpty(0.0).Max();

    /// <summary>The first actor is followed from recording time 0 until the first follow change of the file.</summary>
    public static List<(double Start, ReplayActor Actor)> Follows(ReplayFlight flight)
    {
        var result = new List<(double Start, ReplayActor Actor)> { (0.0, flight.Actors[0]) };
        foreach (ReplayFollow follow in flight.Follows ?? [])
        {
            if (flight.Actors.Find(actor => actor.Id == follow.ActorId) is { } actor && follow.Time > result[^1].Start)
                result.Add((follow.Time, actor));
        }

        return result;
    }

    public static IEnumerable<ReplaySample> FollowedSamples(ReplayFlight flight)
    {
        List<(double Start, ReplayActor Actor)> follows = Follows(flight);
        for (int i = 0; i < follows.Count; i++)
        {
            double from = follows[i].Start;
            double until = i + 1 < follows.Count ? follows[i + 1].Start : double.PositiveInfinity;
            foreach (ReplaySegment segment in follows[i].Actor.Segments)
            {
                foreach (ReplaySample sample in segment.Samples)
                {
                    if (sample.Time >= from && sample.Time < until)
                        yield return sample;
                }
            }
        }
    }

    public static (ReplaySegment Segment, ReplaySample Sample)? Sample(ReplayActor actor, double time)
    {
        if (!double.IsFinite(time) || (actor.End is double end && time >= end))
            return null;
        ReplaySegment? segment = null;
        foreach (ReplaySegment candidate in actor.Segments)
        {
            if (candidate.Start > time)
                break;
            segment = candidate;
        }
        if (segment == null || segment.Samples.Count == 0)
            return null;
        List<ReplaySample> samples = segment.Samples;
        if (time <= samples[0].Time)
            return (segment, samples[0]);
        if (time >= samples[^1].Time)
            return (segment, samples[^1]);
        int lo = 0, hi = samples.Count - 1;
        while (hi - lo > 1)
        {
            int mid = lo + (hi - lo) / 2;
            if (samples[mid].Time <= time) lo = mid;
            else hi = mid;
        }
        ReplaySample a = samples[lo], b = samples[hi];
        double t = (time - a.Time) / (b.Time - a.Time);
        return (segment, a with { Time = time, Position = V3.Lerp(a.Position, b.Position, t), Rotation = InterpolateRotation(a.Rotation, b.Rotation, t) });
    }

    internal static ReplayRotation Normalize(ReplayRotation q)
    {
        double scale = Math.Max(Math.Max(Math.Abs(q.X), Math.Abs(q.Y)), Math.Max(Math.Abs(q.Z), Math.Abs(q.W)));
        if (!double.IsFinite(scale) || scale < 1e-150)
            throw new InvalidDataException("Replay rotation is invalid.");
        double x = q.X / scale, y = q.Y / scale, z = q.Z / scale, w = q.W / scale;
        double length = Math.Sqrt(x * x + y * y + z * z + w * w);
        return new(x / length, y / length, z / length, w / length);
    }

    internal static ReplayRotation InterpolateRotation(ReplayRotation from, ReplayRotation to, double t)
    {
        ReplayRotation a = Normalize(from), b = Normalize(to);
        double dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        if (dot < 0)
        {
            b = new(-b.X, -b.Y, -b.Z, -b.W);
            dot = -dot;
        }
        double wa = 1 - t, wb = t;
        if (dot < 0.9995)
        {
            double angle = Math.Acos(Math.Clamp(dot, -1, 1));
            double sin = Math.Sin(angle);
            wa = Math.Sin((1 - t) * angle) / sin;
            wb = Math.Sin(t * angle) / sin;
        }
        return Normalize(new(a.X * wa + b.X * wb, a.Y * wa + b.Y * wb, a.Z * wa + b.Z * wb, a.W * wa + b.W * wb));
    }
}
