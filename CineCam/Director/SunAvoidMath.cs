namespace CineCam.Director;

/// <summary>
/// Keeps the view direction away from the sun. The camera turns on a circle around its look point, so
/// the subject stays in the picture and only the side it is seen from changes.
/// </summary>
public static class SunAvoidMath
{
    public const double EaseSeconds = 1.0;

    public static double AngleDeg(V3 position, V3 lookAt, V3 sunDirection)
    {
        V3 view = (lookAt - position).NormalizedOr(V3.UnitX);
        V3 sun = sunDirection.NormalizedOr(V3.UnitX);
        return Math.Acos(Math.Clamp(V3.Dot(view, sun), -1.0, 1.0)) * 180.0 / Math.PI;
    }

    /// <summary>
    /// The body is a sphere of its mean radius, so terrain and the atmosphere do not count. A position
    /// under that radius counts as hidden when the sun is below its horizon.
    /// </summary>
    public static bool IsHidden(V3 position, V3 sunDirection, V3 bodyCentre, double bodyRadius)
    {
        if (bodyRadius <= 0.0)
            return false;
        V3 sun = sunDirection.NormalizedOr(V3.UnitX);
        V3 fromCentre = position - bodyCentre;
        double along = V3.Dot(fromCentre, sun);
        if (along >= 0.0)
            return false;
        double outside = V3.Dot(fromCentre, fromCentre) - bodyRadius * bodyRadius;
        return outside <= 0.0 || along * along >= outside;
    }

    public static double RequiredTurnDeg(V3 position, V3 lookAt, V3 sunDirection, double minimumDeg)
    {
        double angle = AngleDeg(position, lookAt, sunDirection);
        return angle >= minimumDeg ? 0.0 : minimumDeg - angle;
    }

    /// <summary>
    /// The turn is in the plane of the view and the sun. When the camera looks straight at the sun or
    /// away from it, that plane is undefined, and the camera turns about view cross up instead, which
    /// lifts it over the subject.
    /// </summary>
    public static CameraPose Turn(CameraPose pose, V3 sunDirection, double turnDeg)
    {
        double distance = (pose.LookAt - pose.Position).Length;
        if (turnDeg <= 1e-9 || distance < 1e-6)
            return pose;

        V3 view = (pose.LookAt - pose.Position) / distance;
        V3 sun = sunDirection.NormalizedOr(V3.UnitX);
        V3 axis = V3.Cross(view, sun);
        if (axis.Length < 1e-6)
            axis = V3.Cross(view, pose.Up);
        if (axis.Length < 1e-6)
            axis = V3.Cross(view, Math.Abs(view.X) < 0.9 ? V3.UnitX : V3.UnitY);
        axis = axis.NormalizedOr(V3.UnitY);

        // A positive turn about view x sun moves the view toward the sun, so the turn away is negative.
        double angle = -turnDeg * Math.PI / 180.0;
        V3 newView = Rotate(view, axis, angle).NormalizedOr(view);
        V3 newUp = Rotate(pose.Up, axis, angle);
        return pose with { Position = pose.LookAt - newView * distance, Up = newUp };
    }

    private static V3 Rotate(V3 v, V3 axis, double angle)
    {
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        return v * cos + V3.Cross(axis, v) * sin + axis * (V3.Dot(axis, v) * (1.0 - cos));
    }
}

/// <summary>Eases the sun avoidance turn, so the camera does not jump when the sun moves across the limit.</summary>
public sealed class SunAvoider
{
    private double _turn;
    private double _velocity;

    /// <summary>In degrees.</summary>
    public double Turn => _turn;

    /// <summary>For a cut.</summary>
    public double Snap(double wanted)
    {
        _turn = wanted;
        _velocity = 0.0;
        return _turn;
    }

    public double Update(double wanted, double dt)
    {
        _turn = Math.Max(Spring.SmoothDamp(_turn, wanted, ref _velocity, SunAvoidMath.EaseSeconds, dt), 0.0);
        return _turn;
    }
}
