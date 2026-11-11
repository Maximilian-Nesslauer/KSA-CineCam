using System.Diagnostics;
using Brutal.Logging;
using Brutal.Numerics;
using CineCam.Core;
using CineCam.Director;
using CineCam.Director.Quick;
using CineCam.Features.Replay;
using CineCam.Features.Takes;
using CineCam.Features.TimeOfDay;
using CineCam.Patches;
using KSA;

namespace CineCam.Features.Quick;

// Owns the main camera through a CameraRig, as a take does. A take or a replay that starts calls
// Stop first, so only one owner moves the camera at a time.
internal static class QuickCamera
{
    // The quick camera ends when the camera hook missed this many frames in a row, for example
    // because the player chose another camera mode.
    private const int MissedFramesLimit = 5;

    private static Session? _session;
    private static long _frame;
    private static QuickMode? _startRequest;
    private static bool _stopRequested;
    private static bool _nextFlybyRequested;
    private static int _partStepRequest;
    private static double? _speedRequest;
    private static (double Before, double Written)? _pendingSpeed;

    public static QuickOptions Options { get; private set; } = new();

    public static bool IsActive => _session != null;

    public static QuickMode? Mode => _session?.Mode;

    public static string Status { get; private set; } = "";

    // Null when the quick camera did not set a speed.
    public static double? SlowMotion => _session?.WrittenSpeed;

    public static string OnboardPartName => _session?.Parts.SelectedName ?? "";

    public static int OnboardPartIndex => _session?.Parts.Index ?? 0;

    public static int OnboardPartCount => _session?.Parts.Count ?? 0;

    public static FlybyAnchor? FlybyAnchorNow => _session is { Mode: QuickMode.Flyby } s ? s.Anchor : null;

    #region Requests

    public static void RequestStart(QuickMode mode)
    {
        if (TakeRunner.Refused("quick camera", CineCamPatches.CameraBlock))
            return;
        _startRequest = mode;
        _stopRequested = false;
    }

    public static void RequestStop()
    {
        _stopRequested = true;
        _startRequest = null;
    }

    public static void RequestNextFlyby() => _nextFlybyRequested = true;

    public static void RequestPartStep(int step) => _partStepRequest += step;

    public static void RequestSlowMotion(double speed) => _speedRequest = speed;

    // Why the mode cannot start now, or null when it can.
    public static string? StartBlock(QuickMode mode)
    {
        if (CineCamPatches.CameraBlock is { } unsupported)
            return unsupported;
        if (!Program.IsLoaded)
            return "Load a flight first.";
        if (Program.Editor != null)
            return "The quick camera does not work in the vehicle editor.";
        if (TakeRunner.Current is { } take)
            return take.Preview != null ? "A shot preview runs. Stop it first." : "A take runs. Stop it first.";
        if (TakeRunner.Reset.IsActive)
            return "A reset runs. Wait until it is done.";
        if (ReplaySession.IsPlayingBack)
            return "A replay is open. Close it first.";
        Vehicle? vehicle = Program.ControlledVehicle;
        if (vehicle == null || vehicle.IsDisposed)
            return "Select a vehicle in flight.";
        if (vehicle.Parent is not Celestial)
            return "The vehicle must be near a planet or a moon.";
        if (mode == QuickMode.GroundTracker)
        {
            // The tracker stays where the mode started, so a running tracker can always stay on.
            if (_session is { Mode: QuickMode.GroundTracker })
                return null;
            return QuickMath.TrackerBlock(vehicle.GetBarometricAltitude());
        }

        return null;
    }

    public static string? SlowMotionBlock()
    {
        if (_session == null)
            return "Start a quick camera first.";
        if (Universe.IsAutoWarpActive)
            return "An automatic time warp runs. The quick camera does not change the speed then.";
        if (SunWarp.IsRunning)
            return "The game time moves to a sun elevation. Wait until it is done.";
        return null;
    }

    #endregion

    #region Frame hooks

    // Runs first in Program.OnFrame, before the take runner, so a stop is done before a take starts.
    public static void OnFrameStart()
    {
        _frame++;
        try
        {
            ApplyPendingSpeed();
            ProcessRequests();
        }
        catch (Exception ex)
        {
            Fail("a request", ex);
        }
    }

    // Prefix of FixedController.OnFrame of the main viewport, which returns true to skip the stock
    // controller. It skips it always while a quick camera was active at the start of the hook, so
    // the camera keeps the last pose when a mode has no pose for a frame, and after a stop the stock
    // controller does not use the zero offset and rotation that CameraRig.Restore can write back,
    // which give a NaN view direction.
    public static bool OnCameraFrame()
    {
        Session? session = _session;
        if (session == null)
            return false;
        session.LastTickFrame = _frame;
        try
        {
            Tick(session);
        }
        catch (Exception ex)
        {
            Fail("the quick camera", ex);
        }

        return true;
    }

    public static void OnFrameEnd()
    {
        Session? session = _session;
        if (session == null || _frame - session.LastTickFrame <= MissedFramesLimit)
            return;
        try
        {
            Stop("the camera mode changed");
        }
        catch (Exception ex)
        {
            Fail("the guard", ex);
        }
    }

    #endregion

    #region Start and stop

    private static void ProcessRequests()
    {
        if (_stopRequested)
        {
            _stopRequested = false;
            Stop("stopped by the player");
        }

        if (_startRequest is QuickMode mode)
        {
            _startRequest = null;
            Start(mode);
        }

        Session? session = _session;
        if (session == null)
        {
            _nextFlybyRequested = false;
            _partStepRequest = 0;
            _speedRequest = null;
            return;
        }

        if (_nextFlybyRequested)
        {
            _nextFlybyRequested = false;
            session.FlybyPending = true;
        }

        if (_partStepRequest != 0)
        {
            session.Parts.Step(_partStepRequest);
            _partStepRequest = 0;
        }

        if (_speedRequest is double speed)
        {
            _speedRequest = null;
            WriteSpeed(session, speed);
        }
    }

    private static void Start(QuickMode mode)
    {
        string? block = StartBlock(mode);
        if (block != null)
        {
            Status = block;
            return;
        }

        Vehicle vehicle = Program.ControlledVehicle!;
        if (_session is { } running && ReferenceEquals(running.Scene.Vehicle, vehicle))
        {
            // A mode change keeps the rig, so the stop gives back the camera from before the first mode.
            running.Begin(mode);
            Status = $"{Describe(mode)} on.";
            return;
        }

        if (_session != null)
            Stop("the controlled vehicle changed");

        var session = new Session(new QuickScene(vehicle), new CameraRig(), _frame,
            SaveLoadWatch.Available ? SaveLoadWatch.Completed : -1);
        session.Begin(mode);
        _session = session;
        try
        {
            session.Rig.Enter();
        }
        catch (Exception ex)
        {
            Fail("the start", ex);
            return;
        }

        Status = $"{Describe(mode)} on.";
        DefaultCategory.Log.Info($"[CineCam] Quick camera {mode} started on {vehicle.Id}.");
    }

    // Acts at once, not at the next frame, because a take or a replay that starts needs the camera.
    public static void Stop(string reason)
    {
        Session? session = _session;
        if (session == null)
            return;
        _session = null;
        _nextFlybyRequested = false;
        _partStepRequest = 0;
        _speedRequest = null;

        try
        {
            RestoreSpeed(session);
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error($"[CineCam] The sim speed could not be reset: {ex.Message}");
        }

        try
        {
            session.Rig.Restore();
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error($"[CineCam] The camera could not be given back: {ex.Message}");
        }

        Status = $"Quick camera off: {reason}.";
        DefaultCategory.Log.Info($"[CineCam] Quick camera {session.Mode} ended: {reason}.");
    }

    public static void Shutdown()
    {
        _startRequest = null;
        _stopRequested = false;
        _nextFlybyRequested = false;
        _partStepRequest = 0;
        _speedRequest = null;
        try
        {
            Stop("the mod unloaded");
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error($"[CineCam] Shutdown of the quick camera failed: {ex.Message}");
            _session = null;
        }

        HandPendingSpeedToMenu();
        _pendingSpeed = null;
        Status = "";
        Options = new QuickOptions();
    }

    // At an unload in a menu pause no later frame gives the old speed back, so the menu gets it
    // when its saved speed is still the one that the quick camera wrote.
    private static void HandPendingSpeedToMenu()
    {
        if (_pendingSpeed is not { } pending)
            return;
        try
        {
            if (MenuPause.Active && MenuPause.RestoreSpeed is double restore && Math.Abs(restore - pending.Written) <= 1e-6)
                MenuPause.Hand(pending.Before);
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error($"[CineCam] The sim speed could not be reset: {ex.Message}");
        }
    }

    private static void Fail(string where, Exception ex)
    {
        LogHelper.ErrorOnce("quick:" + where, $"[CineCam] Error in {where}: {ex}");
        try
        {
            Stop($"error in {where}: {ex.Message}");
        }
        catch (Exception inner)
        {
            LogHelper.ErrorOnce("quick-fail-stop", $"[CineCam] The quick camera could not end cleanly: {inner}");
            _session = null;
        }
    }

    public static string Describe(QuickMode mode) => mode switch
    {
        QuickMode.Flyby => "Flyby",
        QuickMode.Chase => "Chase",
        QuickMode.Orbit => "Orbit",
        QuickMode.Onboard => "Onboard",
        QuickMode.GroundTracker => "Ground tracker",
        _ => mode.ToString(),
    };

    #endregion

    #region Speed

    // Written once per click, not in every frame, so a speed that the player or the game sets later wins.
    private static void WriteSpeed(Session session, double speed)
    {
        if (SlowMotionBlock() is { } block)
        {
            Status = block;
            return;
        }

        double now = Universe.GetSimulationSpeed();
        if (session.WrittenSpeed is double written && Math.Abs(now - written) > 1e-6)
            session.SpeedBefore = null;
        session.SpeedBefore ??= now > 0.0 ? now : 1.0;
        Universe.SetSimulationSpeed(speed, alert: false);
        session.WrittenSpeed = speed;
    }

    // The speed goes back only when it is still the one that the quick camera wrote. A menu pause
    // holds the speed at 0 and gives back the written speed when it closes, so the old speed waits.
    private static void RestoreSpeed(Session session)
    {
        if (session.WrittenSpeed is not double written || session.SpeedBefore is not double before)
            return;
        if (Universe.IsAutoWarpActive)
            return;
        double now = Universe.GetSimulationSpeed();
        if (now == 0.0)
            _pendingSpeed = (before, written);
        else if (Math.Abs(now - written) <= 1e-6)
            Universe.SetSimulationSpeed(before, alert: false);
    }

    private static void ApplyPendingSpeed()
    {
        if (_pendingSpeed is not { } pending || _session != null)
            return;
        if (Universe.IsAutoWarpActive)
        {
            _pendingSpeed = null;
            return;
        }

        double now = Universe.GetSimulationSpeed();
        if (now == 0.0)
            return;
        _pendingSpeed = null;
        if (Math.Abs(now - pending.Written) <= 1e-6)
            Universe.SetSimulationSpeed(pending.Before, alert: false);
    }

    #endregion

    #region Tick

    private static bool Tick(Session session)
    {
        Vehicle? current = Program.ControlledVehicle;
        if (current == null || current.IsDisposed || session.Scene.Vehicle.IsDisposed)
        {
            Stop("the vehicle is gone");
            return false;
        }

        if (!ReferenceEquals(current, session.Scene.Vehicle))
        {
            Stop("the controlled vehicle changed");
            return false;
        }

        if (Program.Editor != null)
        {
            Stop("the vehicle editor opened");
            return false;
        }

        if (session.SaveLoads >= 0 && SaveLoadWatch.Available && SaveLoadWatch.Completed != session.SaveLoads)
        {
            Stop("a save was loaded");
            return false;
        }

        if (session.Scene.Body is not Celestial body)
        {
            Stop("the vehicle is not near a planet or a moon");
            return false;
        }

        if (!ReferenceEquals(body, session.Body))
        {
            // The flyby and tracker points are stored relative to the old body.
            session.Body = body;
            session.FlybyPending = true;
            if (session.Mode == QuickMode.GroundTracker)
            {
                Stop($"the vehicle left the sphere of influence for {body.Id}");
                return false;
            }
        }

        double real = session.Clock.Elapsed.TotalSeconds;
        double realDt = Math.Clamp(real - session.LastReal, 0.0, 0.5);
        session.LastReal = real;
        double game = Universe.GetElapsedSeconds();
        double gameDt = double.IsNaN(session.LastGame) ? 0.0 : Math.Max(game - session.LastGame, 0.0);
        session.LastGame = game;

        QuickWorld world = session.Scene.Read(body, real);
        CameraPose? pose = session.Mode switch
        {
            QuickMode.Flyby => FlybyPose(session, body, world, gameDt),
            QuickMode.Chase => ChasePose(session, world, realDt),
            QuickMode.Orbit => OrbitPose(session, world, realDt),
            QuickMode.Onboard => OnboardPose(session, world),
            QuickMode.GroundTracker => TrackerPose(session, body, world, realDt),
            _ => null,
        };

        if (pose == null)
            return false;
        Apply(session, pose.Value);
        return true;
    }

    private static CameraPose FlybyPose(Session session, Celestial body, QuickWorld world, double gameDt)
    {
        if (session.FlybyPending)
            PlaceFlyby(session, body, world);
        else
            session.FreeFall?.Advance(gameDt);

        V3 camera = FlybyCamera(session, body, world, out V3 relative);
        if (Options.FlybyAutoNext && QuickMath.FlybyDone(world.Centre - camera, relative, session.PlacedDistance))
        {
            PlaceFlyby(session, body, world);
            camera = FlybyCamera(session, body, world, out _);
        }

        return QuickMath.LookAtVehicle(world, camera, Zoom(world, camera, QuickMath.NormalMinFov));
    }

    // Returns the point in the QuickWorld frame and the velocity of the vehicle relative to it.
    private static V3 FlybyCamera(Session session, Celestial body, QuickWorld world, out V3 relative)
    {
        if (session.Anchor == FlybyAnchor.Orbit && session.FreeFall != null)
        {
            relative = world.InertialVelocity - session.FreeFall.Velocity;
            return session.Scene.FromCce(session.FreeFall.Position);
        }

        relative = world.SurfaceVelocity;
        return session.Scene.FromCcf(body, session.PointCcf);
    }

    // The side changes with each new point, so a series of flybys passes left and right in turn.
    private static void PlaceFlyby(Session session, Celestial body, QuickWorld world)
    {
        session.FlybyPending = false;
        FlybyAnchor anchor = QuickMath.ChooseAnchor(world.Altitude, world.AtmosphereHeight, world.Contact);
        session.Side = -session.Side;
        FlybyPlan plan = QuickMath.PlaceFlyby(world, anchor, Options, session.Side);
        V3 position = KeepAboveGround(session, body, world, plan.Position, QuickMath.GroundClearance);
        session.Anchor = anchor;
        session.PlacedDistance = (position - world.Centre).Length;
        if (anchor == FlybyAnchor.Orbit)
        {
            V3 positionCce = QuickScene.ToV3(session.Scene.ToCce(position));
            session.FreeFall = new FreeFallPoint(positionCce, plan.PointVelocity, ((IParentBody)body).Mu);
        }
        else
        {
            session.FreeFall = null;
            session.PointCcf = session.Scene.ToCcf(body, position);
        }
    }

    private static CameraPose ChasePose(Session session, QuickWorld world, double realDt)
    {
        V3 heading = Heading(session, world, realDt);
        CameraPose pose = QuickMath.ChasePose(world, heading, Options);
        return pose with { Fov = Zoom(world, pose.Position, QuickMath.NormalMinFov) };
    }

    private static CameraPose OrbitPose(Session session, QuickWorld world, double realDt)
    {
        session.OrbitAngle = (session.OrbitAngle + Options.OrbitDegPerSec * realDt) % 360.0;
        CameraPose pose = QuickMath.OrbitPose(world, session.OrbitAngle, Options);
        return pose with { Fov = Zoom(world, pose.Position, QuickMath.NormalMinFov) };
    }

    // The onboard camera is rigid on the part, so it uses the field of view of the slider and not the auto-zoom.
    private static CameraPose? OnboardPose(Session session, QuickWorld world)
    {
        session.Parts.Refresh(session.Scene.Vehicle);
        if (!session.Parts.TryBox(session.Scene.Vehicle, out V3 min, out V3 max))
            return null;
        CameraPose pose = QuickMath.OnboardPose(min, max, world.Length, Options.OnboardTowardNose, Options.Fov);
        return PoseMath.ToSiteFrame(pose, world.Vehicle);
    }

    private static CameraPose? TrackerPose(Session session, Celestial body, QuickWorld world, double realDt)
    {
        if (session.TrackerPending)
        {
            session.TrackerPending = false;
            V3 heading = Heading(session, world, 0.0);
            V3 direction = QuickMath.TrackerDirection(world, heading, Options.TrackerDistance);
            double radius = QuickScene.SurfaceRadius(body, QuickScene.ToDouble3(direction)) + QuickMath.TrackerEyeHeight;
            V3 point = QuickMath.PointOnBody(world.BodyCentre, direction, radius);
            session.PointCcf = session.Scene.ToCcf(body, point);
        }

        V3 camera = session.Scene.FromCcf(body, session.PointCcf);
        if ((world.Centre - camera).Length > QuickMath.TrackerMaxRange)
        {
            Stop(FormattableString.Invariant($"the vehicle is more than {QuickMath.TrackerMaxRange / 1000.0:F0} km from the ground tracker"));
            return null;
        }

        return QuickMath.LookAtVehicle(world, camera, Zoom(world, camera, QuickMath.LongLensMinFov));
    }

    // In the air it follows the surface velocity and in orbit the inertial velocity, by the same
    // rule as the flyby point.
    private static V3 Heading(Session session, QuickWorld world, double realDt)
    {
        FlybyAnchor anchor = QuickMath.ChooseAnchor(world.Altitude, world.AtmosphereHeight, world.Contact);
        Basis3 local = LocalHorizon.Frame(V3.Zero, world.BodyCentre, world.Pole);
        return session.Heading.Update(local.X, local.Z, QuickMath.FlightVelocity(world, anchor), realDt);
    }

    private static double Zoom(QuickWorld world, V3 camera, double minFov) =>
        Options.AutoZoom
            ? QuickMath.AutoZoomFov(world.Length, world.Width, (world.Centre - camera).Length, Options.ZoomShare, minFov)
            : Options.Fov;

    private static V3 KeepAboveGround(Session session, Celestial body, QuickWorld world, V3 point, double clearance)
    {
        double3 fromCentre = QuickScene.ToDouble3(point - world.BodyCentre);
        double minimum = QuickScene.SurfaceRadius(body, fromCentre) + clearance;
        return QuickMath.KeepAbove(point, world.BodyCentre, minimum);
    }

    // The QuickWorld frame has the axes of ECL, so directions go to the camera as they are.
    private static void Apply(Session session, CameraPose pose)
    {
        Camera camera = CameraRig.Camera;
        V3 forward = pose.LookAt - pose.Position;
        forward = forward.Length < 1e-6 ? session.LastForward : forward.NormalizedOr(session.LastForward);
        session.LastForward = forward;
        V3 up = session.Up.Stabilize(forward, pose.Up);

        camera.PositionEcl = session.Scene.ToEcl(pose.Position);
        camera.LocalRotation = Camera.LookAtRotation(QuickScene.ToDouble3(forward), QuickScene.ToDouble3(up));
        float fov = (float)Math.Clamp(pose.Fov, 1.0, 150.0);
        if (Math.Abs(camera.GetFieldOfView() * (180.0 / Math.PI) - fov) > 1e-3)
            camera.SetFieldOfView(fov);
    }

    #endregion

    private sealed class Session
    {
        public Session(QuickScene scene, CameraRig rig, long frame, int saveLoads)
        {
            Scene = scene;
            Rig = rig;
            LastTickFrame = frame;
            SaveLoads = saveLoads;
            Body = scene.Body;
            Clock.Start();
        }

        public QuickScene Scene { get; }
        public CameraRig Rig { get; }
        public int SaveLoads { get; }
        public QuickMode Mode { get; private set; }
        public long LastTickFrame { get; set; }
        public Celestial? Body { get; set; }
        public Stopwatch Clock { get; } = new();
        public double LastReal { get; set; }
        public double LastGame { get; set; } = double.NaN;
        public UpStabilizer Up { get; } = new();
        public V3 LastForward { get; set; } = V3.UnitX;
        public FlightHeading Heading { get; private set; } = new();
        public OnboardParts Parts { get; } = new();
        public double? SpeedBefore { get; set; }
        public double? WrittenSpeed { get; set; }

        public bool FlybyPending { get; set; }
        public FlybyAnchor Anchor { get; set; }
        public double3 PointCcf { get; set; }
        public FreeFallPoint? FreeFall { get; set; }
        public double PlacedDistance { get; set; }
        public int Side { get; set; } = -1;
        public double OrbitAngle { get; set; }
        public bool TrackerPending { get; set; }

        public void Begin(QuickMode mode)
        {
            Mode = mode;
            FlybyPending = mode == QuickMode.Flyby;
            TrackerPending = mode == QuickMode.GroundTracker;
            FreeFall = null;
            OrbitAngle = 180.0;
            Heading = new FlightHeading();
            Up.Reset();
            if (mode == QuickMode.Onboard)
                Parts.Refresh(Scene.Vehicle);
        }
    }
}
