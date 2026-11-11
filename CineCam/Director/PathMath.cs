namespace CineCam.Director;

/// <summary>The camera values of a path at one moment, in the frame of the path shot.</summary>
public readonly record struct PathSample(V3 Position, V3 LookAt, double Fov, double Roll);

/// <summary>
/// The motion of a path shot. The camera position and the look point follow a cubic Hermite spline
/// through the keyframes, and the field of view and the roll follow a monotone cubic (no overshoot
/// between two values). The spline is C1 in the path parameter, so the velocity has no jump at a
/// keyframe. Before the first keyframe and after the last one the camera holds.
/// </summary>
/// <remarks>
/// The tangent direction of a vector track is the one of the centripetal Catmull-Rom spline, whose
/// knots grow with the square root of the chord length (Barry and Goldman 1988, Yuksel et al. 2011).
/// That form has no cusps and no loops inside a segment. It is then scaled to the keyframe times,
/// so the keyframes are reached at their own T. The first and the last tangent are the chord of
/// their segment, so the curve never leaves the first or the last point backward.
/// </remarks>
public static class PathMath
{
    // The smallest knot step, so that two equal points give a finite tangent.
    private const double MinKnotStep = 1e-6;

    /// <param name="points">The keyframes in the order of their T. At least one.</param>
    /// <param name="u">The path parameter from 0 to 1, which is the eased progress of the shot.</param>
    /// <param name="shotFov">The field of view for a keyframe that has none.</param>
    public static PathSample Evaluate(IReadOnlyList<PathPoint> points, double u, double shotFov)
    {
        int n = points.Count;
        if (n == 0)
            throw new ArgumentException("a path needs at least one point", nameof(points));
        if (n == 1)
            return Sample(points[0], shotFov);

        u = Math.Clamp(u, 0.0, 1.0);
        if (u <= points[0].T)
            return Sample(points[0], shotFov);
        if (u >= points[n - 1].T)
            return Sample(points[n - 1], shotFov);

        int i = 0;
        while (i < n - 2 && points[i + 1].T <= u)
            i++;

        var times = new double[n];
        var position = new V3[n];
        var look = new V3[n];
        var fov = new double[n];
        var roll = new double[n];
        for (int k = 0; k < n; k++)
        {
            times[k] = points[k].T;
            position[k] = points[k].Position;
            look[k] = points[k].LookAt;
            fov[k] = points[k].Fov ?? shotFov;
            roll[k] = points[k].Roll ?? 0.0;
        }

        double span = times[i + 1] - times[i];
        double f = (u - times[i]) / span;
        V3[] positionTangent = VectorTangents(position, times);
        V3[] lookTangent = VectorTangents(look, times);
        double[] fovTangent = ScalarTangents(fov, times);
        double[] rollTangent = ScalarTangents(roll, times);

        return new PathSample(
            Hermite(position[i], positionTangent[i], position[i + 1], positionTangent[i + 1], span, f),
            Hermite(look[i], lookTangent[i], look[i + 1], lookTangent[i + 1], span, f),
            Hermite(fov[i], fovTangent[i], fov[i + 1], fovTangent[i + 1], span, f),
            Hermite(roll[i], rollTangent[i], roll[i + 1], rollTangent[i + 1], span, f));
    }

    /// <summary>Checks the keyframe times: each from 0 to 1 and strictly increasing. Returns an error text, or null.</summary>
    public static string? CheckTimes(IReadOnlyList<PathPoint> points)
    {
        for (int i = 0; i < points.Count; i++)
        {
            double t = points[i].T;
            if (double.IsNaN(t) || t < 0.0 || t > 1.0)
                return $"point {i + 1} has t {t:G}, which is outside 0 to 1";
            if (i > 0 && !(t > points[i - 1].T))
                return $"point {i + 1} must have a larger t than point {i}";
        }

        return null;
    }

    private static PathSample Sample(PathPoint point, double shotFov) =>
        new(point.Position, point.LookAt, point.Fov ?? shotFov, point.Roll ?? 0.0);

    private static V3[] VectorTangents(V3[] p, double[] t)
    {
        int n = p.Length;
        var knot = new double[n];
        for (int k = 1; k < n; k++)
            knot[k] = knot[k - 1] + Math.Max(Math.Sqrt((p[k] - p[k - 1]).Length), MinKnotStep);

        // Knots per second on each segment, to turn a derivative by the knot into one by the time.
        var rate = new double[n - 1];
        for (int k = 0; k < n - 1; k++)
            rate[k] = (knot[k + 1] - knot[k]) / (t[k + 1] - t[k]);

        var tangent = new V3[n];
        tangent[0] = (p[1] - p[0]) / (t[1] - t[0]);
        tangent[n - 1] = (p[n - 1] - p[n - 2]) / (t[n - 1] - t[n - 2]);
        for (int k = 1; k < n - 1; k++)
        {
            V3 byKnot = (p[k] - p[k - 1]) / (knot[k] - knot[k - 1])
                - (p[k + 1] - p[k - 1]) / (knot[k + 1] - knot[k - 1])
                + (p[k + 1] - p[k]) / (knot[k + 1] - knot[k]);
            tangent[k] = byKnot * (0.5 * (rate[k - 1] + rate[k]));
        }

        return tangent;
    }

    // The Fritsch-Carlson slopes of a monotone cubic: zero at a local extreme, else a weighted
    // harmonic mean of the two chord slopes.
    private static double[] ScalarTangents(double[] y, double[] t)
    {
        int n = y.Length;
        var slope = new double[n - 1];
        for (int k = 0; k < n - 1; k++)
            slope[k] = (y[k + 1] - y[k]) / (t[k + 1] - t[k]);

        var tangent = new double[n];
        tangent[0] = slope[0];
        tangent[n - 1] = slope[n - 2];
        for (int k = 1; k < n - 1; k++)
        {
            if (slope[k - 1] * slope[k] <= 0.0)
                continue;
            double before = t[k] - t[k - 1];
            double after = t[k + 1] - t[k];
            double w1 = 2.0 * after + before;
            double w2 = after + 2.0 * before;
            tangent[k] = (w1 + w2) / (w1 / slope[k - 1] + w2 / slope[k]);
        }

        return tangent;
    }

    private static V3 Hermite(V3 p0, V3 v0, V3 p1, V3 v1, double span, double f)
    {
        Basis(f, out double h00, out double h10, out double h01, out double h11);
        return p0 * h00 + v0 * (h10 * span) + p1 * h01 + v1 * (h11 * span);
    }

    private static double Hermite(double p0, double v0, double p1, double v1, double span, double f)
    {
        Basis(f, out double h00, out double h10, out double h01, out double h11);
        return p0 * h00 + v0 * h10 * span + p1 * h01 + v1 * h11 * span;
    }

    private static void Basis(double f, out double h00, out double h10, out double h01, out double h11)
    {
        double f2 = f * f;
        double f3 = f2 * f;
        h00 = 2.0 * f3 - 3.0 * f2 + 1.0;
        h10 = f3 - 2.0 * f2 + f;
        h01 = -2.0 * f3 + 3.0 * f2;
        h11 = f3 - f2;
    }
}
