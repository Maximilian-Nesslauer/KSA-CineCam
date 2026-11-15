using CineCam.Director.Replay;

namespace CineCam.Director;

/// <summary>
/// The height of the lowest point of a vehicle over the ground under it. The live take and the replay use the
/// same math, so a ground event comes at the same time in both.
/// </summary>
public static class GroundClearance
{
    /// <summary>
    /// Measures along the up direction through <paramref name="centre"/>, down to the lowest of the eight
    /// corners of the part bounding box. The box is in the assembly frame, <paramref name="pivot"/> is the
    /// point of the assembly frame at <paramref name="centre"/>, and <paramref name="rotation"/> turns the
    /// assembly axes into the axes of <paramref name="centre"/>, which is relative to the body center.
    /// <paramref name="surfaceRadius"/> is the distance of the terrain or the sea under
    /// <paramref name="centre"/> from the body center. The result is negative when the lowest corner is
    /// below that surface.
    /// </summary>
    public static double Height(V3 centre, V3 boxMin, V3 boxMax, V3 pivot, ReplayRotation rotation, double surfaceRadius)
    {
        double distance = centre.Length;
        V3 up = centre.NormalizedOr(V3.UnitX);
        double lowest = double.PositiveInfinity;
        for (int corner = 0; corner < 8; corner++)
        {
            var point = new V3(
                (corner & 1) == 0 ? boxMin.X : boxMax.X,
                (corner & 2) == 0 ? boxMin.Y : boxMax.Y,
                (corner & 4) == 0 ? boxMin.Z : boxMax.Z);
            lowest = Math.Min(lowest, V3.Dot(up, rotation.Rotate(point - pivot)));
        }

        return distance + lowest - surfaceRadius;
    }
}
