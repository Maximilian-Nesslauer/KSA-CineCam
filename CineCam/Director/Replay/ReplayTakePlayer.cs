namespace CineCam.Director.Replay;

/// <summary>
/// Plays a take through the same <see cref="TakeDirector"/> as a live take. Only the smoothing of chase
/// and part shots, the flight heading and the sun avoidance carry state from frame to frame, so a seek
/// rebuilds the director and runs it over a short warm-up before the target.
/// </summary>
public sealed class ReplayTakePlayer
{
    /// <summary>Take seconds, one frame at 60 frames per second.</summary>
    public const double WarmUpStep = 1.0 / 60.0;

    /// <summary>Bounds the cost of an exact seek.</summary>
    public const int WarmUpSteps = 600;

    /// <summary>The steps of a rough seek, so a drag of the play head that seeks on every frame stays smooth.</summary>
    public const int ScrubSteps = 20;

    public const double MinPreviewSpeed = 0.1;
    public const double MaxPreviewSpeed = 4.0;

    private TakeDirector? _director;
    private readonly HashSet<int> _primed = new();
    private double _evaluated = double.NaN;
    private double _previewSpeed = 1.0;
    private bool _rough;
    private double _roughAt;

    public ReplayTakePlayer(ReplayWorld world)
    {
        World = world;
        Plan = ReplayTakePlan.Identity(world.Duration);
        Site = ReplaySite.At(world.Start);
    }

    public ReplayWorld World { get; }

    public ReplayTakePlan Plan { get; private set; }

    public ReplaySite Site { get; private set; }

    /// <summary>Take seconds.</summary>
    public double Time { get; private set; }

    public double RecordingTime => Plan.RecordingTime(Time);

    public double Duration => Plan.Duration;

    public bool IsPlaying { get; private set; }

    /// <summary>A multiplier on top of the speed track for watching only, it does not change the plan.</summary>
    public double PreviewSpeed
    {
        get => _previewSpeed;
        set => _previewSpeed = double.IsFinite(value) ? Math.Clamp(value, MinPreviewSpeed, MaxPreviewSpeed) : 1.0;
    }

    public DirectorFrame? Frame { get; private set; }

    public WorldSample? Sample { get; private set; }

    public TakeDirector? Director => _director;

    /// <summary>Why the take could not be resolved, or an empty text.</summary>
    public string Problem { get; private set; } = "";

    /// <summary>
    /// The replay stays at the same recording time, which can be another take time when the speed track
    /// changed. A take that the timeline rejects leaves a plan without a take.
    /// </summary>
    public void Load(Take? take, ShotFile? file, ReplaySite site)
    {
        ReplayTakePlan plan = Resolve(World, take, file, out string problem);
        Apply(plan, problem, site);
    }

    /// <summary>Reads only the recording, so it can run on a worker thread while the replay plays the plan that it has.</summary>
    public static ReplayTakePlan Resolve(ReplayWorld world, Take? take, ShotFile? file, out string problem, CancellationToken cancel = default)
    {
        problem = "";
        if (take == null || file == null)
            return ReplayTakePlan.Identity(world.Duration);
        try
        {
            List<ReplayEvent> events = ReplayEvents.ForTake(world.Flight, EventDetector.Watched(take, file.Events), world.BodyRadius);
            ReplayTakePlan plan = ReplayTakePlan.Resolve(take, file, events, world.Duration, cancel: cancel);
            if (take.Shots.Any(shot => shot.UsesFlightFrame))
                plan.Headings = new ReplayHeadings(world, plan, cancel);
            return plan;
        }
        catch (ArgumentException ex)
        {
            problem = ex.Message;
            return ReplayTakePlan.Identity(world.Duration);
        }
    }

    /// <summary>A rough apply uses a short warm-up for an edit that goes on, and <see cref="Settle"/> makes it exact.</summary>
    public void Apply(ReplayTakePlan plan, string problem, ReplaySite site, bool rough = false)
    {
        double recording = RecordingTime;
        Site = site;
        Problem = problem;
        Plan = plan;
        _director = null;
        Seek(plan.TakeTime(recording), rough);
    }

    /// <summary>Returns true when the play restarted from 0 because it was at the end.</summary>
    public bool SetPlaying(bool playing)
    {
        bool restart = playing && Time >= Duration;
        if (restart)
            Seek(0.0);
        IsPlaying = playing && Duration > 0.0;
        return restart;
    }

    public void Seek(double takeTime) => Seek(takeTime, false);

    /// <summary>
    /// A rough seek costs at most <see cref="ScrubSteps"/> world builds. A move forward within the warm-up
    /// goes on from the state that the director has, any other move warms up in coarse steps.
    /// </summary>
    public void Seek(double takeTime, bool rough)
    {
        double target = double.IsFinite(takeTime) ? Math.Clamp(takeTime, 0.0, Duration) : 0.0;
        if (rough && _director != null && Plan.Take is { } current && target >= _evaluated && target - _evaluated <= WarmUpSeconds(current))
        {
            Time = target;
            PrimeTracks(target);
            double stride = Math.Max(WarmUpStep, (target - _evaluated) / ScrubSteps);
            for (double t = _evaluated + stride; t < target - 1e-9; t += stride)
                _director.Update(t, World.Build(Plan.RecordingTime(t), Site, Plan.WantsSeparated));
            Evaluate(target);
            MarkRough();
            return;
        }

        Time = target;
        _director = Plan.CreateDirector();
        _primed.Clear();
        _rough = false;
        if (_director != null)
        {
            Take take = Plan.Take!;
            PrimeTracks(Time);
            double warmUp = WarmUpSeconds(take);
            double start = Math.Max(0.0, Time - warmUp);
            double step = Math.Max(WarmUpStep, warmUp / (rough ? ScrubSteps : WarmUpSteps));
            if (take.Shots.Any(shot => shot.UsesFlightFrame))
            {
                Plan.Headings ??= new ReplayHeadings(World, Plan);
                _director.SeedHeading(Plan.Headings.At(start).Map(Site.DirectionToSite));
            }
            for (double t = start; t < Time - 1e-9; t += step)
                _director.Update(t, World.Build(Plan.RecordingTime(t), Site, Plan.WantsSeparated));
            if (rough)
                MarkRough();
        }

        Evaluate(Time);
    }

    /// <summary>True after a rough seek or apply, until <see cref="Settle"/> or a play over the warm-up time.</summary>
    public bool IsRough => _rough;

    public void Settle()
    {
        if (_rough)
            Seek(Time);
    }

    private void MarkRough()
    {
        _rough = true;
        _roughAt = Time;
    }

    public void Advance(double realSeconds)
    {
        if (IsPlaying && double.IsFinite(realSeconds) && realSeconds > 0.0)
        {
            Time = Math.Min(Time + realSeconds * PreviewSpeed, Duration);
            if (Time >= Duration)
                IsPlaying = false;
        }

        Follow();
    }

    /// <summary>
    /// A move forward keeps the smoothing state, so a render that moves one picture step per frame gets the
    /// same state at any frame rate of the game. A move back seeks.
    /// </summary>
    public void MoveTo(double takeTime)
    {
        if (double.IsFinite(takeTime))
            Time = Math.Clamp(takeTime, 0.0, Duration);
        Follow();
    }

    private void Follow()
    {
        if (Time < _evaluated)
        {
            Seek(Time);
        }
        else if (Time != _evaluated || Frame == null && _director != null)
        {
            PrimeTracks(Time);
            Evaluate(Time);
        }

        // After a play over the warm-up time the smoothing has forgotten the rough state.
        if (_rough && Plan.Take is { } take && Time - _roughAt >= WarmUpSeconds(take))
            _rough = false;
    }

    // A track shot that keeps the size of the vehicle measures its distance in the world of the moment
    // when it first shows, so a play and a seek give the same size.
    private void PrimeTracks(double time)
    {
        if (_director == null || Plan.Take is not { } take || Plan.Starts is not { } starts)
            return;
        for (int i = 0; i < take.Shots.Count; i++)
        {
            if (take.Shots[i] is not { Type: ShotType.Track, KeepSize: true } || _primed.Contains(i)
                || i >= Plan.FirstShown.Count || Plan.FirstShown[i] is not double shown)
                continue;
            // A shot of a pre-roll loop has its last pass in the starts, so its first pass is the
            // time of the plan step at which it first showed.
            double first = starts.Shots[i] is double start && start <= shown ? start : shown;
            if (first > time)
                continue;
            _primed.Add(i);
            _director.PrimeTrackReference(i, World.Build(Plan.RecordingTime(first), Site, Plan.WantsSeparated));
        }
    }

    /// <summary>
    /// Take seconds. Six smooth times leave a difference of less than a thousandth. The flight heading can
    /// hold state for longer, so the warm-up starts from <see cref="ReplayHeadings"/>.
    /// </summary>
    public static double WarmUpSeconds(Take take)
    {
        double smooth = Math.Max(FlightHeading.SmoothSeconds, SunAvoidMath.EaseSeconds);
        foreach (Shot shot in take.Shots)
        {
            if (shot.Type is ShotType.Chase or ShotType.Part)
                smooth = Math.Max(smooth, shot.Smoothing);
        }

        return Math.Clamp(6.0 * smooth, 3.0, 120.0);
    }

    private void Evaluate(double time)
    {
        WorldSample world = World.Build(Plan.RecordingTime(time), Site, Plan.WantsSeparated);
        Sample = world;
        Frame = _director?.Update(time, world);
        _evaluated = time;
    }
}
