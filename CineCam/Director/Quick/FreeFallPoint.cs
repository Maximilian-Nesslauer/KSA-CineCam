namespace CineCam.Director.Quick;

/// <summary>
/// The flyby point in orbit falls freely, so a coasting vehicle drifts past it slowly instead of falling
/// away from a straight line. The position is relative to the body center in inertial axes. Velocity
/// Verlet keeps the orbit energy well over the short time of a flyby.
/// </summary>
public sealed class FreeFallPoint
{
    /// <summary>Seconds.</summary>
    public const double MaxStep = 0.25;

    /// <summary>Above this the steps get longer, which only happens at a high time warp.</summary>
    public const int MaxSteps = 400;

    private readonly double _mu;

    /// <param name="position">Meters from the body center.</param>
    /// <param name="velocity">Inertial, in m/s.</param>
    /// <param name="mu">m^3/s^2.</param>
    public FreeFallPoint(V3 position, V3 velocity, double mu)
    {
        Position = position;
        Velocity = velocity;
        _mu = Math.Max(mu, 0.0);
    }

    public V3 Position { get; private set; }

    public V3 Velocity { get; private set; }

    /// <summary>Takes game seconds.</summary>
    public void Advance(double seconds)
    {
        if (!(seconds > 0.0))
            return;
        int steps = (int)Math.Clamp(Math.Ceiling(seconds / MaxStep), 1.0, MaxSteps);
        double h = seconds / steps;
        V3 r = Position, v = Velocity;
        V3 a = Gravity(r);
        for (int i = 0; i < steps; i++)
        {
            v += a * (0.5 * h);
            r += v * h;
            a = Gravity(r);
            v += a * (0.5 * h);
        }

        Position = r;
        Velocity = v;
    }

    private V3 Gravity(V3 r)
    {
        double length = r.Length;
        if (_mu <= 0.0 || length < 1.0)
            return V3.Zero;
        return r * (-_mu / (length * length * length));
    }
}
