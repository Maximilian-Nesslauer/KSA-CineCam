using Brutal.Numerics;
using CineCam.Core;
using CineCam.Director;
using CineCam.Features.Quick;
using CineCam.Features.Replay;
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
    private long _hookCursor;
    private List<FlightHooks.Entry>? _carried;
    private bool _changed;
    private readonly HashSet<EngineController> _engineSeen = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Part> _knownParts = new(ReferenceEqualityComparer.Instance);

    public VehicleReader(Vehicle vehicle)
    {
        _vehicle = vehicle;
        _firedCursor = SequenceWatch.Cursor(vehicle);
        _hookCursor = FlightHooks.Cursor(vehicle);
    }

    public Vehicle Vehicle => _vehicle;

    /// <summary>False when the private Vehicle._control is not found, so control-lost has no reason.</summary>
    public static bool ControlReasonAvailable { get; set; }

    public static void ResetReflection() => ControlReasonAvailable = false;

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

        // The EVAs, boardings and explosions of the vehicle before that no sample read yet go into the next sample,
        // because a part failure explodes the part on the vehicle before the game splits it.
        if (FlightHooks.Since(_vehicle, ref _hookCursor) is { } pending)
            (_carried ??= new List<FlightHooks.Entry>()).AddRange(pending);
        _vehicle = current;
        _firedCursor = SequenceWatch.Cursor(current);
        _hookCursor = FlightHooks.Cursor(current);
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
    // numbers, so a value one step old does no harm to an event check. The part bounding box of the
    // height over the ground is the exception: an animation step on a worker can write the box of a
    // part (Part.BoundingBoxVehicleAsmb) while it is read, so a read can mix the corners of two
    // animation steps of one box. Both are real boxes of the part, so that is harmless and cannot crash.
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
        ReadHooks(out int evas, out int boardings, out List<string>? explosions);
        ReadProbes(out int rebooting, out int failed, out PowerWarning power);
        bool? controllable = _vehicle.HasControlModule ? _vehicle.IsControllable : null;
        MissionPlan? mission = _vehicle.Parts.Mission;

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
            changed,
            GroundHeight(),
            Controllable: controllable,
            ControlReason: controllable == false ? ControlReason() : null,
            ProbesRebooting: rebooting,
            ProbesFailed: failed,
            ProbePower: power,
            LifeSupport: LifeSupportLevel(),
            Incapacitated: Incapacitated(),
            MissionArrived: mission?.ArrivedAt.HasValue == true,
            MissionBody: mission?.Destination == MissionDestination.Body ? mission.BodyId : null,
            Evas: evas,
            Boardings: boardings,
            Explosions: explosions,
            GimbalLimit: GimbalLimit(),
            DynamicPressure: _vehicle.ComputeDynamicPressure());
    }

    /// <summary>
    /// The explosions of the vehicle that no sample read yet, for a vehicle that the game destroyed, so the take and
    /// the recording can log them before they end.
    /// </summary>
    public List<string> TakeExplosions()
    {
        ReadHooks(out _, out _, out List<string>? explosions);
        return explosions ?? [];
    }

    private void ReadHooks(out int evas, out int boardings, out List<string>? explosions)
    {
        evas = 0;
        boardings = 0;
        explosions = null;
        List<FlightHooks.Entry>? entries = FlightHooks.Since(_vehicle, ref _hookCursor);
        if (_carried != null)
        {
            if (entries != null)
                _carried.AddRange(entries);
            entries = _carried;
            _carried = null;
        }

        foreach (FlightHooks.Entry entry in entries ?? [])
        {
            switch (entry.Kind)
            {
                case FlightHooks.Kind.Eva:
                    evas++;
                    break;
                case FlightHooks.Kind.Board:
                    boardings++;
                    break;
                default:
                    (explosions ??= new List<string>()).Add(entry.Detail);
                    break;
            }
        }
    }

    // Vehicle.UpdateControl computes the private VehicleControl, and its name is the reason, for example
    // "probe core unpowered". Vehicle.NoControlTooltip has the same reasons only as translated texts.
    private string? ControlReason()
    {
        if (!ControlReasonAvailable)
            return null;
        try
        {
            return GameReflection.Vehicle_Control!.GetValue(_vehicle) is VehicleControl control ? EventText.Words(control.ToString()) : null;
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("control-reason", $"[CineCam] The reason of a loss of control could not be read: {ex.Message}");
            return null;
        }
    }

    // The avionics of all parts, as Vehicle.CreditAvionics credits them, and the power of the probe cores as
    // Vehicle.ScanControls finds them: a Control part outside every HabitatZone, with its PowerConsumer that has a
    // power switch. PowerConsumer.ComputeStatus is the status light of the game, Low below PowerForecast.LOW_SECONDS.
    private void ReadProbes(out int rebooting, out int failed, out PowerWarning power)
    {
        rebooting = 0;
        failed = 0;
        power = PowerWarning.None;
        PartTree tree = _vehicle.Parts;
        double now = Universe.GetElapsedSeconds();
        foreach (Avionics avionics in tree.Modules.Get<Avionics>())
        {
            if (avionics.ComputeHealth() == AvionicsHealth.Failed)
                failed++;
            else if (avionics.IsRebooting(now))
                rebooting++;
        }

        foreach (Control control in tree.Modules.Get<Control>())
        {
            if (HabitatZone.FindZone(tree, control.Parent) != null)
                continue;
            foreach (PowerConsumer consumer in control.Parent.Modules.Get<PowerConsumer>())
            {
                if (!consumer.PowerSwitch)
                    continue;
                PowerWarning level = consumer.ComputeStatus(tree) switch
                {
                    PowerStatus.Out => PowerWarning.Out,
                    PowerStatus.Low => PowerWarning.Low,
                    _ => PowerWarning.None,
                };
                if (level > power)
                    power = level;
                break;
            }
        }
    }

    // The game warns of life support and incapacitates kittens only with the life support rule.
    private SupplyWarning LifeSupportLevel()
    {
        if (!Universe.Rules.LifeSupport)
            return SupplyWarning.None;
        return _vehicle.LifeSupportForecast.Level switch
        {
            KSA.LifeSupportLevel.Low => SupplyWarning.Low,
            KSA.LifeSupportLevel.Critical => SupplyWarning.Critical,
            KSA.LifeSupportLevel.Out => SupplyWarning.Out,
            _ => SupplyWarning.None,
        };
    }

    // The kittens in the seats that the roster rates incapacitated, as Vehicle.CreditCrewStats finds the able zones.
    private int Incapacitated()
    {
        if (!Universe.Rules.LifeSupport)
            return 0;
        UniverseTime now = Universe.GetElapsedTime();
        int count = 0;
        foreach (IVASeat seat in _vehicle.Crew)
        {
            if (seat.AssignedKittenHash == KeyHash.Zero)
                continue;
            KittenRosterEntryData? kitten = Universe.KittenRoster.Find(seat.AssignedKittenHash);
            if (kitten != null && !kitten.Kia && kitten.Condition.IsIncapacitated(now))
                count++;
        }

        return count;
    }

    // Only a gimbal that can move (Gimbal.CanActuate) counts.
    private double GimbalLimit()
    {
        var gimbals = _vehicle.Parts.Gimbals;
        if (gimbals == null)
            return 0.0;
        double share = 0.0;
        foreach (var item in gimbals.ModulesAndStates)
        {
            Gimbal gimbal = item.Module;
            if (!gimbal.CanActuate())
                continue;
            ref readonly GimbalState state = ref item.State;
            share = Math.Max(share, GimbalMath.Share(state.AngleY, state.AngleZ, gimbal.AxisY.MaxAngle, gimbal.AxisZ.MaxAngle, gimbal.ConstrainToCircle));
        }

        return share;
    }

    // The part bounding box (PartTree.ComputeBoundingBoxAsmb) is read on every sample, because an animated
    // part such as a landing leg changes its box (KeyframeAnimationModule calls Part.UpdateBounds). The read
    // is one pass over the parts. The recorder stores this value, so a replay sees the same height.
    private double? GroundHeight()
    {
        if (_vehicle.Parent is not Celestial body)
            return null;
        (double3 min, double3 max) = _vehicle.Parts.ComputeBoundingBoxAsmb();
        double3 centreCce = _vehicle.GetPositionCce();
        double height = GroundClearance.Height(QuickScene.ToV3(centreCce), QuickScene.ToV3(min), QuickScene.ToV3(max),
            QuickScene.ToV3(_vehicle.CenterOfMassAsmb), FlightRecorder.ToRotation(_vehicle.Body2Cce), QuickScene.SurfaceRadius(body, centreCce));
        return double.IsFinite(height) ? height : null;
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
        // The state list is null when the vehicle has no rocket at all.
        if (!ModuleStateful<Rocket, RocketState, EmptyStruct, EmptyStruct>.TryGetFrom(parts.States, out var states) || states == null)
            return false;
        Span<EngineController> engines = parts.Modules.Get<EngineController>();
        for (int i = 0; i < engines.Length; i++)
        {
            Rocket[] rockets = engines[i].Rockets;
            bool on = false;
            for (int j = 0; j < rockets.Length && !on; j++)
                on = states.GetState(rockets[j]).Throttle > 0f;

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
