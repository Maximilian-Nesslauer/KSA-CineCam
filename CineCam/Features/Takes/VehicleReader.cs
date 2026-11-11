using Brutal.Numerics;
using CineCam.Director;
using KSA;

namespace CineCam.Features.Takes;

// The body frame of the vehicle (Vehicle.Body2Cce) has +X toward the nose, +Y right and +Z down.
// The assembly frame has the same orientation, and Vehicle.PosAsmbToBody only subtracts the center
// of mass.
internal sealed class VehicleReader
{
    // The extent changes mostly when parts leave, which the part count check catches at once, so
    // the timed read can be slow.
    private const double ExtentIntervalSeconds = 0.5;

    private Vehicle _vehicle;
    private int _partCount = -1;
    private int _knownCount = -1;
    private double _nextExtent;
    private double _baseX;
    private double _height = 1.0;
    private double _width = 1.0;
    private long _firedCursor;
    private bool _changed;
    private readonly HashSet<EngineController> _engineSeen = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Part> _knownParts = new(ReferenceEqualityComparer.Instance);

    public VehicleReader(Vehicle vehicle)
    {
        _vehicle = vehicle;
        _firedCursor = SequenceWatch.Cursor(vehicle);
    }

    public Vehicle Vehicle => _vehicle;

    // Follows the controlled vehicle when it shares a part object with the known parts. A docking
    // (DockingPort.Dock, Vehicle.MergeFrom on the other vehicle), an undocking (Vehicle.Split) and a
    // breakup (PartFailureEvent) make or keep another Vehicle, but the Part objects move with it.
    public bool TryFollow(Vehicle current)
    {
        if (_knownParts.Count == 0)
            return false;
        ReadOnlySpan<Part> parts = current.Parts.Parts;
        bool shared = false;
        for (int i = 0; i < parts.Length && !shared; i++)
            shared = _knownParts.Contains(parts[i]);
        if (!shared)
            return false;

        _vehicle = current;
        _firedCursor = SequenceWatch.Cursor(current);
        _partCount = -1;
        _knownCount = -1;
        _nextExtent = 0.0;
        _changed = true;
        return true;
    }

    public double Height => _height;

    // The larger of the two sizes across the axis of the vehicle.
    public double Width => _width;

    // The vehicle solver workers may still run here, because they start at the end of
    // Program.PrepareFrame and the game waits for them in the next frame. The sample reads single
    // numbers, so a value one step old does no harm to an event check.
    public VehicleSample Sample(int marks = 0)
    {
        bool engineOn = ReadEngines(out int fresh);
        bool changed = _changed;
        _changed = false;
        bool contact = _vehicle.Situation.HasAnyContact();

        // IParentBody.GetNearSurfaceRadius is the highest terrain plus 1 m on an airless body, where
        // GetAtmosphereRadius is 0. On a body with an atmosphere the outside test already rejects a
        // hop, so the periapsis limit applies only to an airless body, and a coast after the ascent
        // with the periapsis still in the atmosphere counts as stable.
        IParentBody parent = _vehicle.Parent;
        double3 position = _vehicle.GetPositionCci();
        double distance = position.Length();
        double radial = distance > 1e-6 ? double3.Dot(position, _vehicle.GetVelocityCci()) / distance : 0.0;
        Orbit orbit = _vehicle.Orbit;
        bool stable = orbit.GetOrbitType() == Orbit.OrbitType.Elliptical
            && (parent.GetAtmosphereRadius() > 0.0 || orbit.Periapsis >= parent.GetNearSurfaceRadius());
        bool outside = !contact && distance > parent.GetAtmosphereRadius();

        return new VehicleSample(
            engineOn,
            contact,
            _vehicle.Parts.SequenceList.ActiveSequence,
            _vehicle.GetBarometricAltitude(),
            _vehicle.GetSurfaceSpeed(),
            changed ? null : SequenceWatch.Since(_vehicle, ref _firedCursor),
            changed ? 0 : fresh,
            parent.Id,
            radial,
            stable,
            outside,
            DockedPorts(),
            marks,
            changed);
    }

    private int DockedPorts()
    {
        Module<DockingPort>.List? list = _vehicle.Parts.DockingPorts;
        if (list == null)
            return 0;
        Span<DockingPort> ports = list.Modules;
        int docked = 0;
        for (int i = 0; i < ports.Length; i++)
        {
            if (ports[i].Docked)
                docked++;
        }

        return docked;
    }

    // Reaction control thrusters have their own cores and are not counted. The fresh count is the
    // number of running engines not seen running in an earlier sample of this reader.
    private bool ReadEngines(out int fresh)
    {
        fresh = 0;
        bool any = false;
        PartTree parts = _vehicle.Parts;
        Span<EngineController> engines = parts.Modules.Get<EngineController>();
        for (int i = 0; i < engines.Length; i++)
        {
            RocketCore[] cores = engines[i].Cores;
            bool on = false;
            for (int j = 0; j < cores.Length && !on; j++)
            {
                ref readonly RocketCoreState state = ref parts.RocketCores.GetStateByIdx(cores[j].StatesIdx);
                on = state.Throttle > 0f;
            }

            if (!on)
                continue;
            any = true;
            if (_engineSeen.Add(engines[i]))
                fresh++;
        }

        return any;
    }

    // The recorder calls this without RefreshExtent, which it does not need.
    public void RefreshParts()
    {
        int count = _vehicle.Parts.Count;
        if (count == _knownCount)
            return;
        _knownParts.Clear();
        ReadOnlySpan<Part> parts = _vehicle.Parts.Parts;
        for (int i = 0; i < parts.Length; i++)
            _knownParts.Add(parts[i]);
        _knownCount = count;
    }

    public void RefreshExtent(double realSeconds, bool force = false)
    {
        int count = _vehicle.Parts.Count;
        if (!force && count == _partCount && realSeconds < _nextExtent)
            return;
        RefreshParts();
        _partCount = count;
        _nextExtent = realSeconds + ExtentIntervalSeconds;
        (double3 min, double3 max) = _vehicle.Parts.ComputeBoundingBoxAsmb();
        double centerX = _vehicle.CenterOfMassAsmb.X;
        _baseX = min.X - centerX;
        _height = Math.Max(max.X - min.X, 1.0);
        _width = Math.Max(Math.Max(max.Y - min.Y, max.Z - min.Z), 0.1);
    }

    // In the site frame, with the origin at the base of the vehicle on its axis and the body axes.
    public Basis3 Frame(SiteFrame site)
    {
        doubleQuat body2Cce = _vehicle.Body2Cce;
        double3 origin = _vehicle.GetPositionEcl() + (double3.UnitX * _baseX).Transform(body2Cce);
        return new Basis3(
            site.FromEcl(origin),
            site.DirectionFromEcl(double3.UnitX.Transform(body2Cce)),
            site.DirectionFromEcl(double3.UnitY.Transform(body2Cce)),
            site.DirectionFromEcl(double3.UnitZ.Transform(body2Cce)));
    }

    // Both in the site frame. The centre is of Vehicle.Parent, the body of the sphere of influence,
    // and the pole is of the site body.
    public void ReadHorizon(SiteFrame site, out V3 bodyCentre, out V3 pole)
    {
        Celestial body = _vehicle.Parent as Celestial ?? site.Body;
        bodyCentre = site.FromEcl(body.GetPositionEcl());
        pole = site.PoleInSite;
    }

    // The unit direction from the vehicle to Universe.WorldSun in the site axes, or null without a sun.
    public V3? ReadSun(SiteFrame site, out double bodyRadius)
    {
        Celestial body = _vehicle.Parent as Celestial ?? site.Body;
        bodyRadius = body.MeanRadius;
        if (Universe.WorldSun is not { } sun)
            return null;
        return site.DirectionFromEcl((sun.GetPositionEcl() - _vehicle.GetPositionEcl()).Normalized());
    }

    // Vehicle.GetSurfaceVelocityCci already subtracts the rotation of the body, so only its axes
    // are turned into CCF.
    public V3 SurfaceVelocity(SiteFrame site)
    {
        Celestial body = _vehicle.Parent as Celestial ?? site.Body;
        return site.DirectionFromCcf(_vehicle.GetSurfaceVelocityCci().Transform(body.GetCci2Ccf()));
    }

    // Without smoothing, for callers that have no take running.
    public V3 RawHeading(SiteFrame site, V3 bodyCentre, V3 pole)
    {
        Basis3 local = LocalHorizon.Frame(Frame(site).Origin, bodyCentre, pole);
        return FlightHeading.Raw(local.X, local.Z, SurfaceVelocity(site));
    }

    // Matches the template id, the instance id or the display name, and returns the position in the
    // vehicle frame. A part loaded from a vehicle save has the instance id of the save as its Id, so
    // a shot file usually names the template id.
    public V3? FindPart(string id)
    {
        ReadOnlySpan<Part> parts = _vehicle.Parts.Parts;
        for (int i = 0; i < parts.Length; i++)
        {
            Part part = parts[i];
            if (!string.Equals(part.Template?.Id, id, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(part.Id, id, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(part.DisplayName, id, StringComparison.OrdinalIgnoreCase))
                continue;
            double3 body = _vehicle.PosAsmbToBody(part.PositionVehicleAsmb);
            return new V3(body.X - _baseX, body.Y, body.Z);
        }

        return null;
    }
}
