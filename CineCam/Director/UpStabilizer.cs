namespace CineCam.Director;

/// <summary>
/// Keeps the roll of the camera continuous when the view runs along the up hint of a shot, where the
/// part of the hint perpendicular to the view vanishes and flips. Near this pole the up of the last
/// frame turns toward the hint by a share that shrinks to zero on the pole. A view that passes very
/// close to the pole still turns the horizon by up to half a turn, because the geometry asks for it.
/// </summary>
public sealed class UpStabilizer
{
    // The sine of the angle between the view and the up axis below which the last frame
    // takes part. At 0.2 this is about 11.5 degrees from the pole.
    private const double BlendSine = 0.2;

    private V3? _last;

    public void Reset() => _last = null;

    /// <summary>The view and the hint need not be unit vectors.</summary>
    public V3 Stabilize(V3 forward, V3 up)
    {
        V3 view = forward.NormalizedOr(V3.UnitX);
        V3 hint = up.NormalizedOr(V3.UnitX);
        V3 perpendicular = hint - view * V3.Dot(view, hint);
        double sine = perpendicular.Length;

        V3 result;
        if (sine >= BlendSine)
        {
            result = perpendicular / sine;
        }
        else
        {
            V3 memory = Project(_last ?? FallbackAxis(view), view);
            if (sine < 1e-9)
            {
                result = memory;
            }
            else
            {
                double x = sine / BlendSine;
                double weight = x * x * (3.0 - 2.0 * x);
                result = TurnToward(memory, perpendicular / sine, view, weight);
            }
        }

        _last = result;
        return result;
    }

    // Both vectors are perpendicular to the view, so the result is too.
    private static V3 TurnToward(V3 from, V3 to, V3 view, double share)
    {
        double angle = Math.Atan2(V3.Dot(view, V3.Cross(from, to)), V3.Dot(from, to)) * share;
        V3 side = V3.Cross(view, from);
        return (from * Math.Cos(angle) + side * Math.Sin(angle)).NormalizedOr(from);
    }

    private static V3 Project(V3 direction, V3 view)
    {
        V3 perpendicular = direction - view * V3.Dot(view, direction);
        if (perpendicular.Length > 1e-9)
            return perpendicular.NormalizedOr(V3.UnitY);

        V3 axis = Math.Abs(view.X) < 0.9 ? V3.UnitX : V3.UnitY;
        return (axis - view * V3.Dot(view, axis)).NormalizedOr(V3.UnitZ);
    }

    private static V3 FallbackAxis(V3 view) => Math.Abs(view.X) < 0.9 ? V3.UnitX : V3.UnitY;
}
