namespace CineCam.Director;

/// <summary>
/// A vehicle that a shot follows. The frame has its origin at the base and +X to the nose.
/// <see cref="Name"/> is empty for the controlled vehicle and the vehicle id for a separated one.
/// </summary>
public sealed class SubjectSample
{
    public string Name { get; init; } = "";
    public Basis3 Frame { get; init; } = Basis3.Identity;
    public double Height { get; init; }
    public double Width { get; init; }
    public Func<string, V3?>? PartLookup { get; init; }

    /// <summary>Distance to the controlled vehicle in meters, for a separated vehicle.</summary>
    public double Distance { get; init; }
}

/// <summary>What the game side tells the director in one frame. All vectors are in the site frame, lengths in meters.</summary>
public sealed class WorldSample
{
    public VehicleSample Vehicle { get; set; }

    public Basis3 VehicleFrame { get; set; } = Basis3.Identity;

    public double VehicleHeight { get; set; }

    /// <summary>Zero when unknown.</summary>
    public double VehicleWidth { get; set; }

    /// <summary>The velocity over the surface in m/s, or null when unknown.</summary>
    public V3? SurfaceVelocity { get; set; }

    /// <summary>Returns the position of a part in the vehicle frame.</summary>
    public Func<string, V3?>? PartLookup { get; set; }

    public V3? BodyCentre { get; set; }

    /// <summary>The rotation axis of the body, toward north.</summary>
    public V3? Pole { get; set; }

    /// <summary>The mean radius of the body, or 0 when unknown.</summary>
    public double BodyRadius { get; set; }

    /// <summary>A unit vector, or null when there is no sun.</summary>
    public V3? SunDirection { get; set; }

    /// <summary>Set only when a shot needs it.</summary>
    public SubjectSample? SeparatedNewest { get; set; }

    /// <summary>Set only when a shot needs it.</summary>
    public SubjectSample? SeparatedNearest { get; set; }
}

public sealed record DirectorReport(string Name, string Text);

/// <summary>Poses are in the launch site frame.</summary>
public sealed class DirectorFrame
{
    /// <summary>Null while no shot has started.</summary>
    public CameraPose? Pose { get; init; }

    public int ShotIndex { get; init; } = -1;

    /// <summary>Seconds since the active shot started, limited to its duration.</summary>
    public double LocalTime { get; init; }

    /// <summary>Progress after the ease, 0 to 1.</summary>
    public double Progress { get; init; }

    public double TargetSpeed { get; init; } = 1.0;

    /// <summary>Before the first speed key starts, the take leaves the speed alone.</summary>
    public bool SpeedActive { get; init; }

    public bool Finished { get; init; }

    public IReadOnlyList<DetectedEvent> NewEvents { get; init; } = Array.Empty<DetectedEvent>();

    public IReadOnlyList<int> StartedShots { get; init; } = Array.Empty<int>();

    public IReadOnlyList<int> StartedSpeedKeys { get; init; } = Array.Empty<int>();

    public IReadOnlyList<int> StartedActions { get; init; } = Array.Empty<int>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<DirectorReport> Reports { get; init; } = Array.Empty<DirectorReport>();
}

/// <summary>
/// The game-free side of a take. The game side calls <see cref="Update"/> once per frame and applies
/// the result.
/// </summary>
public sealed class TakeDirector
{
    private readonly TimelineRunner _timeline;
    private readonly EventDetector _events;
    private readonly Dictionary<int, BasisSmoother> _smoothers = new();
    private readonly BasisSmoother _previewSmoother = new();
    private readonly Dictionary<int, double> _trackReference = new();
    private readonly HashSet<int> _partWarned = new();
    private readonly HashSet<int> _keepOutReported = new();
    private readonly HashSet<int> _fallbackReported = new();
    private readonly Dictionary<int, string> _pickReported = new();
    private readonly HashSet<int> _sunReported = new();
    private readonly SunAvoider _sun = new();
    private int _sunShot = -1;
    private readonly FlightHeading _heading = new();
    private readonly ShotFile _file;
    private V3? _flightHeading;
    private double _lastTime = double.NaN;
    private bool _speedActive;

    private static readonly IReadOnlyDictionary<string, double> NoEvents = new Dictionary<string, double>();
    private readonly bool _fixedStarts;

    public TakeDirector(Take take, ShotFile file)
    {
        Take = take;
        _timeline = new TimelineRunner(take, file.Events);
        _events = new EventDetector(EventDetector.Watched(take, file.Events));
        _file = file;
    }

    /// <summary>
    /// A director for a replay, where all starts are known before it runs. It detects no events, and
    /// a start that is null never comes.
    /// </summary>
    public TakeDirector(Take take, ShotFile file, TimelineStarts starts) : this(take, file)
    {
        _timeline.Preset(starts);
        _fixedStarts = true;
    }

    public V3? Heading => _flightHeading;

    /// <summary>A caller that previews a shot without <see cref="Update"/> calls this itself once per frame.</summary>
    public void TrackHeading(WorldSample world, double dt)
    {
        if (world.SurfaceVelocity is not V3 velocity)
        {
            _flightHeading = null;
            return;
        }

        Basis3 local = LocalHorizon.Frame(world.VehicleFrame.Origin, world.BodyCentre, world.Pole);
        _flightHeading = _heading.Update(local.X, local.Z, velocity, dt);
    }

    /// <summary>
    /// For a replay that rebuilds the director after a seek. The vectors are in the site frame, and the
    /// first update must come at the time of the state, with no time step.
    /// </summary>
    public void SeedHeading(FlightHeading.State state) => _heading.Restore(state);

    public Take Take { get; }

    public TimelineRunner Timeline => _timeline;

    public EventDetector Events => _events;

    public DirectorFrame Update(double takeTime, WorldSample world)
    {
        double dt = double.IsNaN(_lastTime) ? 0.0 : Math.Max(takeTime - _lastTime, 0.0);
        _lastTime = takeTime;

        TrackHeading(world, dt);
        IReadOnlyList<DetectedEvent> detected = _fixedStarts ? Array.Empty<DetectedEvent>() : _events.Update(takeTime, world.Vehicle);
        TimelineState state = _fixedStarts ? _timeline.Update(takeTime, NoEvents) : _timeline.Update(takeTime, _events.Seen, _events.Predictions);
        if (state.StartedSpeedKeys.Count > 0)
            _speedActive = true;

        var warnings = new List<string>();
        var notes = new List<string>();
        var reports = new List<DirectorReport>();
        CameraPose? pose = null;
        if (state.ShotIndex >= 0)
        {
            Shot shot = Take.Shots[state.ShotIndex];
            SubjectSample subject = SubjectOf(shot, world);
            ReportSubject(state.ShotIndex, shot, subject, reports);
            Basis3 smoothed = SmoothFor(state.ShotIndex, subject.Frame, dt);
            pose = SitePose(state.ShotIndex, state.Progress, world, smoothed, warnings);
            if (state.PreviousShotIndex >= 0)
            {
                Basis3 smoothedBefore = SmoothFor(state.PreviousShotIndex, SubjectOf(Take.Shots[state.PreviousShotIndex], world).Frame, dt);
                CameraPose before = SitePose(state.PreviousShotIndex, state.PreviousProgress, world, smoothedBefore, warnings);
                bool curved = PoseMath.MotionFrameOf(Take.Shots[state.PreviousShotIndex]) != PoseMath.MotionFrameOf(shot);
                pose = PoseMath.Blend(before, pose.Value, state.BlendWeight, curved);
            }

            pose = AvoidSunOf(state.ShotIndex, pose.Value, world, dt, reports);
            pose = KeepOutOf(state.ShotIndex, pose.Value, notes);
        }

        return new DirectorFrame
        {
            Pose = pose,
            ShotIndex = state.ShotIndex,
            LocalTime = state.LocalTime,
            Progress = state.Progress,
            TargetSpeed = state.TargetSpeed,
            SpeedActive = _speedActive,
            Finished = state.Finished,
            NewEvents = detected,
            StartedShots = state.StartedShots,
            StartedSpeedKeys = state.StartedSpeedKeys,
            StartedActions = state.StartedActions,
            Warnings = warnings,
            Notes = notes,
            Reports = reports,
        };
    }

    public static SubjectSample SubjectOf(Shot shot, WorldSample world)
    {
        if (shot.Subject == ShotSubject.Separated && Shot.SupportsAim(shot.Type))
        {
            SubjectSample? picked = shot.Pick == SeparatedPick.Nearest ? world.SeparatedNearest : world.SeparatedNewest;
            if (picked != null)
                return picked;
        }

        return new SubjectSample
        {
            Frame = world.VehicleFrame,
            Height = world.VehicleHeight,
            Width = world.VehicleWidth,
            PartLookup = world.PartLookup,
        };
    }

    private void ReportSubject(int index, Shot shot, SubjectSample subject, List<DirectorReport> reports)
    {
        if (shot.Subject != ShotSubject.Separated || !Shot.SupportsAim(shot.Type))
            return;
        if (subject.Name.Length == 0)
        {
            if (_fallbackReported.Add(index))
                reports.Add(new DirectorReport("target", $"camera[{index}]: no vehicle has separated yet, so the shot follows the controlled vehicle"));
            return;
        }

        if (_pickReported.TryGetValue(index, out string? last) && last == subject.Name)
            return;
        _pickReported[index] = subject.Name;
        string pick = shot.Pick == SeparatedPick.Nearest ? "nearest" : "newest";
        reports.Add(new DirectorReport("target", string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"camera[{index}]: the shot follows the separated vehicle \"{subject.Name}\" ({pick}, {subject.Distance:F0} m from the controlled vehicle, {subject.Height:F1} m long)")));
    }

    // The turn eases except at a cut, where the picture jumps anyway.
    private CameraPose AvoidSunOf(int shotIndex, CameraPose pose, WorldSample world, double dt, List<DirectorReport> reports)
    {
        Shot shot = Take.Shots[shotIndex];
        double wanted = 0.0;
        if (shot.AvoidSun is double minimum && world.SunDirection is V3 sun)
        {
            bool hidden = world.BodyCentre is V3 centre && SunAvoidMath.IsHidden(pose.Position, sun, centre, world.BodyRadius);
            if (!hidden)
                wanted = SunAvoidMath.RequiredTurnDeg(pose.Position, pose.LookAt, sun, minimum);
        }

        bool snap = shotIndex != _sunShot && (_sunShot < 0 || shot.Transition.Kind == TransitionKind.Cut);
        _sunShot = shotIndex;
        double turn = snap ? _sun.Snap(wanted) : _sun.Update(wanted, dt);
        if (turn <= 1e-6 || world.SunDirection is not V3 direction)
            return pose;
        if (_sunReported.Add(shotIndex))
        {
            reports.Add(new DirectorReport("avoidSun", string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"camera[{shotIndex}]: the view was {SunAvoidMath.AngleDeg(pose.Position, pose.LookAt, direction):F1} deg from the sun, so the camera turns {wanted:F1} deg about its look point")));
        }

        return SunAvoidMath.Turn(pose, direction, turn);
    }

    private CameraPose KeepOutOf(int shotIndex, CameraPose pose, List<string> notes)
    {
        V3 position = KeepOutMath.Apply(_file.KeepOutFor(Take), pose.Position, out KeepOutBox? box, out string face);
        if (box == null)
            return pose;
        if (_keepOutReported.Add(shotIndex))
        {
            string name = box.Name.Length > 0 ? box.Name : "unnamed";
            notes.Add($"camera[{shotIndex}]: the camera was inside the keep-out box \"{name}\", so it moved to the {face} face of the box");
        }

        return pose with { Position = position };
    }

    // Each shot has its own smoother, so a blend keeps the lag of the outgoing shot.
    private Basis3 SmoothFor(int index, Basis3 frame, double dt)
    {
        if (!_smoothers.TryGetValue(index, out BasisSmoother? smoother))
        {
            smoother = new BasisSmoother();
            _smoothers[index] = smoother;
        }

        Shot shot = Take.Shots[index];
        bool smooths = shot.Type is ShotType.Chase or ShotType.Part;
        return smoother.Update(frame, smooths ? shot.Smoothing : 0.0, dt);
    }

    public Basis3 SmoothVehicle(Basis3 frame, double smoothTime, double dt) => _previewSmoother.Update(frame, smoothTime, dt);

    /// <summary>
    /// Measures the start distance of a keep-size track shot in the world of the moment when the shot
    /// first ran. A replay that starts in the middle of the shot calls it, so the shot has the size of a
    /// play from the take start.
    /// </summary>
    public void PrimeTrackReference(int index, WorldSample world)
    {
        if (index < 0 || index >= Take.Shots.Count || _trackReference.ContainsKey(index))
            return;
        Shot shot = Take.Shots[index];
        if (shot.Type == ShotType.Track && shot.KeepSize)
            _trackReference[index] = TrackReferenceOf(shot, SubjectOf(shot, world));
    }

    private static double TrackReferenceOf(Shot shot, SubjectSample subject)
    {
        V3 aim = subject.Frame.Origin + subject.Frame.X * (subject.Height * 0.5);
        return (aim - shot.Position).Length;
    }

    public void ForgetTrackReference() => _trackReference.Clear();

    private CameraPose SitePose(int index, double progress, WorldSample world, Basis3 smoothed, List<string> warnings) =>
        ShotPoseInSite(Take.Shots[index], index, progress, world, smoothed, warnings);

    /// <summary>
    /// The key names the shot for the per-shot memory (the track start distance and the part warning),
    /// so a caller that previews one shot uses the same key each time.
    /// </summary>
    public CameraPose ShotPoseInSite(Shot shot, int key, double progress, WorldSample world, Basis3 smoothed, List<string> warnings)
    {
        int index = key;
        SubjectSample subject = SubjectOf(shot, world);
        var inputs = new PoseInputs
        {
            Vehicle = subject.Frame,
            VehicleHeight = subject.Height,
            VehicleWidth = subject.Width,
            BodyCentre = world.BodyCentre,
            Pole = world.Pole,
            FlightHeading = _flightHeading,
        };

        if (shot.Type == ShotType.Part && shot.Part.Length > 0)
        {
            inputs.PartPosition = subject.PartLookup?.Invoke(shot.Part);
            if (inputs.PartPosition == null && _partWarned.Add(index))
                warnings.Add($"camera[{index}]: the vehicle has no part \"{shot.Part}\", so the shot uses the base of the vehicle");
        }

        if (shot.Type == ShotType.Track && shot.KeepSize)
        {
            if (!_trackReference.TryGetValue(index, out double reference))
            {
                reference = TrackReferenceOf(shot, subject);
                _trackReference[index] = reference;
            }

            inputs.TrackReferenceDistance = reference;
        }

        return PoseMath.EvaluateSite(shot, progress, inputs, smoothed);
    }
}
