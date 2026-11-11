namespace CineCam.Director;

/// <summary>Critically damped smoothing, as a closed form that stays stable for any time step.</summary>
public static class Spring
{
    /// <summary>
    /// <paramref name="smoothTime"/> is about the time the value needs to reach the target. The
    /// polynomial approximates exp(-x) as in the common SmoothDamp form of the critically damped spring.
    /// </summary>
    public static double SmoothDamp(double current, double target, ref double velocity, double smoothTime, double dt)
    {
        if (smoothTime <= 1e-6 || dt <= 0.0)
        {
            if (smoothTime <= 1e-6)
                velocity = 0.0;
            return smoothTime <= 1e-6 ? target : current;
        }

        double omega = 2.0 / smoothTime;
        double x = omega * dt;
        double decay = 1.0 / (1.0 + x + 0.48 * x * x + 0.235 * x * x * x);
        double change = current - target;
        double temp = (velocity + omega * change) * dt;
        velocity = (velocity - omega * temp) * decay;
        return target + (change + temp) * decay;
    }

    public static V3 SmoothDamp(V3 current, V3 target, ref V3 velocity, double smoothTime, double dt)
    {
        double vx = velocity.X, vy = velocity.Y, vz = velocity.Z;
        double x = SmoothDamp(current.X, target.X, ref vx, smoothTime, dt);
        double y = SmoothDamp(current.Y, target.Y, ref vy, smoothTime, dt);
        double z = SmoothDamp(current.Z, target.Z, ref vz, smoothTime, dt);
        velocity = new V3(vx, vy, vz);
        return new V3(x, y, z);
    }
}

/// <summary>
/// Smooths the turn of a vehicle frame for chase shots. Only the axes are smoothed, because a
/// smoothed origin would trail a fast vehicle by its speed times the smooth time.
/// </summary>
public sealed class BasisSmoother
{
    private bool _started;
    private V3 _x, _y;
    private V3 _velocityX, _velocityY;

    public void Reset() => _started = false;

    public Basis3 Update(Basis3 target, double smoothTime, double dt)
    {
        if (!_started || smoothTime <= 1e-6)
        {
            _started = true;
            _x = target.X;
            _y = target.Y;
            _velocityX = V3.Zero;
            _velocityY = V3.Zero;
            return target;
        }

        _x = Spring.SmoothDamp(_x, target.X, ref _velocityX, smoothTime, dt).NormalizedOr(target.X);
        _y = Spring.SmoothDamp(_y, target.Y, ref _velocityY, smoothTime, dt).NormalizedOr(target.Y);
        Basis3 smoothed = Basis3.FromXAndHint(target.Origin, _x, _y);
        _x = smoothed.X;
        _y = smoothed.Y;
        return smoothed;
    }
}
