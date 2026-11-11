namespace CineCam.Director;

/// <summary>The director core uses its own vector type so that it compiles without any game assembly.</summary>
public readonly record struct V3(double X, double Y, double Z)
{
    public static readonly V3 Zero = new(0.0, 0.0, 0.0);
    public static readonly V3 UnitX = new(1.0, 0.0, 0.0);
    public static readonly V3 UnitY = new(0.0, 1.0, 0.0);
    public static readonly V3 UnitZ = new(0.0, 0.0, 1.0);

    public double Length => Math.Sqrt(Dot(this, this));

    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static V3 operator -(V3 a) => new(-a.X, -a.Y, -a.Z);

    public static V3 operator *(V3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static V3 operator *(double s, V3 a) => a * s;

    public static V3 operator /(V3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static V3 Cross(V3 a, V3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public static V3 Lerp(V3 a, V3 b, double t) => a + (b - a) * t;

    public V3 NormalizedOr(V3 fallback)
    {
        double length = Length;
        return length > 1e-12 ? this / length : fallback;
    }

    /// <summary>
    /// For opposite directions the turn goes around any axis perpendicular to the first one, so that
    /// the result stays defined.
    /// </summary>
    public static V3 SlerpDirection(V3 a, V3 b, double t)
    {
        V3 from = a.NormalizedOr(UnitX);
        V3 to = b.NormalizedOr(from);
        double dot = Math.Clamp(Dot(from, to), -1.0, 1.0);
        if (dot > 0.9995)
            return Lerp(from, to, t).NormalizedOr(from);

        V3 axis;
        double angle;
        if (dot < -0.9995)
        {
            axis = Cross(from, UnitY).NormalizedOr(Cross(from, UnitZ).NormalizedOr(UnitZ));
            angle = Math.PI;
        }
        else
        {
            axis = Cross(from, to).NormalizedOr(UnitZ);
            angle = Math.Acos(dot);
        }

        double theta = angle * t;
        double cos = Math.Cos(theta);
        double sin = Math.Sin(theta);
        V3 rotated = from * cos + Cross(axis, from) * sin + axis * (Dot(axis, from) * (1.0 - cos));
        return rotated.NormalizedOr(from);
    }

    public override string ToString() => $"({X:G6}, {Y:G6}, {Z:G6})";
}

/// <summary>
/// An orthonormal frame in a parent frame. <see cref="X"/>, <see cref="Y"/> and <see cref="Z"/>
/// are the axes of the child frame written in the parent frame.
/// </summary>
public readonly record struct Basis3(V3 Origin, V3 X, V3 Y, V3 Z)
{
    public static readonly Basis3 Identity = new(V3.Zero, V3.UnitX, V3.UnitY, V3.UnitZ);

    public V3 ToParent(V3 local) => Origin + X * local.X + Y * local.Y + Z * local.Z;

    public V3 DirectionToParent(V3 local) => X * local.X + Y * local.Y + Z * local.Z;

    /// <summary>Keeps the first axis and makes the second one perpendicular to it.</summary>
    public static Basis3 FromXAndHint(V3 origin, V3 x, V3 yHint)
    {
        V3 ax = x.NormalizedOr(V3.UnitX);
        V3 hint = yHint.NormalizedOr(V3.UnitY);
        V3 y = hint - ax * V3.Dot(hint, ax);
        if (y.Length < 1e-9)
        {
            V3 other = Math.Abs(ax.Y) < 0.9 ? V3.UnitY : V3.UnitZ;
            y = other - ax * V3.Dot(other, ax);
        }

        y = y.NormalizedOr(V3.UnitY);
        return new Basis3(origin, ax, y, V3.Cross(ax, y));
    }
}

public static class Easing
{
    /// <summary>The quintic smootherstep, which has zero speed and zero acceleration at both ends.</summary>
    public static double Smootherstep(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
    }

    public static double Apply(Ease ease, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return ease switch
        {
            Ease.In => t * t,
            Ease.Out => 1.0 - (1.0 - t) * (1.0 - t),
            Ease.InOut => t * t * (3.0 - 2.0 * t),
            _ => t,
        };
    }
}
