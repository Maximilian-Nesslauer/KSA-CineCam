using Brutal.Logging;
using Brutal.Numerics;
using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Features.Takes;
using KSA;
using KSA.Rendering.Lighting;

namespace CineCam.Features.Replay;

internal enum ReplayCameraMode { Take, Free }

internal sealed class ReplayScene : IDisposable
{
    private sealed record Model(ReplaySegment Segment, PartTree Parts, RocketNozzle[] Nozzles, ReplayShape Shape,
        double3 Center, double Radius)
    {
        public ReplayPartState? PartState { get; set; }
    }
    private readonly Dictionary<ReplaySegment, Model> _models = new(ReferenceEqualityComparer.Instance);
    private readonly List<(Model Model, ReplaySample Sample)> _visible = [];
    private readonly HashSet<VolumetricExhaustInstance> _seenPlumes = new(ReferenceEqualityComparer.Instance);
    private readonly List<ExhaustLight> _lights = [];
    private readonly Celestial _body;
    private readonly StellarBody? _sun;
    private readonly ReplayLiveState _liveState = new();
    private readonly ReplayCamera _camera;
    private double _lastFrameTime;
    private bool _partStateFailed;

    private static Camera Camera => Program.MainViewport.BaseCamera;
    public ReplayFlight Flight { get; }
    public CelestialSystem System { get; }
    public Celestial Body => _body;
    public ReplayWorld World { get; }
    public ReplayTakePlayer Player { get; }

    // The site frame of the take that plays. It is fixed in the body-fixed frame like the site of a live take.
    public SiteFrame Site { get; private set; }

    // Why the take plays in another site than its own, or an empty text.
    public string SiteNote { get; private set; } = "";

    /// <summary>The recording time that the replay shows.</summary>
    public double Time => Player.RecordingTime;
    public double RecordingDuration => World.Duration;
    public double TakeTime => Player.Time;
    public double Duration => Player.Duration;
    // VolumetricExhaustInstance.UpdateState reads a pulse start time of 0 as no pulse and a shutdown
    // time of 0 or less as no shutdown, so the plume clock starts later than any burn age before it.
    private const double PlumeClockOffset = 60.0;

    public double VisualTime => PlumeClockOffset + Time;

    /// <summary>The game time of the recorded moment that the replay shows.</summary>
    public double GameTime => Flight.StartTime + Time;
    public int? FollowActorId => _camera.FollowActorId;
    public double FieldOfView => _camera.FieldOfView;
    public bool IsPlaying => Player.IsPlaying;
    public ReplayCameraMode CameraMode { get; private set; } = ReplayCameraMode.Free;

    public ReplayScene(ReplayFlight flight, CelestialSystem system, Celestial body)
    {
        Flight = flight;
        System = system;
        _body = body;
        _sun = Universe.WorldSun;
        _camera = new ReplayCamera(body, id => Sample(id, Time));

        try
        {
            foreach (ReplaySegment segment in flight.Actors.SelectMany(actor => actor.Segments))
            {
                PartTree parts = ReplayParts.Decode(segment.Definition);
                try
                {
                    (double3 min, double3 max) = parts.ComputeBoundingBoxAsmb();
                    RocketNozzle[] nozzles = ReplayParts.OrderedNozzles(parts);
                    // An empty engine list means that no engine burns in that sample.
                    if (segment.Samples.Any(sample => sample.Nozzles.Length != 0 && sample.Nozzles.Length != nozzles.Length))
                        throw new InvalidDataException("A recorded engine layout differs from the installed parts.");
                    var shape = ReplayShape.FromBounds(FlightRecorder.ToV3(min), FlightRecorder.ToV3(max), segment.CenterOfMass,
                        id => FindPart(parts, id));
                    _models.Add(segment, new Model(segment, parts, nozzles, shape, (min + max) * 0.5,
                        Math.Max(2, (max - min).Length() * 0.5)) { PartState = PartStateOf(segment, parts) });
                }
                catch
                {
                    parts.Dispose();
                    throw;
                }
            }

            World = new ReplayWorld(flight, body.MeanRadius, segment => _models[segment].Shape, SunCcf);
            Player = new ReplayTakePlayer(World);
            Site = SiteFor(null, null, out _);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Enter()
    {
        _liveState.Enter();
        _camera.Follow(World.FollowedAt(Time).Id);
        Update(0);
    }

    public void Dispose()
    {
        try
        {
            _liveState.Dispose();
        }
        finally
        {
            foreach (Model model in _models.Values)
                model.Parts.Dispose();
            _models.Clear();
        }
    }

    /// <summary>
    /// The site frame of a take on this recording, by the rules of a live take at the start of the
    /// recording. A take whose Landmark or anchor is not on the recorded body falls back to the
    /// ground under the start, and the note says why.
    /// </summary>
    public SiteFrame SiteFor(string? siteId, WorldAnchor? anchor, out string note) => SiteFor(siteId, anchor, out note, out _);

    private SiteFrame SiteFor(string? siteId, WorldAnchor? anchor, out string note, out bool fellBack)
    {
        fellBack = false;
        double3 start = ReplayCamera.Vector(Flight.Actors[0].Segments[0].Samples[0].Position);
        SiteFrame? site = SiteFrame.FromPosition(_body, start, siteId, anchor, out note);
        if (site != null && ReferenceEquals(site.Body, _body))
            return site;
        string reason = note;
        fellBack = true;
        site = SiteFrame.FromPosition(_body, start, null, null, out note);
        note = $"{reason}; the replay uses {note}";
        return site!;
    }

    /// <summary>The take, its file and its site on this recording, ready for a resolve on any thread.</summary>
    public sealed record TakeRequest(Take? Take, ShotFile? File, SiteFrame Site, string SiteNote);

    /// <summary>The site of a take on this recording. It reads the game, so it runs on the main thread.</summary>
    public TakeRequest PrepareTake(Take? take, ShotFile? file)
    {
        SiteFrame site = SiteFor(take?.Site ?? file?.Site, take?.Anchor, out string note, out bool fellBack);
        return new TakeRequest(take, file, site, fellBack ? note : "");
    }

    /// <summary>Plays a take of the editor, or no take for null.</summary>
    public void LoadTake(Take? take, ShotFile? file)
    {
        TakeRequest request = PrepareTake(take, file);
        ReplayTakePlan plan = ReplayTakePlayer.Resolve(World, take, file, out string problem);
        ApplyTake(request, plan, problem, false);
    }

    /// <summary>
    /// Plays a resolved plan at the same moment of the flight. The plumes start again only when the
    /// moment changed, so an edit of the take does not restart the burns on the screen.
    /// </summary>
    public void ApplyTake(TakeRequest request, ReplayTakePlan plan, string problem, bool rough)
    {
        double before = Time;
        Site = request.Site;
        SiteNote = request.SiteNote;
        Player.Apply(plan, problem, Site.ToReplaySite(), rough);
        AfterJump(Math.Abs(Time - before) > 1e-9);
    }

    public void Seek(double takeTime, bool rough = false)
    {
        Player.Seek(takeTime, rough);
        AfterJump();
    }

    /// <summary>Makes the state of a rough seek exact, at the same time.</summary>
    public void Settle()
    {
        if (!Player.IsRough)
            return;
        Player.Settle();
        AfterJump(false);
    }

    public void SetPlaying(bool enabled)
    {
        if (Player.SetPlaying(enabled))
            AfterJump();
    }

    public void SetPreviewSpeed(double speed) => Player.PreviewSpeed = speed;

    // The take camera starts from the pose of the take at once. The free camera starts where the
    // camera is and follows the vehicle that the take follows.
    public void SetCameraMode(ReplayCameraMode mode)
    {
        CameraMode = mode;
        _liveState.Hold(mode == ReplayCameraMode.Take);
        _camera.Keep(World.FollowedAt(Time).Id);
        if (mode == ReplayCameraMode.Take)
            ApplyTake();
    }

    public bool CanFollow(int actorId) => Sample(actorId, Time) != null;

    public void Follow(int? actorId)
    {
        CameraMode = ReplayCameraMode.Free;
        _liveState.Hold(false);
        _camera.Follow(actorId);
    }

    public void SetFov(double fov)
    {
        CameraMode = ReplayCameraMode.Free;
        _liveState.Hold(false);
        _camera.SetFieldOfView(fov);
    }

    public void Update(double deltaTime)
    {
        _liveState.Hold(CameraMode == ReplayCameraMode.Take);
        double before = Time;
        Player.Advance(Math.Max(0, deltaTime));
        if (Time < before)
            ResetPlumes();

        _visible.Clear();
        foreach (ReplayActor actor in Flight.Actors)
        {
            if (ReplayTimeline.Sample(actor, Time) is { } pose)
                _visible.Add((_models[pose.Segment], pose.Sample));
        }

        if (CameraMode == ReplayCameraMode.Take)
            ApplyTake();
        else
            _camera.UpdateFollow();
    }

    // The pose of the take, or the camera following the followed vehicle while no shot runs.
    private void ApplyTake()
    {
        Site.Refresh();
        int followed = World.FollowedAt(Time).Id;
        if (Player.Frame?.Pose is CameraPose pose)
        {
            _camera.Apply(Site, pose);
            _camera.Keep(followed);
        }
        else if (_camera.FollowActorId != followed)
        {
            _camera.Keep(followed);
        }
        else
        {
            _camera.UpdateFollow();
        }
    }

    private void AfterJump(bool moved = true)
    {
        _lastFrameTime = Time;
        if (moved)
            ResetPlumes();
        if (CameraMode == ReplayCameraMode.Take)
            ApplyTake();
        else
            _camera.UpdateFollow();
    }

    /// <summary>The camera pose of a saved view of an old replay, or null when its vehicle or part is not in the recording at its time.</summary>
    public LegacyCamera? LegacyCameraOf(ReplayView view)
    {
        ReplayCameraTarget? target = view.ActorId is int id ? Sample(id, view.Time) : null;
        if (ReplayCamera.ViewPoseCcf(view, target) is not { } pose)
            return null;
        return new LegacyCamera(view.Time, pose.Position, pose.Forward, view.Fov, view.ActorId == Flight.Actors[0].Id, view.Cut);
    }

    private ReplayCameraTarget? Sample(int id, double time)
    {
        ReplayActor? actor = Flight.Actors.Find(candidate => candidate.Id == id);
        if (actor == null || ReplayTimeline.Sample(actor, time) is not { } pose)
            return null;
        Model model = _models[pose.Segment];
        return new ReplayCameraTarget(pose.Segment, pose.Sample, model.Parts, model.Center, model.Radius);
    }

    // Finds a part as VehicleReader.FindPart does: by template id, instance id or display name. The
    // position is Part.PositionVehicleAsmb, in the assembly frame of the decoded tree.
    private static V3? FindPart(PartTree parts, string id)
    {
        ReadOnlySpan<Part> all = parts.Parts;
        for (int i = 0; i < all.Length; i++)
        {
            Part part = all[i];
            if (string.Equals(part.Template?.Id, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(part.Id, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(part.DisplayName, id, StringComparison.OrdinalIgnoreCase))
                return FlightRecorder.ToV3(part.PositionVehicleAsmb);
        }

        return null;
    }

    // The position of the sun in the body-fixed frame at a recording time, from the orbit of the sun
    // at the game time of that moment.
    private V3? SunCcf(double time)
    {
        if (_sun == null)
            return null;
        var at = new UniverseTime(Flight.StartTime + time);
        double3 sun = _sun is IOrbiter orbiting ? orbiting.GetPositionEcl(at) : _sun.GetPositionEcl();
        return FlightRecorder.ToV3((sun - _body.GetPositionEcl(at)).Transform(_body.GetCce2Ccf(at)));
    }

    // The recorded part state of a segment, or null when the segment has none or its channels do
    // not fit the decoded tree. Then the parts keep the state that the part tree definition holds.
    private static ReplayPartState? PartStateOf(ReplaySegment segment, PartTree parts)
    {
        if (segment.Channels is not { Count: > 0 })
            return null;
        ReplayPartState state = ReplayPartState.Of(parts);
        if (state.Matches(segment))
            return state;
        DefaultCategory.Log.Warning($"[CineCam] The replay shows the parts of a segment at {segment.Start:0.###} s without their recorded state, because the part modules differ from the recording.");
        return null;
    }

    public void DrawModels(IViewport viewport, int frameIndex)
    {
        foreach ((Model model, ReplaySample sample) in _visible)
        {
            if (model.PartState != null)
                ApplyPartState(model, sample.Time);
            (double3 position, doubleQuat orientation) = ReplayCamera.WorldPose(_body, sample);
            double4x4 transform = double4x4.CreateFromQuaternion(orientation) *
                double4x4.CreateTranslation(position - Camera.PositionEcl);
            model.Parts.UpdateRenderData(in transform, false, viewport, frameIndex);
        }
    }

    // The part state is a part of the picture, not of the flight, so a model whose state fails keeps
    // the state that it has and the replay goes on.
    private void ApplyPartState(Model model, double time)
    {
        try
        {
            model.PartState!.Apply(model.Segment.Channels!, time);
        }
        catch (Exception ex)
        {
            model.PartState = null;
            if (!_partStateFailed)
                DefaultCategory.Log.Warning($"[CineCam] The replay shows the parts of a segment at {model.Segment.Start:0.###} s without their recorded state after an error: {ex.Message}");
            _partStateFailed = true;
        }
    }

    public bool TrySunPosition(Camera camera, IPosition target, out double3 position)
    {
        position = default;
        if (!ReferenceEquals(camera, Camera) || _sun == null || !ReferenceEquals(target, _sun) || SunCcf(Time) is not V3 sunCcf)
            return false;
        position = _body.GetPositionEcl() + ReplayCamera.Vector(sunCcf).Transform(_body.GetCcf2Cce()) - camera.PositionEcl;
        return true;
    }

    public void DrawPlumes(VolumetricExhaustRenderer renderer)
    {
        double delta = Math.Max(0, Time - _lastFrameTime);
        _lastFrameTime = Time;
        _lights.Clear();
        int modelIndex = -1;
        foreach ((Model model, ReplaySample sample) in _visible)
        {
            modelIndex++;
            (double3 origin, doubleQuat orientation) = ReplayCamera.WorldPose(_body, sample);
            origin -= Camera.PositionEcl;
            float3 air = float3.Pack(ReplayCamera.Vector(sample.AirVelocity).Transform(_body.GetCcf2Cce()));
            var states = model.Parts.RocketNozzles.GetModulesAndAllStates(model.Nozzles);
            int index = -1;
            while (states.MoveNext())
            {
                var nozzle = states.Current;
                ReplayNozzle frame = ++index < sample.Nozzles.Length ? sample.Nozzles[index] : default;
                if (nozzle.FxState.VolumetricExhaust is not { } instance)
                    continue;
                if (!IsBurning(frame))
                {
                    if (_seenPlumes.Remove(instance) && instance.Template is { } template)
                        nozzle.FxState.VolumetricExhaust = new VolumetricExhaustInstance(template);
                    continue;
                }
                RocketNozzleState state = RocketNozzleState.Zero;
                state.Exhaust = new GasConditions { Pressure = frame.ExhaustPressure, Temperature = frame.Temperature };
                state.ExhaustVelocity = frame.ExhaustVelocity;
                RocketCoreConditions core = new()
                {
                    Gas = new GasProperties { Gamma = frame.Gamma, SpecificGasConstant = frame.SpecificGasConstant },
                    Core = new GasConditions { Pressure = frame.ChamberPressure, Temperature = frame.ChamberTemperature },
                    Exit = new GasConditions { Pressure = frame.ExitPressure, Temperature = frame.ExitTemperature }
                };
                double3 positionEgo = origin + ReplayCamera.Vector(frame.Position).Transform(orientation);
                double3 axisEgo = ReplayCamera.Vector(frame.Axis).Transform(orientation).NormalizeOrZero();
                float3 position = float3.Pack(positionEgo);
                float3 axis = float3.Pack(axisEgo);
                float3 restPosition = float3.Pack(origin + ReplayCamera.Vector(frame.RestPosition).Transform(orientation));
                float3 restAxis = float3.Pack(ReplayCamera.Vector(frame.RestAxis).Transform(orientation).NormalizeOrZero());
                double step = delta;
                if (_seenPlumes.Add(instance))
                {
                    // The game renderer builds the ignition pulse over several frames, so a plume
                    // that first shows in the middle of a burn gets its age at once, also while paused.
                    double age = BurnAge(model.Segment.Samples, index);
                    instance.UpdateState(VisualTime - age, true, 0, in core.Gas, in state.Exhaust,
                        state.ExhaustVelocity, position, axis, restPosition, restAxis, sample.AmbientPressure, air, sample.AirDensity);
                    step = age;
                }
                bool lit = ReplayExhaust.Lit(frame.DutyCycle, Time, modelIndex * 4096 + index);
                renderer.AddPlume(instance, nozzle.Module, in state, in core, VisualTime, lit,
                    step, position, axis, restPosition, restAxis, sample.AmbientPressure, air, sample.AirDensity);
                if (lit && nozzle.FxState.ExhaustLight)
                    _lights.Add(new ExhaustLight(instance, positionEgo, axisEgo, model.Segment.Samples, index));
            }
        }
    }

    private readonly record struct ExhaustLight(VolumetricExhaustInstance Instance, double3 Position, double3 Axis,
        List<ReplaySample> Samples, int Nozzle);

    /// <summary>
    /// The light of the burning recorded plumes, by the rule of Vehicle.UpdateExhaustLight: a point
    /// light 0.1 m along the exhaust axis, with the brightness from the plume density that the
    /// renderer computed in EndPlumeGrouping, and the startup curve of the plume template. A plume
    /// that stops burning loses its light at once, as the replay plume ends at once.
    /// </summary>
    public void DrawExhaustLights()
    {
        foreach (ExhaustLight light in _lights)
        {
            VolumetricExhaustTemplate? template = light.Instance.Template;
            if (template == null)
                continue;
            double size = 1.0;
            if (template.StartupTransient is { } startup && template.ShutdownTransient != null && startup.Duration.Seconds > 0)
                size = startup.EmissiveSizeCurve.Evaluate(Math.Min(1.0, BurnAge(light.Samples, light.Nozzle) / startup.Duration.Seconds));
            float density = (float)(double)template.Absorption.Density;
            float scattering = (float)(double)template.Absorption.ScatteringBrightness;
            float brightness = (float)(double)template.Emission.Brightness;
            float plumeDensity = light.Instance.LastPlumeData.Density;
            float radius = light.Instance.LastPlumeData.NozzleExitRadius;
            float length = radius * 20f;
            float absorbed = 1f - float.Exp(-density * plumeDensity * length);
            float emitted = brightness * (density > 1e-6f ? absorbed / density : plumeDensity * length);
            float intensity = float.Max(scattering * absorbed, emitted) * 4f * MathF.PI * radius * radius;
            if (intensity.IsNearlyZero())
                continue;
            Program.LightSystem.CreateLightInstance(Light.CreatePointLight(light.Position + light.Axis * 0.10000000149011612,
                2f * (float)size, template.Emission.ColorGradient.WeightedAverageColor, intensity * (float)size,
                ELightFlags.CastsShadows | ELightFlags.SoftShadows), Program.MainViewport);
        }
        _lights.Clear();
    }

    private void ResetPlumes()
    {
        _seenPlumes.Clear();
        foreach (Model model in _models.Values)
        {
            var states = model.Parts.RocketNozzles.ModulesAndAllStates.GetEnumerator();
            while (states.MoveNext())
            {
                var nozzle = states.Current;
                if (nozzle.FxState.VolumetricExhaust is { Template: { } template })
                    nozzle.FxState.VolumetricExhaust = new VolumetricExhaustInstance(template);
            }
        }
    }

    private double BurnAge(List<ReplaySample> samples, int nozzle)
    {
        int lo = 0, hi = samples.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (samples[mid].Time <= Time)
                lo = mid + 1;
            else
                hi = mid;
        }
        double start = Time;
        for (int i = lo - 1; i >= 0; i--)
        {
            ReplaySample sample = samples[i];
            if (nozzle >= sample.Nozzles.Length || !IsBurning(sample.Nozzles[nozzle]))
                break;
            start = sample.Time;
            if (Time - start >= 5)
                break;
        }
        return Math.Clamp(Time - start, 0.0, 5.0);
    }

    private static bool IsBurning(ReplayNozzle frame) =>
        frame.DutyCycle > 0 && frame.ExhaustPressure > 0 && frame.ExhaustVelocity > 0;
}
