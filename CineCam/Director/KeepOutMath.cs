namespace CineCam.Director;

/// <summary>
/// Keeps the camera out of boxes in the site frame (+X up). The bottom face is never used, because a
/// camera under a deck would end up under the ground.
/// </summary>
public static class KeepOutMath
{
    /// <summary>How far outside the face the camera stands after a push, in meters.</summary>
    public const double Margin = 1.0;

    /// <summary>Boxes act only for a camera this close to the site origin, in meters.</summary>
    public const double NearSiteMeters = 5000.0;

    public static bool Contains(KeepOutBox box, V3 p) =>
        p.X >= box.Min.X && p.X <= box.Max.X
        && p.Y >= box.Min.Y && p.Y <= box.Max.Y
        && p.Z >= box.Min.Z && p.Z <= box.Max.Z;

    /// <summary>Moves a point inside the box to the nearest face that is not the bottom one, plus the margin.</summary>
    public static V3 Push(KeepOutBox box, V3 p, out string face)
    {
        face = "";
        if (!Contains(box, p))
            return p;

        (double Distance, string Name)[] faces =
        {
            (box.Max.X - p.X, "top"),
            (p.Y - box.Min.Y, "-Y"),
            (box.Max.Y - p.Y, "+Y"),
            (p.Z - box.Min.Z, "-Z"),
            (box.Max.Z - p.Z, "+Z"),
        };
        var nearest = faces[0];
        foreach (var candidate in faces)
        {
            if (candidate.Distance < nearest.Distance)
                nearest = candidate;
        }

        face = nearest.Name;
        return face switch
        {
            "top" => p with { X = box.Max.X + Margin },
            "-Y" => p with { Y = box.Min.Y - Margin },
            "+Y" => p with { Y = box.Max.Y + Margin },
            "-Z" => p with { Z = box.Min.Z - Margin },
            _ => p with { Z = box.Max.Z + Margin },
        };
    }

    /// <summary>
    /// A push can end inside a neighbouring box, so the pass repeats once per box.
    /// <paramref name="moved"/> is the first box that moved the point.
    /// </summary>
    public static V3 Apply(IReadOnlyList<KeepOutBox> boxes, V3 position, out KeepOutBox? moved, out string face)
    {
        moved = null;
        face = "";
        if (boxes.Count == 0 || position.Length > NearSiteMeters)
            return position;

        V3 result = position;
        for (int pass = 0; pass <= boxes.Count; pass++)
        {
            bool changed = false;
            foreach (KeepOutBox box in boxes)
            {
                V3 pushed = Push(box, result, out string used);
                if (pushed == result)
                    continue;
                result = pushed;
                changed = true;
                if (moved == null)
                {
                    moved = box;
                    face = used;
                }
            }

            if (!changed)
                return result;
        }

        // Boxes that touch can push a point back and forth. Lifting it above each box it is still in
        // always ends, because the height only grows.
        foreach (KeepOutBox box in boxes.OrderBy(b => b.Max.X))
        {
            if (Contains(box, result))
                result = result with { X = box.Max.X + Margin };
        }

        return result;
    }
}
