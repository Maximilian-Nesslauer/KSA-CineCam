using Brutal.Numerics;
using CineCam.Director;
using CineCam.Director.Quick;
using KSA;

namespace CineCam.Features.Quick;

// The QuickWorld frame has its origin at the vehicle center of mass and the axes of CCE, which
// has the orientation of ECL, so a point plus Vehicle.GetPositionEcl is an ECL position.
internal sealed class QuickScene
{
    // The extent changes mostly when parts leave, which the part count check catches at once, so
    // the timed read can be slow.
    private const double ExtentIntervalSeconds = 0.5;

    private readonly Vehicle _vehicle;
    private int _partCount = -1;
    private double _nextExtent = double.NegativeInfinity;
    private V3 _centreBody;
    private double _length = 1.0;
    private double _width = 1.0;

    public QuickScene(Vehicle vehicle)
    {
        _vehicle = vehicle;
    }

    public Vehicle Vehicle => _vehicle;

    // The quick camera needs a body with a surface and a rotation, so a barycenter gives null.
    public Celestial? Body => _vehicle.Parent as Celestial;

    public QuickWorld Read(Celestial body, double realSeconds)
    {
        RefreshExtent(realSeconds);
        doubleQuat body2Cce = _vehicle.Body2Cce;
        var axes = new Basis3(V3.Zero, ToV3(double3.UnitX.Transform(body2Cce)),
            ToV3(double3.UnitY.Transform(body2Cce)), ToV3(double3.UnitZ.Transform(body2Cce)));

        double3 positionCce = _vehicle.GetPositionCce();
        doubleQuat cci2Cce = body.GetCci2Cce();
        return new QuickWorld
        {
            Vehicle = axes,
            Centre = axes.DirectionToParent(_centreBody),
            Length = _length,
            Width = _width,
            BodyCentre = ToV3(-positionCce),
            // The rotation axis of the body is +Z of CCF.
            Pole = ToV3(double3.UnitZ.Transform(body.GetCcf2Cce())),
            SurfaceVelocity = ToV3(_vehicle.GetSurfaceVelocityCci().Transform(cci2Cce)),
            InertialVelocity = ToV3(_vehicle.GetVelocityCce()),
            Altitude = _vehicle.GetBarometricAltitude(),
            AtmosphereHeight = body.GetPhysicalAtmosphere()?.Height ?? 0.0,
            Contact = _vehicle.Situation.HasAnyContact(),
        };
    }

    private void RefreshExtent(double realSeconds)
    {
        int count = _vehicle.Parts.Count;
        if (count == _partCount && realSeconds < _nextExtent)
            return;
        _partCount = count;
        _nextExtent = realSeconds + ExtentIntervalSeconds;
        (double3 min, double3 max) = _vehicle.Parts.ComputeBoundingBoxAsmb();
        _centreBody = ToV3(_vehicle.PosAsmbToBody((min + max) * 0.5));
        _length = Math.Max(max.X - min.X, 1.0);
        _width = Math.Max(Math.Max(max.Y - min.Y, max.Z - min.Z), 0.1);
    }

    // The radius below which a camera is under the ground or the sea, for a direction in the CCE
    // axes. GetTerrainHeightFromDirCcf and OceanReference.Level are both heights above the mean radius.
    public static double SurfaceRadius(Celestial body, double3 directionCce)
    {
        double3 directionCcf = directionCce.Normalized().Transform(body.GetCce2Ccf());
        double ground = body.GetTerrainHeightFromDirCcf(directionCcf, accurate: true);
        if (body.GetOceanReference() is { } ocean)
            ground = Math.Max(ground, ocean.Level.InMeters());
        return body.MeanRadius + ground;
    }

    // A ground camera is kept in CCF, which turns with the body.
    public double3 ToCcf(Celestial body, V3 point) =>
        (ToDouble3(point) + _vehicle.GetPositionCce()).Transform(body.GetCce2Ccf());

    public V3 FromCcf(Celestial body, double3 pointCcf) =>
        ToV3(pointCcf.Transform(body.GetCcf2Cce()) - _vehicle.GetPositionCce());

    // A flyby point is kept relative to the body center in the CCE axes.
    public double3 ToCce(V3 point) => ToDouble3(point) + _vehicle.GetPositionCce();

    public V3 FromCce(V3 pointCce) => ToV3(ToDouble3(pointCce) - _vehicle.GetPositionCce());

    public double3 ToEcl(V3 point) => _vehicle.GetPositionEcl() + ToDouble3(point);

    public static V3 ToV3(double3 value) => new(value.X, value.Y, value.Z);

    public static double3 ToDouble3(V3 value) => new(value.X, value.Y, value.Z);
}
