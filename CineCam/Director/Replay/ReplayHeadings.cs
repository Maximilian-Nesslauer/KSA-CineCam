namespace CineCam.Director.Replay;

/// <summary>
/// Runs the flight heading from take time 0 in steps of take time, as a live take does, so the turn
/// limit and the velocity smoothing see the same steps under a speed key. The heading holds its
/// direction while the vehicle is slow, so its state can come from longer before a seek than the
/// warm-up covers, which is why a seek starts the warm-up from this table. Vectors are in the
/// body-fixed frame.
/// </summary>
public sealed class ReplayHeadings
{
    /// <summary>Bounds the work and the memory, so a long take uses a coarser step than one frame.</summary>
    public const int MaxSteps = 240_000;

    /// <summary>Trades the size of the table against the steps that a lookup runs.</summary>
    public const int StepsPerEntry = 60;

    private readonly ReplayWorld _world;
    private readonly ReplayTakePlan _plan;
    private readonly FlightHeading.State[] _states;
    private readonly long _steps;

    /// <summary>Reads only the recording, so it can run on a worker thread.</summary>
    public ReplayHeadings(ReplayWorld world, ReplayTakePlan plan, CancellationToken cancel = default)
    {
        _world = world;
        _plan = plan;
        double duration = Math.Max(plan.Duration, 0.0);
        Step = Math.Max(ReplayTakePlayer.WarmUpStep, duration / MaxSteps);
        _steps = (long)Math.Floor(duration / Step + 1e-9);
        _states = new FlightHeading.State[_steps / StepsPerEntry + 1];
        var heading = new FlightHeading();
        for (long i = 0; i <= _steps; i++)
        {
            if ((i & 4095) == 0)
                cancel.ThrowIfCancellationRequested();
            Update(heading, i * Step, i == 0 ? 0.0 : Step);
            if (i % StepsPerEntry == 0)
                _states[i / StepsPerEntry] = heading.Save();
        }
    }

    /// <summary>Take seconds.</summary>
    public double Step { get; }

    public FlightHeading.State At(double takeTime)
    {
        double time = double.IsFinite(takeTime) ? Math.Clamp(takeTime, 0.0, _steps * Step) : 0.0;
        long index = Math.Min((long)Math.Floor(time / Step + 1e-9), _steps);
        long entry = index / StepsPerEntry;
        var heading = new FlightHeading();
        heading.Restore(_states[entry]);
        for (long i = entry * StepsPerEntry + 1; i <= index; i++)
            Update(heading, i * Step, Step);
        if (time - index * Step > 1e-9)
            Update(heading, time, time - index * Step);
        return heading.Save();
    }

    private void Update(FlightHeading heading, double takeTime, double dt)
    {
        (V3 origin, V3 velocity) = _world.Motion(_plan.RecordingTime(takeTime));
        Basis3 local = LocalHorizon.Frame(origin, V3.Zero, V3.UnitZ);
        heading.Update(local.X, local.Z, velocity, dt);
    }
}
