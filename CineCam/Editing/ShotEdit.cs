using System.Globalization;
using CineCam.Director;

namespace CineCam.Editing;

public enum ShotField
{
    Target,
    Center,
    Radius,
    Height,
    StartDeg,
    DegPerSec,
    LookAt,
    From,
    To,
    Azimuth,
    Position,
    KeepSize,
    Offset,
    Smoothing,
    Part,
    Frame,
    Up,
    Points,
    Fit,
    Aim,
    AimOffset,
    AvoidSun,
    Subject,
    Pick,
}

/// <summary>The file writer follows the same rules, so a change here must match it.</summary>
public static class ShotFields
{
    public static bool Uses(ShotType type, ShotField field) => field switch
    {
        // The pose of a track, chase or part shot is always in one fixed frame, so the target only
        // matters for the other types.
        ShotField.Target => type is ShotType.Orbit or ShotType.Crane or ShotType.Tripod or ShotType.Path,
        ShotField.Center or ShotField.Height or ShotField.StartDeg or ShotField.DegPerSec => type == ShotType.Orbit,
        ShotField.Radius => type is ShotType.Orbit or ShotType.Crane,
        ShotField.LookAt => type is ShotType.Orbit or ShotType.Tripod or ShotType.Chase or ShotType.Part,
        ShotField.From or ShotField.To or ShotField.Azimuth => type == ShotType.Crane,
        ShotField.Position => type is ShotType.Tripod or ShotType.Track,
        ShotField.KeepSize => type == ShotType.Track,
        ShotField.Offset or ShotField.Smoothing => type is ShotType.Chase or ShotType.Part,
        ShotField.Part => type == ShotType.Part,
        ShotField.Frame => type is ShotType.Chase or ShotType.Part or ShotType.Path,
        ShotField.Points => type == ShotType.Path,
        ShotField.Fit => PoseMath.SupportsFit(type),
        ShotField.Up => true,
        ShotField.AvoidSun => true,
        ShotField.Aim or ShotField.Subject => Shot.SupportsAim(type),
        ShotField.AimOffset => Shot.SupportsAim(type),
        ShotField.Pick => Shot.SupportsAim(type),
        _ => false,
    };
}

public static class ShotEdit
{
    public static Shot NewShot(ShotType type)
    {
        var shot = new Shot { Type = type, Target = Shot.DefaultTarget(type), Duration = 8.0, Fov = 40.0 };
        switch (type)
        {
            case ShotType.Orbit:
                shot.Radius = 120.0;
                shot.Height = 25.0;
                shot.DegPerSec = 6.0;
                shot.LookAt = new V3(30.0, 0.0, 0.0);
                shot.Duration = 12.0;
                break;
            case ShotType.Crane:
                shot.To = 30.0;
                shot.Radius = 14.0;
                shot.Azimuth = 200.0;
                shot.Ease = Ease.InOut;
                break;
            case ShotType.Tripod:
                shot.Position = new V3(3.0, -60.0, -45.0);
                shot.LookAt = new V3(15.0, 0.0, 0.0);
                break;
            case ShotType.Track:
                shot.Position = new V3(3.0, -80.0, -60.0);
                break;
            case ShotType.Chase:
                shot.Offset = new V3(-25.0, 8.0, -12.0);
                shot.LookAt = new V3(10.0, 0.0, 0.0);
                break;
            case ShotType.Part:
                shot.Offset = new V3(-6.0, 4.0, -4.0);
                break;
            case ShotType.Path:
                shot.Ease = Ease.InOut;
                shot.Duration = 10.0;
                shot.Points.Add(new PathPoint { T = 0.0, Position = new V3(3.0, -80.0, -60.0), LookAt = new V3(15.0, 0.0, 0.0) });
                shot.Points.Add(new PathPoint { T = 1.0, Position = new V3(20.0, -35.0, -25.0), LookAt = new V3(15.0, 0.0, 0.0) });
                break;
        }

        return shot;
    }

    /// <summary>The default is a typical fairing release, which stages sequence 3 after the burnout of sequence 2.</summary>
    public static TakeAction NewAction() => new()
    {
        Kind = ActionKind.Stage,
        At = Trigger.OnEvent("sequence:2", 10.0),
        Sequence = 3,
    };

    public static SpeedKey NewSpeedKey() => new() { To = 1.0, Ramp = 1.0, Ease = Ease.InOut };

    /// <summary>
    /// Keeps the fields that both types share. A field that the new type needs and that is not valid yet gets
    /// the value of a new shot of that type.
    /// </summary>
    public static void ChangeType(Shot shot, ShotType type)
    {
        if (shot.Type == type)
            return;
        Shot fresh = NewShot(type);
        bool hadTarget = ShotFields.Uses(shot.Type, ShotField.Target);
        shot.Type = type;
        if (!Shot.SupportsAim(type))
        {
            shot.Aim = ShotAim.Look;
            shot.Subject = ShotSubject.Controlled;
        }

        if (!ShotFields.Uses(type, ShotField.Target) || !hadTarget)
            shot.Target = fresh.Target;
        if (ShotFields.Uses(type, ShotField.Radius) && shot.Radius <= 0.0)
            shot.Radius = fresh.Radius;
        if (type == ShotType.Part && shot.Offset == V3.Zero)
            shot.Offset = fresh.Offset;
        if (type == ShotType.Path && shot.Points.Count == 0)
        {
            shot.Points.AddRange(fresh.Points);
            if (shot.Ease == Ease.None)
                shot.Ease = Ease.InOut;
        }
    }

    public static Shot Clone(Shot source)
    {
        var copy = new Shot
        {
            Type = source.Type,
            Duration = source.Duration,
            Ease = source.Ease,
            Fov = source.Fov,
            Roll = source.Roll,
            Frame = source.Frame,
            Up = source.Up,
            Target = source.Target,
            Transition = new Transition { Kind = source.Transition.Kind, Seconds = source.Transition.Seconds },
            At = CloneTrigger(source.At),
            Center = source.Center,
            Radius = source.Radius,
            Height = source.Height,
            StartDeg = source.StartDeg,
            DegPerSec = source.DegPerSec,
            LookAt = source.LookAt,
            From = source.From,
            To = source.To,
            Azimuth = source.Azimuth,
            Position = source.Position,
            KeepSize = source.KeepSize,
            Fit = source.Fit,
            Aim = source.Aim,
            AimOffset = source.AimOffset,
            AvoidSun = source.AvoidSun,
            Subject = source.Subject,
            Pick = source.Pick,
            Offset = source.Offset,
            Smoothing = source.Smoothing,
            Part = source.Part,
        };
        foreach (PathPoint point in source.Points)
            copy.Points.Add(point.Clone());
        foreach (var pair in source.Extras)
            copy.Extras[pair.Key] = pair.Value;
        return copy;
    }

    public static SpeedKey Clone(SpeedKey source)
    {
        var copy = new SpeedKey { At = CloneTrigger(source.At), To = source.To, Ramp = source.Ramp, Ease = source.Ease };
        foreach (var pair in source.Extras)
            copy.Extras[pair.Key] = pair.Value;
        return copy;
    }

    public static TakeAction Clone(TakeAction source)
    {
        var copy = new TakeAction { At = CloneTrigger(source.At), Kind = source.Kind, Sequence = source.Sequence };
        foreach (var pair in source.Extras)
            copy.Extras[pair.Key] = pair.Value;
        return copy;
    }

    public static Take Clone(Take source, string name)
    {
        // The copy has no checkpoint, because the save belongs to the original take.
        var copy = new Take
        {
            Name = name,
            IsDefault = false,
            Site = source.Site,
            Anchor = source.Anchor?.Clone(),
            HideUiAt = source.HideUiAt,
            AutoStart = source.AutoStart,
            AutoStartDelay = source.AutoStartDelay,
            PreRoll = source.PreRoll?.Clone(),
        };
        if (source.KeepOut != null)
            copy.KeepOut = source.KeepOut.Select(b => b.Clone()).ToList();
        foreach (Shot shot in source.Shots)
            copy.Shots.Add(Clone(shot));
        foreach (SpeedKey key in source.Speed)
            copy.Speed.Add(Clone(key));
        foreach (TakeAction action in source.Actions)
            copy.Actions.Add(Clone(action));
        foreach (var pair in source.Extras)
            copy.Extras[pair.Key] = pair.Value;
        return copy;
    }

    public static Trigger CloneTrigger(Trigger source) =>
        new() { Kind = source.Kind, Time = source.Time, Event = source.Event, Delay = source.Delay };

    public static void ChangeTriggerKind(Trigger trigger, TriggerKind kind)
    {
        if (trigger.Kind == kind)
            return;
        trigger.Kind = kind;
        if (kind == TriggerKind.Event && trigger.Event.Length == 0)
            trigger.Event = "ignition";
    }

    /// <summary>Returns the new index of the item, or the old one at the ends.</summary>
    public static int Move<T>(List<T> list, int index, int delta)
    {
        int target = index + delta;
        if (index < 0 || index >= list.Count || target < 0 || target >= list.Count)
            return index;
        (list[index], list[target]) = (list[target], list[index]);
        return target;
    }

    /// <summary>
    /// The new point lies on the curve halfway to the next point or to the end. A path that ends at T 1 gets
    /// the new point before the last one instead.
    /// </summary>
    public static int AddPoint(Shot shot, int index)
    {
        List<PathPoint> points = shot.Points;
        if (points.Count == 0)
        {
            points.Add(new PathPoint { T = 0.0, Position = new V3(3.0, -60.0, -45.0), LookAt = new V3(15.0, 0.0, 0.0) });
            return 0;
        }

        index = Math.Clamp(index, 0, points.Count - 1);
        int after = index;
        if (index == points.Count - 1 && points[index].T > 0.999 && index > 0)
            after = index - 1;
        int next = after + 1 < points.Count ? after + 1 : after;
        double from = points[after].T;
        double to = after + 1 < points.Count ? points[after + 1].T : 1.0;
        double t = Math.Round((from + to) * 0.5, 3);
        if (!(t > from) || !(t < to))
            return index;

        PathSample sample = PathMath.Evaluate(points, t, shot.Fov);
        var point = new PathPoint
        {
            T = t,
            Position = Round2(sample.Position),
            LookAt = Round2(sample.LookAt),
            Fov = points[after].Fov.HasValue || points[next].Fov.HasValue ? Math.Round(sample.Fov, 1) : null,
            Roll = points[after].Roll.HasValue || points[next].Roll.HasValue ? Math.Round(sample.Roll, 1) : null,
        };
        points.Insert(after + 1, point);
        return after + 1;
    }

    /// <summary>The last point stays. Returns the index to select next.</summary>
    public static int RemovePoint(Shot shot, int index)
    {
        if (shot.Points.Count <= 1 || index < 0 || index >= shot.Points.Count)
            return index;
        shot.Points.RemoveAt(index);
        return Math.Min(index, shot.Points.Count - 1);
    }

    /// <summary>The times T stay with their places, so the order of T stays right and only the poses swap.</summary>
    public static int MovePoint(Shot shot, int index, int delta)
    {
        int target = index + delta;
        List<PathPoint> points = shot.Points;
        if (index < 0 || index >= points.Count || target < 0 || target >= points.Count)
            return index;
        PathPoint a = points[index], b = points[target];
        (a.T, b.T) = (b.T, a.T);
        (points[index], points[target]) = (b, a);
        return target;
    }

    private static V3 Round2(V3 v) => new(Math.Round(v.X, 2), Math.Round(v.Y, 2), Math.Round(v.Z, 2));

    public static string UniqueTakeName(IEnumerable<Take> takes, string wanted)
    {
        var used = new HashSet<string>(takes.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(wanted))
            return wanted;
        for (int n = 2; ; n++)
        {
            string candidate = wanted + "-" + n.ToString(CultureInfo.InvariantCulture);
            if (!used.Contains(candidate))
                return candidate;
        }
    }

    public static string Describe(Trigger trigger)
    {
        switch (trigger.Kind)
        {
            case TriggerKind.Time:
                return "at " + Num(trigger.Time) + " s";
            case TriggerKind.Event:
                string delay = trigger.Delay == 0.0 ? "" : (trigger.Delay > 0.0 ? " +" : " ") + Num(trigger.Delay) + " s";
                return "on " + trigger.Event + delay;
            default:
                return "after the one before";
        }
    }

    public static string Describe(int index, Shot shot) =>
        $"{index + 1}  {shot.Type.ToString().ToLowerInvariant()}  {Num(shot.Duration)} s  {Describe(shot.At)}";

    public static string Describe(int index, SpeedKey key) =>
        $"{index + 1}  to {Num(key.To)}x  ramp {Num(key.Ramp)} s  {Describe(key.At)}";

    public static string Describe(int index, TakeAction action)
    {
        string number = action.Sequence.HasValue ? $" sequence {action.Sequence.Value}" : "";
        return $"{index + 1}  {action.Kind.ToString().ToLowerInvariant()}{number}  {Describe(action.At)}";
    }

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
