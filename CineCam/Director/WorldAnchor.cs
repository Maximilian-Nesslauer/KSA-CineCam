using System.Text.Json;

namespace CineCam.Director;

/// <summary>
/// The origin of the site frame when no Landmark is near. Latitude and longitude are in degrees in the
/// body-fixed frame (+Z toward the north pole, longitude 0 on +X). <see cref="Altitude"/> is in meters
/// above the terrain, so the origin stays on the ground when the terrain data of the body changes.
/// </summary>
public sealed class WorldAnchor
{
    public string Body { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; }

    /// <summary>Fields of the file that this version does not know. They are written back.</summary>
    public Dictionary<string, JsonElement> Extras { get; } = new();

    public WorldAnchor Clone()
    {
        var copy = new WorldAnchor { Body = Body, Latitude = Latitude, Longitude = Longitude, Altitude = Altitude };
        foreach (KeyValuePair<string, JsonElement> pair in Extras)
            copy.Extras[pair.Key] = pair.Value.Clone();
        return copy;
    }

    public V3 DirectionCcf => WorldPoint.Direction(Latitude, Longitude);

    public static WorldAnchor FromDirection(string body, V3 directionCcf, double altitude)
    {
        WorldPoint.LatLon(directionCcf, out double latitude, out double longitude);
        return new WorldAnchor { Body = body, Latitude = latitude, Longitude = longitude, Altitude = altitude };
    }
}

/// <summary>The math of points and local axes on a body, in the body-fixed frame (+Z is the pole).</summary>
public static class WorldPoint
{
    /// <summary>Matches the game's LocationReference.ForwardCcf for a latitude and longitude in degrees.</summary>
    public static V3 Direction(double latitudeDeg, double longitudeDeg)
    {
        double lat = latitudeDeg * Math.PI / 180.0;
        double lon = longitudeDeg * Math.PI / 180.0;
        return new V3(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat)).NormalizedOr(V3.UnitX);
    }

    /// <summary>Latitude from -90 to 90 and longitude from -180 to 180 degrees of a direction.</summary>
    public static void LatLon(V3 direction, out double latitudeDeg, out double longitudeDeg)
    {
        V3 unit = direction.NormalizedOr(V3.UnitX);
        latitudeDeg = Math.Asin(Math.Clamp(unit.Z, -1.0, 1.0)) * 180.0 / Math.PI;
        longitudeDeg = Math.Abs(unit.X) < 1e-15 && Math.Abs(unit.Y) < 1e-15 ? 0.0 : Math.Atan2(unit.Y, unit.X) * 180.0 / Math.PI;
    }

    /// <summary>
    /// The same axes as the game's LocationReference.GetAxesCcf gives for a Landmark. At a pole, where
    /// north has no meaning, east is +Y and north is up cross east.
    /// </summary>
    public static void Axes(V3 up, out V3 east, out V3 north)
    {
        V3 u = up.NormalizedOr(V3.UnitZ);
        V3 down = V3.Cross(V3.Cross(V3.UnitZ, u), u);
        if (down.Length < 1e-9)
        {
            east = V3.UnitY;
            north = V3.Cross(u, east).NormalizedOr(-V3.UnitX);
            return;
        }

        V3 d = down.NormalizedOr(V3.UnitX);
        east = V3.Cross(u, d);
        north = -d;
    }
}
