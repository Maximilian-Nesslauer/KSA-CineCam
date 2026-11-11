using System.Globalization;
using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Editing;
using CineCam.Features.Replay;
using CineCam.Features.Ui;
using KSA;

namespace CineCam.Features.Takes;

/// <summary>
/// The frames and the moment that a new shot is made in, from the live flight, a paused take or a replay.
/// <see cref="RunningShot"/> is -1 unless the paused take or the replay plays the selected take of the editor.
/// </summary>
internal readonly record struct CaptureContext(
    SiteFrame Site, Basis3 Vehicle, double Height, double Width, V3 SurfaceVelocity, V3 BodyCentre, V3 Pole, V3? Heading,
    Func<string, V3?>? PartLookup, Trigger? At, bool Replay, int RunningShot, double RunningProgress,
    double? Start = null, IReadOnlyList<double?>? Starts = null);

internal static class KeyCapture
{
    // Why no key can be added now, or an empty text.
    public static string Block(EditSession session)
    {
        if (session.Take == null)
            return "Make a take first with New take.";
        if (ReplaySession.IsPlayingBack)
            return ReplaySession.TakeName != null && string.Equals(ReplaySession.TakeName, session.Take.Name, StringComparison.OrdinalIgnoreCase)
                ? "" : ReplaySession.TakeProblem.Length > 0 ? ReplaySession.TakeProblem : "The replay has not played the selected take yet. Wait a moment.";
        if (FreeCamera.Block is { Length: > 0 } camera)
            return camera;
        Vehicle? vehicle = Program.ControlledVehicle;
        if (vehicle == null || vehicle.IsDisposed)
            return "Needs a controlled vehicle in flight.";
        return PausedTakeBlock(session);
    }

    // In a pause the pose is read in the site frame of the paused take, so the new shot can go only
    // into that take, but the take picker stays open and the selected take can be another one.
    private static string PausedTakeBlock(EditSession session)
    {
        if (TakeRunner.Current is not { IsPaused: true } paused || IsSameTake(session, paused.Take.Name))
            return "";
        return $"The take \"{paused.Take.Name}\" is paused. Select it in the take list to add a key at the moment of the pause.";
    }

    private static bool IsSameTake(EditSession session, string name) =>
        session.Take != null && string.Equals(session.Take.Name, name, StringComparison.OrdinalIgnoreCase);

    public static bool TryContext(EditSession session, string? siteId, WorldAnchor? anchor, bool usePause, out CaptureContext context, out string message)
    {
        context = default;
        message = "";
        if (ReplaySession.Scene is { } scene)
            return TryReplayContext(session, scene, out context, out message);

        if (FreeCamera.Block is { Length: > 0 } camera)
        {
            message = camera;
            return false;
        }

        ActiveTake? take = TakeRunner.Current;
        if (usePause && take is { IsPaused: true })
        {
            if (PausedTakeBlock(session) is { Length: > 0 } block)
            {
                message = block;
                return false;
            }

            take.Site.Refresh();
            take.Vehicle.RefreshExtent(0.0, force: true);
            take.Vehicle.ReadHorizon(take.Site, out V3 centre, out V3 pole);
            IReadOnlyDictionary<string, string> aliases = session.Document?.Events ?? take.File.Events;
            Trigger at = RehearsalEdit.TriggerHere(take.Director.Events.Seen, aliases, take.Seconds);
            bool same = IsSameTake(session, take.Take.Name);
            DirectorFrame? frame = same ? take.LastFrame : null;
            context = new CaptureContext(take.Site, take.Vehicle.Frame(take.Site), take.Vehicle.Height, take.Vehicle.Width,
                take.Vehicle.SurfaceVelocity(take.Site), centre, pole, take.Director.Heading, take.Vehicle.FindPart, at, false,
                frame?.ShotIndex ?? -1, frame?.Progress ?? 0.0,
                ShotPlacer.StartOf(at, take.Director.Events.Seen, aliases),
                same && session.Take is { } edited
                    ? ShotPlacer.StartsInPause(edited, aliases, take.Take, take.File.Events, take.Director.Timeline.Starts.Shots, take.Seconds, take.Director.Events.Seen)
                    : null);
            return true;
        }

        Vehicle? vehicle = Program.ControlledVehicle;
        if (vehicle == null || vehicle.IsDisposed)
        {
            message = "Needs a controlled vehicle in flight.";
            return false;
        }

        SiteFrame? site = SiteFrame.FromVehicle(vehicle, siteId, anchor, out string note);
        if (site == null)
        {
            message = $"No world point frame here: {note}.";
            return false;
        }

        var reader = new VehicleReader(vehicle);
        reader.RefreshExtent(0.0, force: true);
        site.Refresh();
        reader.ReadHorizon(site, out V3 bodyCentre, out V3 bodyPole);
        context = new CaptureContext(site, reader.Frame(site), reader.Height, reader.Width, reader.SurfaceVelocity(site),
            bodyCentre, bodyPole, reader.RawHeading(site, bodyCentre, bodyPole), reader.FindPart, null, false, -1, 0.0);
        return true;
    }

    private static bool TryReplayContext(EditSession session, ReplayScene scene, out CaptureContext context, out string message)
    {
        context = default;
        // The running shot and its progress must come from the take as it is now, not from a plan
        // that a worker resolved before the last edit.
        ReplaySession.EnsureCurrentTake();
        message = Block(session);
        if (message.Length > 0 || scene.Player.Sample is not { } world)
        {
            if (message.Length == 0)
                message = "The replay has no picture yet.";
            return false;
        }

        scene.Site.Refresh();
        IReadOnlyDictionary<string, string> aliases = session.Document?.Events ?? new Dictionary<string, string>();
        Trigger at = RehearsalEdit.TriggerHere(scene.Player.Plan.EventTimes, aliases, scene.TakeTime);
        V3 centre = world.BodyCentre ?? V3.Zero;
        V3 pole = world.Pole ?? V3.UnitZ;
        V3 velocity = world.SurfaceVelocity ?? V3.Zero;
        Basis3 local = LocalHorizon.Frame(world.VehicleFrame.Origin, centre, pole);
        V3 heading = scene.Player.Director?.Heading ?? FlightHeading.Raw(local.X, local.Z, velocity);
        DirectorFrame? frame = scene.Player.Frame;
        ReplayTakePlan plan = scene.Player.Plan;
        context = new CaptureContext(scene.Site, world.VehicleFrame, world.VehicleHeight, world.VehicleWidth, velocity,
            centre, pole, heading, world.PartLookup, at, true, frame?.ShotIndex ?? -1, frame?.Progress ?? 0.0,
            ShotPlacer.StartOf(at, plan.EventTimes, aliases), plan.Starts?.Shots);
        return true;
    }

    public static bool AddKey(EditSession session, KeyKind kind, out string message)
    {
        if (!TryContext(session, session.Take?.Site ?? session.Document?.Site, session.Take?.Anchor, true, out CaptureContext c, out message))
            return false;

        CameraPose pose = CameraRig.CurrentPose(c.Site);
        var camera = new CapturedCamera(pose.Position, (pose.LookAt - pose.Position).NormalizedOr(V3.UnitX), pose.Fov);

        if (kind == KeyKind.PathPoint && session.SelectedShot is { Type: ShotType.Path } path)
        {
            bool ok = c.RunningShot >= 0 && c.RunningShot == session.ShotIndex
                ? ShotCapture.AddPathPoint(path, camera, c.Vehicle, c.Height, c.RunningProgress, out message, c.BodyCentre, c.Pole, c.Heading)
                : ShotCapture.AppendPathPoint(path, camera, c.Vehicle, c.Height, out message, c.BodyCentre, c.Pole, c.Heading);
            if (ok)
            {
                session.AdoptAnchor(GroundAnchor(c.Site));
                session.Changed();
            }

            return ok;
        }

        string failure = "";
        Shot? shot = kind == KeyKind.PathPoint
            ? ShotCapture.NewPath(camera, c.Vehicle, c.Height, c.BodyCentre, c.Pole)
            : ShotCapture.NewKey(kind, camera, c.Vehicle, c.Height, out failure, c.BodyCentre, c.Pole, c.Heading);
        if (shot == null)
        {
            message = failure;
            return false;
        }

        return Insert(session, shot, c, ShotCapture.KeyLabel(kind), out message);
    }

    public static bool AddPreset(EditSession session, ShotPreset preset, out string message)
    {
        if (!TryContext(session, session.Take?.Site ?? session.Document?.Site, session.Take?.Anchor, true, out CaptureContext c, out message))
            return false;
        var input = new PresetInput(c.Height, c.Width, c.Vehicle, c.SurfaceVelocity);
        return Insert(session, ShotPresets.Make(preset, input), c, ShotPresets.Label(preset), out message);
    }

    public static bool SetShotFromCamera(EditSession session, Shot shot, string? siteId, WorldAnchor? anchor, out string message, out WorldAnchor? groundAnchor)
    {
        groundAnchor = null;
        if (!TryContext(session, siteId, anchor, false, out CaptureContext c, out message))
            return false;
        groundAnchor = GroundAnchor(c.Site);
        CameraPose pose = CameraRig.CurrentPose(c.Site);
        V3 forward = (pose.LookAt - pose.Position).NormalizedOr(V3.UnitX);
        V3? part = shot.Type == ShotType.Part && shot.Part.Length > 0 ? c.PartLookup?.Invoke(shot.Part) : null;
        return ShotCapture.Apply(shot, new CapturedCamera(pose.Position, forward, pose.Fov), c.Vehicle, c.Height, part, out message,
            c.BodyCentre, c.Pole, c.Heading);
    }

    private static bool Insert(EditSession session, Shot shot, CaptureContext c, string what, out string message)
    {
        string note = "";
        if (c.At == null || session.Take is not { } take)
        {
            session.InsertShot(shot);
        }
        else
        {
            shot.At = c.At;
            int loopShots = ShotPlacer.LoopShots(take, session.Document?.Events ?? new Dictionary<string, string>());
            ShotPlacement place = c.Start is double start
                ? ShotPlacer.Place(take, start, c.Starts, loopShots, session.ShotIndex)
                : ShotPlacer.Place(take, 0.0, null, loopShots, session.ShotIndex);
            if (place.Duration is double duration)
                shot.Duration = duration;
            session.InsertShot(shot, place.Index);
            if (place.Duration != null && place.Delay > 1e-9)
                note = string.Create(CultureInfo.InvariantCulture,
                    $" It lasts {shot.Duration:0.##} s, the shortest length, so the shots after it start {place.Delay:0.##} s later.");
            else if (place.Duration != null)
                note = string.Create(CultureInfo.InvariantCulture,
                    $" It lasts {shot.Duration:0.##} s, until the next shot starts, so the shots after it keep their times.");
            if (place.InPreRoll)
                note += " The pre-roll loop runs at this moment, so its next pass takes the picture back.";
        }

        session.AdoptAnchor(GroundAnchor(c.Site));
        string added = $"Added {what} as shot {session.ShotIndex + 1}.";
        if (c.At == null)
        {
            message = $"{added} Save to keep it.";
            return true;
        }

        string starts = " It starts " + Widgets.DescribeTrigger(c.At);
        if (!c.Replay)
        {
            message = $"{added}{starts} in the next take; this run is not changed.{note} Until the next take, the other camera edits of this pause change this run only. Save to keep it.";
            return true;
        }

        ReplaySession.EnsureCurrentTake();
        if (ReplaySession.TakeName == null)
        {
            message = $"{added}{starts}, but the replay cannot play the take with it. {ReplaySession.TakeProblem}";
            return false;
        }

        message = $"{added}{starts}, and the replay plays it now.{note} Save to keep it.";
        return true;
    }

    private static WorldAnchor? GroundAnchor(SiteFrame site) => site.Landmark == null ? site.Anchor : null;
}
