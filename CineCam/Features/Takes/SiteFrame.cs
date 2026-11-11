using System.Globalization;
using Brutal.Numerics;
using CineCam.Director;
using CineCam.Director.Replay;
using KSA;

namespace CineCam.Features.Takes;

// A frame at a point on the ground of a body, with +X up, +Y east and +Z north. The axes and the
// origin are fixed in the body-fixed frame (CCF), so Refresh must run once per frame because the
// body turns.
internal sealed class SiteFrame
{
    // A Landmark farther than this from the vehicle does not count as its launch site.
    private const double NearestLimitMeters = 2000.0;

    private readonly double3 _upCcf;
    private readonly double3 _eastCcf;
    private readonly double3 _northCcf;
    private readonly double3 _originCcf;
    private doubleQuat _ccf2Cce = doubleQuat.Identity;
    private doubleQuat _cce2Ccf = doubleQuat.Identity;
    private double3 _bodyEcl;

    private SiteFrame(Celestial body, LandmarkReference landmark)
    {
        Body = body;
        Landmark = landmark;
        Id = landmark.Key;
        landmark.GetAxesCcf(out _upCcf, out _eastCcf, out _northCcf);

        // The static object offset puts the origin on the pad surface that the player sees.
        double ground = landmark.GetStaticObject()?.GroundOffset ?? 0.0;
        double terrain = body.GetTerrainHeightFromDirCcf(landmark.ForwardCcf, accurate: true);
        _originCcf = _upCcf * (body.MeanRadius + terrain + ground);
        Refresh();
    }

    // The axes match the ones that LocationReference.GetAxesCcf gives for a Landmark at the same place.
    private SiteFrame(Celestial body, WorldAnchor anchor, string id)
    {
        Body = body;
        Anchor = anchor;
        Id = id;
        V3 up = anchor.DirectionCcf;
        WorldPoint.Axes(up, out V3 east, out V3 north);
        _upCcf = ToDouble3(up);
        _eastCcf = ToDouble3(east);
        _northCcf = ToDouble3(north);
        double terrain = body.GetTerrainHeightFromDirCcf(_upCcf, accurate: true);
        _originCcf = _upCcf * (body.MeanRadius + terrain + anchor.Altitude);
        Refresh();
    }

    public Celestial Body { get; }

    // Null for a frame at a world point.
    public LandmarkReference? Landmark { get; }

    // Null for a frame at a Landmark. A take without an anchor can store this one, so a later
    // start uses the same origin.
    public WorldAnchor? Anchor { get; }

    public string Id { get; }

    // The rotation axis of the body (+Z of CCF, the pole that GetAxesCcf uses for north) in the
    // site axes. It is constant because the site axes are fixed in CCF.
    public V3 PoleInSite => new(_upCcf.Z, _eastCcf.Z, _northCcf.Z);

    // The frame in CCF, for the game-free replay world.
    public ReplaySite ToReplaySite() => new(ToV3(_originCcf), ToV3(_upCcf), ToV3(_eastCcf), ToV3(_northCcf));

    private static V3 ToV3(double3 v) => new(v.X, v.Y, v.Z);

    public void Refresh()
    {
        _ccf2Cce = Body.GetCcf2Cce();
        _cce2Ccf = Body.GetCce2Ccf();
        _bodyEcl = Body.GetPositionEcl();
    }

    // ECL has the same orientation as CCE, which GetCcf2Cce maps to, so only the body position is added.
    public double3 ToEcl(V3 site)
    {
        double3 ccf = _originCcf + _upCcf * site.X + _eastCcf * site.Y + _northCcf * site.Z;
        return _bodyEcl + ccf.Transform(_ccf2Cce);
    }

    public double3 DirectionToEcl(V3 site)
    {
        double3 ccf = _upCcf * site.X + _eastCcf * site.Y + _northCcf * site.Z;
        return ccf.Transform(_ccf2Cce);
    }

    public V3 FromEcl(double3 ecl)
    {
        double3 rel = (ecl - _bodyEcl).Transform(_cce2Ccf) - _originCcf;
        return new V3(double3.Dot(rel, _upCcf), double3.Dot(rel, _eastCcf), double3.Dot(rel, _northCcf));
    }

    public V3 DirectionFromCcf(double3 ccf) =>
        new(double3.Dot(ccf, _upCcf), double3.Dot(ccf, _eastCcf), double3.Dot(ccf, _northCcf));

    public V3 DirectionFromEcl(double3 ecl)
    {
        double3 rel = ecl.Transform(_cce2Ccf);
        return new V3(double3.Dot(rel, _upCcf), double3.Dot(rel, _eastCcf), double3.Dot(rel, _northCcf));
    }

    public static SiteFrame? FromVehicle(Vehicle vehicle, string? wantedId, out string how) =>
        FromVehicle(vehicle, wantedId, null, out how);

    // The order is the named Landmark, the anchor of the take, the nearest Landmark within
    // NearestLimitMeters, and else the ground under the vehicle. A named Landmark or an anchor that
    // cannot be found gives no frame, because the shots were made for that place.
    public static SiteFrame? FromVehicle(Vehicle vehicle, string? wantedId, WorldAnchor? anchor, out string how)
    {
        how = "";
        if (vehicle.Parent is not Celestial body)
        {
            how = "the vehicle does not orbit a body with a surface";
            return null;
        }

        // Vehicle.GetPositionCci is relative to the center of the parent body.
        double3 positionCcf = vehicle.GetPositionCci().Transform(body.GetCci2Ccf());
        return FromPosition(body, positionCcf, wantedId, anchor, out how);
    }

    // The same rules for a point in CCF, such as the start of a recording.
    public static SiteFrame? FromPosition(Celestial body, double3 positionCcf, string? wantedId, WorldAnchor? anchor, out string how)
    {
        how = "";
        if (!string.IsNullOrWhiteSpace(wantedId))
        {
            if (body.GetLocation(wantedId) is LandmarkReference named)
            {
                how = $"Landmark \"{named.Key}\" named by the shot file";
                return new SiteFrame(body, named);
            }

            how = $"the shot file names the site \"{wantedId}\", but {body.Id} has no such Landmark";
            return null;
        }

        if (anchor != null)
        {
            Celestial? anchorBody = string.Equals(anchor.Body, body.Id, StringComparison.Ordinal)
                ? body
                : Universe.CurrentSystem?.Get(anchor.Body) as Celestial;
            if (anchorBody == null)
            {
                how = $"the take has an anchor on \"{anchor.Body}\", but the system has no such body";
                return null;
            }

            how = $"anchor of the take on {anchorBody.Id} at {AnchorText(anchor)}";
            return new SiteFrame(anchorBody, anchor, "anchor");
        }

        LandmarkReference? nearest = Nearest(body, positionCcf.Normalized(), out double distance);
        if (nearest != null)
        {
            how = FormattableString.Invariant($"Landmark \"{nearest.Key}\" nearest to the vehicle ({distance:F0} m)");
            return new SiteFrame(body, nearest);
        }

        V3 direction = new(positionCcf.X, positionCcf.Y, positionCcf.Z);
        WorldAnchor ground = WorldAnchor.FromDirection(body.Id, direction, 0.0);
        how = FormattableString.Invariant($"no Landmark within {NearestLimitMeters:F0} m, so the ground under the vehicle on {body.Id} at {AnchorText(ground)}");
        return new SiteFrame(body, ground, "ground");
    }

    public static LandmarkReference? Nearest(Celestial body, Vehicle vehicle, out double distanceMeters) =>
        Nearest(body, (vehicle.GetPositionEcl() - body.GetPositionEcl()).Normalized().Transform(body.GetCce2Ccf()), out distanceMeters);

    private static LandmarkReference? Nearest(Celestial body, double3 dir, out double distanceMeters)
    {
        LandmarkReference? best = null;
        distanceMeters = NearestLimitMeters;
        foreach (LocationReference location in body.BodyTemplate.Locations)
        {
            if (location is not LandmarkReference landmark)
                continue;
            double distance = (landmark.ForwardCcf - dir).Length() * body.MeanRadius;
            if (distance < distanceMeters)
            {
                distanceMeters = distance;
                best = landmark;
            }
        }

        return best;
    }

    public static LandmarkReference? FirstLaunchPad(Celestial body)
    {
        foreach (LocationReference location in body.BodyTemplate.Locations)
        {
            if (location is LandmarkReference { IsLaunchPad: true } pad)
                return pad;
        }

        return null;
    }

    private static string AnchorText(WorldAnchor anchor) => string.Create(CultureInfo.InvariantCulture,
        $"latitude {anchor.Latitude:F5}, longitude {anchor.Longitude:F5}, {anchor.Altitude:F1} m above the terrain");

    private static double3 ToDouble3(V3 v) => new(v.X, v.Y, v.Z);
}
