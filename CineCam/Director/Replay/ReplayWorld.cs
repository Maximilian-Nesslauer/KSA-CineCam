namespace CineCam.Director.Replay;

/// <summary>
/// The site frame (up, east, north) given in the body-fixed frame CCF. The site frame of a live take is
/// fixed in CCF too, so a recording in CCF maps into it without the game.
/// </summary>
public readonly record struct ReplaySite(V3 OriginCcf, V3 UpCcf, V3 EastCcf, V3 NorthCcf)
{
    /// <summary>Uses the axes that the game gives a Landmark at that point.</summary>
    public static ReplaySite At(V3 originCcf)
    {
        V3 up = originCcf.NormalizedOr(V3.UnitX);
        WorldPoint.Axes(up, out V3 east, out V3 north);
        return new ReplaySite(originCcf, up, east, north);
    }

    public V3 ToSite(V3 ccf) => DirectionToSite(ccf - OriginCcf);

    public V3 DirectionToSite(V3 ccf) => new(V3.Dot(ccf, UpCcf), V3.Dot(ccf, EastCcf), V3.Dot(ccf, NorthCcf));

    public V3 ToCcf(V3 site) => OriginCcf + DirectionToCcf(site);

    public V3 DirectionToCcf(V3 site) => UpCcf * site.X + EastCcf * site.Y + NorthCcf * site.Z;
}

/// <summary>
/// In the assembly frame, which is the frame of the recorded rotation. <see cref="Base"/> is the origin of
/// the vehicle frame of the director, the lowest point along the axis through the center of mass.
/// </summary>
public sealed record ReplayShape(V3 Base, double Height, double Width, Func<string, V3?>? PartAssembly = null)
{
    /// <summary>Without a center of mass the axis goes through the middle of the box.</summary>
    public static ReplayShape FromBounds(V3 min, V3 max, V3? centerOfMass, Func<string, V3?>? parts = null)
    {
        V3 center = centerOfMass ?? (min + max) * 0.5;
        return new ReplayShape(new V3(min.X, center.Y, center.Z), Math.Max(max.X - min.X, 1.0),
            Math.Max(Math.Max(max.Y - min.Y, max.Z - min.Z), 0.1), parts);
    }
}

/// <summary>
/// Makes from a recording the same <see cref="WorldSample"/> values that a live take reads from the game.
/// The take follows the vehicle that the recorder followed at each time, and distances between vehicles
/// are measured between their centers of mass, as the game positions of a live take are.
/// </summary>
public sealed class ReplayWorld
{
    /// <summary>A vehicle farther than this from the followed vehicle when it first shows up is not a separated part, as in a live take.</summary>
    public const double NearMeters = 200.0;

    private readonly Func<ReplaySegment, ReplayShape> _shapes;
    private readonly Func<double, V3?>? _sunCcf;

    // Read once, so that Motion does not call the shape delegate, which reads game data, on a worker thread.
    private readonly Dictionary<ReplaySegment, V3> _bases;

    // A live take starts to look for separated vehicles again when it follows another vehicle, so each
    // followed span has its own list of the vehicles that showed up near it in the span.
    private readonly (double Start, ReplayActor Actor, List<ReplayActor> Separated)[] _follows;

    /// <param name="bodyRadius">Meters.</param>
    /// <param name="sunCcf">The position of the sun in the body-fixed frame at a recording time, or null without a sun.</param>
    public ReplayWorld(ReplayFlight flight, double bodyRadius, Func<ReplaySegment, ReplayShape> shapes, Func<double, V3?>? sunCcf = null)
    {
        Flight = flight;
        BodyRadius = bodyRadius;
        _shapes = shapes;
        _sunCcf = sunCcf;
        _bases = new Dictionary<ReplaySegment, V3>(ReferenceEqualityComparer.Instance);
        foreach (ReplaySegment segment in flight.Actors.SelectMany(actor => actor.Segments))
            _bases[segment] = shapes(segment).Base;
        Duration = ReplayTimeline.Duration(flight);
        Primary = flight.Actors[0];
        Start = Primary.Segments[0].Samples[0].Position;
        List<(double Start, ReplayActor Actor)> follows = ReplayTimeline.Follows(flight);
        _follows = new (double, ReplayActor, List<ReplayActor>)[follows.Count];
        for (int i = 0; i < follows.Count; i++)
        {
            (double start, ReplayActor followed) = follows[i];
            var separated = new List<ReplayActor>();
            foreach (ReplayActor actor in flight.Actors.Where(a => !a.Neighbor && !ReferenceEquals(a, followed) && a.Segments[0].Samples[0].Time > start)
                .OrderBy(a => a.Segments[0].Start).ThenBy(a => a.Id))
            {
                ReplaySegment segment = actor.Segments[0];
                ReplaySample first = segment.Samples[0];
                if (ReplayTimeline.Sample(followed, first.Time) is { } pose
                    && (CenterCcf(segment, first) - CenterCcf(pose.Segment, pose.Sample)).Length <= NearMeters)
                    separated.Add(actor);
            }

            _follows[i] = (start, followed, separated);
        }
    }

    public ReplayFlight Flight { get; }

    public double BodyRadius { get; }

    public double Duration { get; }

    public ReplayActor Primary { get; }

    /// <summary>The model origin of the first vehicle at the start of the recording in the body-fixed frame.</summary>
    public V3 Start { get; }

    public ReplayActor FollowedAt(double time) => _follows[FollowIndex(time)].Actor;

    /// <summary>Takes recording time and gives the world in the site frame.</summary>
    public WorldSample Build(double time, ReplaySite site, bool separated = true)
    {
        int follow = FollowIndex(time);
        ReplayActor followed = _follows[follow].Actor;
        var (segment, sample) = Followed(time, ref followed);
        ReplayShape shape = _shapes(segment);
        var world = new WorldSample
        {
            Vehicle = VehicleOf(sample),
            VehicleFrame = FrameOf(sample, shape, site),
            VehicleHeight = shape.Height,
            VehicleWidth = shape.Width,
            SurfaceVelocity = site.DirectionToSite(SurfaceVelocityCcf(followed, time)),
            PartLookup = PartsOf(shape),
            BodyCentre = site.ToSite(V3.Zero),
            Pole = site.DirectionToSite(V3.UnitZ),
            BodyRadius = BodyRadius,
        };

        V3 originCcf = OriginCcf(sample, shape);
        if (_sunCcf?.Invoke(time) is V3 sun)
            world.SunDirection = site.DirectionToSite((sun - originCcf).NormalizedOr(V3.UnitX));
        if (separated)
            Separated(_follows[follow].Separated, time, site, CenterCcf(segment, sample), world);
        return world;
    }

    /// <summary>
    /// The values of <see cref="Build"/> that the flight heading reads, in the body-fixed frame. It reads only
    /// the recording, so it can run on a worker thread.
    /// </summary>
    public (V3 OriginCcf, V3 VelocityCcf) Motion(double time)
    {
        ReplayActor followed = _follows[FollowIndex(time)].Actor;
        var (segment, sample) = Followed(time, ref followed);
        return (sample.Position + sample.Rotation.Rotate(_bases[segment]), SurfaceVelocityCcf(followed, time));
    }

    public VehicleSample VehicleAt(double time)
    {
        ReplayActor followed = FollowedAt(time);
        return VehicleOf(Followed(time, ref followed).Sample);
    }

    private int FollowIndex(double time)
    {
        int index = 0;
        while (index + 1 < _follows.Length && _follows[index + 1].Start <= time)
            index++;
        return index;
    }

    // The recorder follows a vehicle only while it records it, so only a damaged file has no pose, and
    // then the vehicle followed before stands in.
    private (ReplaySegment Segment, ReplaySample Sample) Followed(double time, ref ReplayActor followed)
    {
        if (ReplayTimeline.Sample(followed, time) is { } pose)
            return pose;
        for (int i = FollowIndex(time); i >= 0; i--)
        {
            if (ReplayTimeline.Sample(_follows[i].Actor, time) is { } before)
            {
                followed = _follows[i].Actor;
                return before;
            }
        }

        throw new InvalidDataException($"The recording has no followed vehicle at {time:0.###} s.");
    }

    // In the body-fixed frame, and the model origin in a file without the center of mass.
    private static V3 CenterCcf(ReplaySegment segment, ReplaySample sample) =>
        segment.CenterOfMass is V3 center ? sample.Position + sample.Rotation.Rotate(center) : sample.Position;

    /// <summary>In the site frame, or null when the actor does not exist at that time.</summary>
    public Basis3? FrameOf(ReplayActor actor, double time, ReplaySite site) =>
        ReplayTimeline.Sample(actor, time) is { } pose ? FrameOf(pose.Sample, _shapes(pose.Segment), site) : null;

    public ReplayShape? ShapeOf(ReplayActor actor, double time) =>
        ReplayTimeline.Sample(actor, time) is { } pose ? _shapes(pose.Segment) : null;

    /// <summary>
    /// In the body-fixed frame, from the motion of the center of mass, because a live take reads
    /// Vehicle.GetSurfaceVelocityCci, the velocity of the orbit state at the center of mass, so a vehicle
    /// that spins gives the same value. A segment of one sample uses the recorded velocity of the air.
    /// </summary>
    public static V3 SurfaceVelocityCcf(ReplayActor actor, double time)
    {
        var (segment, sample) = ReplayTimeline.Sample(actor, time)!.Value;
        double first = segment.Samples[0].Time, last = segment.Samples[^1].Time;
        double from = Math.Clamp(time - ReplaySampling.Interval, first, last);
        double to = Math.Clamp(time + ReplaySampling.Interval, first, last);
        if (to - from < 1e-6)
            return sample.AirVelocity;
        V3 a = CenterCcf(segment, ReplayTimeline.Sample(actor, from)!.Value.Sample);
        V3 b = CenterCcf(segment, ReplayTimeline.Sample(actor, to)!.Value.Sample);
        return (b - a) / (to - from);
    }

    private VehicleSample VehicleOf(ReplaySample sample)
    {
        ReplayState state = sample.State ?? default;
        bool hasState = sample.State.HasValue;
        return new VehicleSample(
            state.Has(ReplayStateFlags.EngineOn),
            hasState ? state.Has(ReplayStateFlags.Contact) : false,
            state.Sequence,
            ReplayEvents.Altitude(sample, BodyRadius),
            ReplayEvents.Speed(sample),
            Body: Flight.BodyId,
            RadialSpeed: hasState ? state.Radial : 0.0,
            StableOrbit: state.Has(ReplayStateFlags.StableOrbit),
            OutsideAtmosphere: state.Has(ReplayStateFlags.OutsideAtmosphere),
            DockedPorts: state.Docked);
    }

    private static V3 OriginCcf(ReplaySample sample, ReplayShape shape) => sample.Position + sample.Rotation.Rotate(shape.Base);

    private static Basis3 FrameOf(ReplaySample sample, ReplayShape shape, ReplaySite site) => new(
        site.ToSite(OriginCcf(sample, shape)),
        site.DirectionToSite(sample.Rotation.Rotate(V3.UnitX)),
        site.DirectionToSite(sample.Rotation.Rotate(V3.UnitY)),
        site.DirectionToSite(sample.Rotation.Rotate(V3.UnitZ)));

    // The vehicle frame has the axes of the assembly frame and its origin at the base.
    private static Func<string, V3?>? PartsOf(ReplayShape shape) => shape.PartAssembly is { } find
        ? id => find(id) is V3 part ? part - shape.Base : null
        : null;

    // Picks the newest and the nearest separated vehicle as the live tracker does, and the candidates are
    // ordered by the time they appeared.
    private void Separated(List<ReplayActor> candidates, double time, ReplaySite site, V3 followedCcf, WorldSample world)
    {
        SubjectSample? newest = null, nearest = null;
        double best = double.MaxValue;
        foreach (ReplayActor actor in candidates)
        {
            if (actor.Segments[0].Start > time || ReplayTimeline.Sample(actor, time) is not { } pose)
                continue;
            ReplayShape shape = _shapes(pose.Segment);
            double distance = (CenterCcf(pose.Segment, pose.Sample) - followedCcf).Length;
            var subject = new SubjectSample
            {
                Name = actor.Name,
                Frame = FrameOf(pose.Sample, shape, site),
                Height = shape.Height,
                Width = shape.Width,
                PartLookup = PartsOf(shape),
                Distance = distance,
            };
            newest = subject;
            if (distance < best)
            {
                best = distance;
                nearest = subject;
            }
        }

        world.SeparatedNewest = newest;
        world.SeparatedNearest = nearest;
    }
}
