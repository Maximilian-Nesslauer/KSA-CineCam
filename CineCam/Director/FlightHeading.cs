namespace CineCam.Director;

/// <summary>
/// The horizontal direction of the smoothed surface velocity, for the flight frame. The heading holds
/// its direction at low horizontal speed and turns at a limited rate, so it stays continuous through a
/// stop, a reverse, a vertical ascent or a fall.
/// </summary>
public sealed class FlightHeading
{
    /// <summary>Below this horizontal speed (m/s) the heading holds its direction, and it is north when it had none.</summary>
    public const double NorthBelow = 2.0;

    /// <summary>Above this horizontal speed (m/s) the heading turns at the full rate.</summary>
    public const double FlightAbove = 6.0;

    public const double SmoothSeconds = 1.0;

    public const double TurnDegreesPerSecond = 180.0;

    private bool _started;
    private V3 _velocity;
    private V3 _rate;
    private V3? _heading;

    /// <summary>
    /// The heading when no heading is known yet. It has no state, so between the two speed limits it
    /// flips sides when the velocity crosses south. <paramref name="up"/> and <paramref name="north"/>
    /// are perpendicular unit vectors.
    /// </summary>
    public static V3 Raw(V3 up, V3 north, V3 velocity)
    {
        V3 horizontal = velocity - up * V3.Dot(velocity, up);
        double speed = horizontal.Length;
        if (speed <= NorthBelow)
            return north;

        return Turn(up, north, horizontal / speed, Weight(speed));
    }

    // Turning about up keeps the result horizontal for every pair, also for opposite vectors.
    private static V3 Turn(V3 up, V3 from, V3 to, double part)
    {
        double angle = Math.Atan2(V3.Dot(up, V3.Cross(from, to)), V3.Dot(from, to)) * part;
        return (from * Math.Cos(angle) + V3.Cross(up, from) * Math.Sin(angle)).NormalizedOr(from);
    }

    public static V3 Follow(V3 up, V3 north, V3 last, V3 velocity, double dt)
    {
        V3 held = (last - up * V3.Dot(last, up)).NormalizedOr(north);
        V3 horizontal = velocity - up * V3.Dot(velocity, up);
        double speed = horizontal.Length;
        if (speed <= NorthBelow || !(dt > 0.0))
            return held;

        V3 target = horizontal / speed;
        double angle = Math.Abs(Math.Atan2(V3.Dot(up, V3.Cross(held, target)), V3.Dot(held, target)));
        double step = Weight(speed) * TurnDegreesPerSecond * Math.PI / 180.0 * dt;
        return angle <= step ? target : Turn(up, held, target, step / angle);
    }

    private static double Weight(double speed) => Easing.Smootherstep((speed - NorthBelow) / (FlightAbove - NorthBelow));

    /// <summary>
    /// A replay restores this state after a seek, because the heading can hold its direction for
    /// longer than any warm-up.
    /// </summary>
    public readonly record struct State(bool Started, V3 Velocity, V3 Rate, V3? Heading)
    {
        /// <summary>The state in another frame, for a linear map such as a rotation.</summary>
        public State Map(Func<V3, V3> direction) =>
            new(Started, direction(Velocity), direction(Rate), Heading is V3 heading ? direction(heading) : null);
    }

    public State Save() => new(_started, _velocity, _rate, _heading);

    public void Restore(State state)
    {
        _started = state.Started;
        _velocity = state.Velocity;
        _rate = state.Rate;
        _heading = state.Heading;
    }

    /// <summary>The velocity is a vector in the site frame.</summary>
    public V3 Update(V3 up, V3 north, V3 velocity, double dt)
    {
        if (!_started)
        {
            _started = true;
            _velocity = velocity;
            _rate = V3.Zero;
        }
        else
        {
            _velocity = Spring.SmoothDamp(_velocity, velocity, ref _rate, SmoothSeconds, dt);
        }

        _heading = _heading is V3 last ? Follow(up, north, last, _velocity, dt) : Raw(up, north, _velocity);
        return _heading.Value;
    }
}
