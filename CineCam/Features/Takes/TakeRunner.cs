using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.Logging;
using Brutal.Numerics;
using CineCam.Core;
using CineCam.Director;
using CineCam.Features.Replay;
using CineCam.Features.TimeOfDay;
using CineCam.Patches;
using KSA;

namespace CineCam.Features.Takes;

// The keys and the console only queue requests, which run at the start of the next frame before
// the game steps, so a take always begins and ends between two frames. When the camera hook stops
// running, for example because the player left the fixed camera mode, the guard after the frame
// ends the take.
internal static partial class TakeRunner
{
    // A take ends when the camera hook missed this many frames in a row.
    private const int MissedFramesLimit = 5;

    private static ActiveTake? _take;
    private static long _frame;
    private static bool _startRequested;
    private static string? _requestedTake;
    private static bool _stopRequested;
    private static bool _reloadRequested;
    private static bool _requestedDry;
    private static PreviewState? _previewRequest;
    private static double? _pendingSpeed;
    private static EditRequest _editRequest;
    private static int _pendingMarks;

    public static bool IsRunning => _take != null;

    /// <summary>The running take, a dry run or a preview.</summary>
    public static ActiveTake? Current => _take;

    #region Requests

    // A request that the game build cannot serve is refused at once, because no frame hook would
    // ever take it from the queue.
    internal static bool Refused(string what, string? block)
    {
        if (block == null)
            return false;
        LogHelper.WarnOnce("refused:" + what, $"[CineCam] No {what}: {block}");
        return true;
    }

    public static void RequestStart(string? takeName, bool dryRun = false)
    {
        if (ReplaySession.IsPlayingBack || Refused(dryRun ? "dry run" : "take", CineCamPatches.CameraBlock))
            return;
        _startRequested = true;
        _requestedDry = dryRun;
        _requestedTake = string.IsNullOrWhiteSpace(takeName) ? null : takeName.Trim();
    }

    // The caller keeps the state object and moves the progress while the preview runs.
    public static void RequestPreview(PreviewState preview)
    {
        if (!ReplaySession.IsPlayingBack && !Refused("shot preview", CineCamPatches.CameraBlock))
            _previewRequest = preview;
    }

    public static void RequestStop() => _stopRequested = true;

    public static void RequestReload()
    {
        if (!Refused("reload of the shot file", CineCamPatches.FrameBlock))
            _reloadRequested = true;
    }

    public static void RequestMark()
    {
        bool recorded = ReplaySession.RequestMark();
        if (_take == null || _take.Preview != null)
        {
            if (!recorded)
                DefaultCategory.Log.Warning("[CineCam] A mark needs a running take or a flight recording.");
            return;
        }

        _pendingMarks++;
    }

    public static void RequestToggle()
    {
        if (IsRunning)
            RequestStop();
        else
            RequestStart(null);
    }

    #endregion

    #region Frame hooks

    // Runs first in Program.OnFrame.
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

    // Runs last in Program.OnFrame.
    public static void OnFrameEnd()
    {
        ActiveTake? take = _take;
        if (take?.Offline is { Pending: true } pending && (!OfflineHooks.Detects || OfflineHooks.PostRenderCount != pending.PostRenderMark))
            pending.Rendered = true;
        if (take == null)
            return;
        if (take.IsPaused)
        {
            // The free camera does not call the camera hook, so the pause keeps the take alive itself.
            take.LastTickFrame = _frame;
            try
            {
                TickPaused(take);
            }
            catch (Exception ex)
            {
                Fail("the paused take", ex);
            }

            return;
        }

        if (_frame - take.LastTickFrame <= MissedFramesLimit)
            return;

        try
        {
            End("the camera is no longer in the take mode, so the player probably changed it");
        }
        catch (Exception ex)
        {
            Fail("the guard", ex);
        }
    }

    // Prefix of FixedController.OnFrame of the main viewport, which returns true to skip the stock
    // controller. A take that ends in this hook also skips it, because the offset and rotation that
    // CameraRig.Restore wrote back are zero when the fixed mode was not used before, and a zero
    // rotation gives a NaN view direction.
    public static bool OnCameraFrame()
    {
        ActiveTake? take = _take;
        if (take == null)
            return false;

        take.LastTickFrame = _frame;
        bool set;
        try
        {
            set = Tick(take);
        }
        catch (Exception ex)
        {
            Fail("the take tick", ex);
            set = false;
        }

        return set || !ReferenceEquals(_take, take);
    }

    /// <summary>True while a take hides the interface, when the console is not drawn because its lines would be in the pictures.</summary>
    public static bool HidesInterface => _take is { HidUi: true, IsPaused: false };

    // Runs after the game draws its frame statistics, which it does even with the interface hidden.
    public static void OnDrawOverlay()
    {
        ActiveTake? take = _take;
        if (take == null || !take.FlashPending)
            return;

        take.FlashPending = false;
        try
        {
            ImDrawListPtr list = ImGuiHelper.GetMainOverlayDrawList();
            var viewport = ImGui.GetMainViewport();
            float2 min = viewport.Pos;
            float2 max = viewport.Pos + viewport.Size;
            list.AddRectFilled(in min, in max, new byte4(255, 255, 255, 255), 0f);
            WriteRow(take, "marker", "syncFlash", "white frame drawn");
        }
        catch (Exception ex)
        {
            Fail("the sync flash", ex);
        }
    }

    #endregion

    #region Start and end

    private static void ProcessRequests()
    {
        if (_stopRequested)
        {
            _stopRequested = false;
            if (Flow.IsActive)
                CancelReset("stopped by the player");
            if (_take != null)
                End("stopped by the player");
        }

        if (_editRequest != EditRequest.None)
        {
            EditRequest request = _editRequest;
            _editRequest = EditRequest.None;
            ProcessEditRequest(request);
        }

        if (_reloadRequested)
        {
            _reloadRequested = false;
            Reload();
        }

        if (_startRequested)
        {
            _startRequested = false;
            string? name = _requestedTake;
            _requestedTake = null;
            bool dry = _requestedDry;
            _requestedDry = false;
            if (Flow.IsCountingDown)
                CancelReset("the player started the take");
            if (_take != null)
                DefaultCategory.Log.Warning("[CineCam] A take is running already. Stop it first.");
            else if (Flow.IsActive)
                DefaultCategory.Log.Warning("[CineCam] A reset runs. Start the take when it is done.");
            else
                Start(name, dry);
        }

        if (_previewRequest != null)
        {
            PreviewState preview = _previewRequest;
            _previewRequest = null;
            if (_take != null)
                DefaultCategory.Log.Warning("[CineCam] A take is running already. Stop it first.");
            else if (Flow.IsActive)
                DefaultCategory.Log.Warning("[CineCam] A reset runs. Start the preview when it is done.");
            else
                StartPreview(preview);
        }
    }

    private static void Reload()
    {
        if (_take != null)
        {
            DefaultCategory.Log.Warning("[CineCam] The shot file is read again at the next take start, not during a take.");
            return;
        }

        ShotFile? file = ShotFileStore.Load(out string path);
        if (file == null)
            return;
        bool ok = ShotFileStore.Check(file);
        DefaultCategory.Log.Info($"[CineCam] Shot file {path}: {file.Takes.Count} take(s) ({string.Join(", ", file.Takes.Select(t => t.Name))}), {(ok ? "no errors" : "errors in the log above")}.");
    }

    // The editor copy wins when the window has unsaved changes, so a run shows what the player
    // sees in the window.
    private static ShotFile? ResolveFile(out string path, out bool fromEditor)
    {
        fromEditor = false;
        EditorCopy? edited = ShotFileStore.EditorCopyProvider?.Invoke();
        if (edited != null)
        {
            fromEditor = true;
            path = edited.Path;
            return edited.File;
        }

        return ShotFileStore.Load(out path);
    }

    private static void Start(string? takeName, bool dryRun)
    {
        if (_take != null)
        {
            DefaultCategory.Log.Warning("[CineCam] A take is running already. Stop it first.");
            return;
        }

        if (Refused(dryRun ? "dry run" : "take", CineCamPatches.CameraBlock))
            return;

        if (SunWarp.IsRunning)
        {
            DefaultCategory.Log.Warning("[CineCam] The game time is moving to the sun elevation. Start the take when that is done.");
            return;
        }

        Vehicle? vehicle = Program.ControlledVehicle;
        if (vehicle == null || vehicle.IsDisposed)
        {
            DefaultCategory.Log.Warning("[CineCam] A take needs a controlled vehicle in flight.");
            return;
        }

        if (Program.Editor != null)
        {
            DefaultCategory.Log.Warning("[CineCam] A take does not run in the vehicle editor.");
            return;
        }

        ShotFile? file;
        string path;
        bool fromEditor;
        try
        {
            file = ResolveFile(out path, out fromEditor);
        }
        catch (ShotFileException ex)
        {
            DefaultCategory.Log.Error($"[CineCam] The unsaved changes of the window have errors, so no take starts: {ex.Message}");
            return;
        }

        if (file == null)
            return;

        Take? take = takeName == null ? file.DefaultTake : file.FindTake(takeName);
        if (take == null)
        {
            DefaultCategory.Log.Error($"[CineCam] The shot file has no take \"{takeName}\". Takes: {string.Join(", ", file.Takes.Select(t => t.Name))}.");
            return;
        }

        TakeDirector director;
        try
        {
            director = new TakeDirector(take, file);
        }
        catch (ArgumentException ex)
        {
            DefaultCategory.Log.Error($"[CineCam] Take \"{take.Name}\": {ex.Message}");
            return;
        }

        SiteFrame? site = SiteFrame.FromVehicle(vehicle, take.Site ?? file.Site, take.Anchor, out string siteNote);
        if (site == null)
        {
            DefaultCategory.Log.Error($"[CineCam] No site frame for the take: {siteNote}.");
            return;
        }

        string folder = "";
        int run = 0;
        EventLogCsv log;
        if (dryRun)
        {
            log = new EventLogCsv(TextWriter.Null);
        }
        else
        {
            try
            {
                string root = CineCamPaths.TakesFolder;
                IEnumerable<string> earlier = Directory.Exists(root)
                    ? Directory.GetDirectories(root).Select(d => Path.GetFileName(d))
                    : Enumerable.Empty<string>();
                run = TakeFolders.NextRun(earlier, take.Name);
                folder = Path.Combine(root, TakeFolders.Unique(TakeFolders.FolderName(DateTime.Now, take.Name),
                    n => Directory.Exists(Path.Combine(root, n))));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DefaultCategory.Log.Error($"[CineCam] The take folders could not be listed: {ex.Message}");
                return;
            }

            try
            {
                Directory.CreateDirectory(folder);
                string copy = Path.Combine(folder, "shots.json");
                if (fromEditor)
                    ShotFileJson.Save(file, copy);
                else
                    File.Copy(path, copy, overwrite: true);
                log = EventLogCsv.Create(Path.Combine(folder, "events.csv"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DefaultCategory.Log.Error($"[CineCam] The take folder {folder} could not be made: {ex.Message}");
                return;
            }
        }

        OfflineCapture? offline = null;
        if (!dryRun && file.Capture.Mode == CaptureMode.Offline)
        {
            offline = BeginOffline(file, take, folder);
            if (offline == null)
            {
                log.Dispose();
                return;
            }
        }

        Quick.QuickCamera.Stop("a take starts");
        var reader = new VehicleReader(vehicle);
        reader.RefreshExtent(0.0);
        var rig = new CameraRig();
        var active = new ActiveTake(take, file, director, reader, site, rig, log, folder, _frame)
        {
            LastTickFrame = _frame,
            FlashPending = file.SyncFlash && !dryRun && offline == null,
            DryRun = dryRun,
            Offline = offline,
        };

        _take = active;
        _pendingMarks = 0;
        try
        {
            if (file.HideUi && !dryRun)
            {
                string? hideText = file.HideUiAtFor(take);
                if (!ShotFile.IsHideUiAtStart(hideText) && EventName.TryResolve(hideText!, file.Events, out EventName hideAt, out _))
                    active.HideUiEvent = hideAt.Canonical;
                else
                    HideInterface(active);
            }

            rig.Enter();
        }
        catch (Exception ex)
        {
            Fail("the take start", ex);
            return;
        }

        V3 vehicleInSite = site.FromEcl(vehicle.GetPositionEcl());
        string siteText = string.Create(CultureInfo.InvariantCulture,
            $"{site.Id} on {site.Body.Id}, vehicle at up {vehicleInSite.X:F2} east {vehicleInSite.Y:F2} north {vehicleInSite.Z:F2} m");
        DefaultCategory.Log.Info($"[CineCam] {(dryRun ? "Dry run of take" : "Take")} \"{take.Name}\" started. Site: {siteNote}. {siteText}. Log folder: {(folder.Length == 0 ? "none" : folder)}");
        WriteRow(active, "take", "start", $"take={take.Name}; dryRun={dryRun}; site={siteText}; rule={siteNote}; file={path}{(fromEditor ? " (unsaved copy)" : "")}; hideUi={file.HideUi}; syncFlash={file.SyncFlash}{(dryRun ? "" : $"; run={run}")}{ResetNoteFor(take)}");
        if (offline != null)
            WriteRow(active, "take", "offline", string.Create(CultureInfo.InvariantCulture, $"fps={offline.Fps}; frames={offline.FramesFolder}"));

        if (!dryRun && CineCamSettings.ExperimentalReplay && CineCamSettings.RecordWithTake && !ReplaySession.IsRecording)
        {
            string? why = ReplaySession.BeginTakeRecording(take.Name);
            WriteRow(active, "info", "recording", why == null ? "the flight records with the take" : "the flight does not record: " + why);
            if (why != null)
                DefaultCategory.Log.Warning($"[CineCam] The flight does not record with the take: {why}");
        }

        // With the sim stopped no event can come, so the take would wait for ever. A menu pause
        // sets the speed to 0 again in the same frame.
        if (!dryRun && Universe.GetSimulationSpeed() == 0.0 && !Universe.IsAutoWarpActive)
        {
            Universe.SetSimulationSpeed(1.0, alert: false);
            DefaultCategory.Log.Info("[CineCam] The sim was stopped at the take start, so the take set it to 1x.");
            WriteRow(active, "info", "simSpeed", "the sim was stopped at the take start; set to 1x");
        }
    }

    private static void StartPreview(PreviewState preview)
    {
        if (_take != null || Refused("shot preview", CineCamPatches.CameraBlock))
            return;

        Vehicle? vehicle = Program.ControlledVehicle;
        if (vehicle == null || vehicle.IsDisposed)
        {
            DefaultCategory.Log.Warning("[CineCam] A preview needs a controlled vehicle.");
            return;
        }

        if (Program.Editor != null || SunWarp.IsRunning)
        {
            DefaultCategory.Log.Warning("[CineCam] A preview does not run in the vehicle editor or while the game time moves.");
            return;
        }

        SiteFrame? site = SiteFrame.FromVehicle(vehicle, ShotFileStore.PreviewSiteId, ShotFileStore.PreviewAnchor, out string siteNote);
        if (site == null)
        {
            DefaultCategory.Log.Error($"[CineCam] No site frame for the preview: {siteNote}.");
            return;
        }

        Quick.QuickCamera.Stop("a shot preview starts");
        var reader = new VehicleReader(vehicle);
        reader.RefreshExtent(0.0);
        var file = new ShotFile();
        var take = new Take { Name = "preview" };
        var active = new ActiveTake(take, file, new TakeDirector(take, file), reader, site, new CameraRig(),
            new EventLogCsv(TextWriter.Null), "", _frame)
        {
            LastTickFrame = _frame,
            Preview = preview,
        };

        _take = active;
        try
        {
            active.Camera.Enter();
        }
        catch (Exception ex)
        {
            Fail("the preview start", ex);
        }
    }

    public static void End(string reason)
    {
        ActiveTake? take = _take;
        if (take == null)
            return;
        _take = null;
        ReplaySession.EndTakeRecording();

        try
        {
            FinishOffline(take, reason);
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error($"[CineCam] The offline capture could not be closed: {ex.Message}");
        }

        double seconds = take.Seconds;
        bool wasPaused = take.IsPaused;
        try
        {
            if (wasPaused)
            {
                // A game menu that opened during the pause gives its saved speed back when it
                // closes, so the speed goes to the menu instead.
                double real = take.Clock.Resume(ClockSeconds);
                double back = take.LastWrittenSpeed.HasValue ? SpeedOrOne(take.SpeedBefore) : SpeedOrOne(take.PausedSpeed);
                if (MenuPause.Active)
                    MenuPause.Hand(back);
                else
                    Universe.SetSimulationSpeed(back, alert: false);
                take.Log.Write(seconds, GameSeconds(), MissionSeconds(take), Universe.GetSimulationSpeed(), take.Offline?.FrameNumber ?? -1,
                    "pause", "end", string.Create(CultureInfo.InvariantCulture, $"the take ended during the pause; length={real:F3} s real"));
            }
            else if (take.LastWrittenSpeed.HasValue)
            {
                RestoreSpeed(take.SpeedBefore ?? 1.0);
            }
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error($"[CineCam] The sim speed could not be reset: {ex.Message}");
        }

        // Restore switches the saved mode back only from the fixed mode, and a pause left the
        // camera in the free mode.
        if (wasPaused)
        {
            try
            {
                take.Camera.EndFree();
            }
            catch (Exception ex)
            {
                DefaultCategory.Log.Error($"[CineCam] The camera could not leave the free mode: {ex.Message}");
            }
        }

        try
        {
            take.Camera.Restore();
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error($"[CineCam] The camera could not be given back: {ex.Message}");
        }

        try
        {
            if (take.HidUi)
            {
                Program.DrawUI = take.SavedDrawUi;
                Program.ConsoleWindow?.EnableMessageFading();
                TimedAlert.ClearAll();
            }
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error($"[CineCam] The game interface could not be shown again: {ex.Message}");
        }

        try
        {
            ReportUnfiredActions(take, seconds);
            take.Log.Write(seconds, GameSeconds(), MissionSeconds(take), Universe.GetSimulationSpeed(), take.Offline?.Count ?? -1, "take", "end", reason);
            take.Log.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            DefaultCategory.Log.Error($"[CineCam] The event log could not be closed: {ex.Message}");
        }

        DefaultCategory.Log.Info(FormattableString.Invariant($"[CineCam] Take \"{take.Take.Name}\" ended after {seconds:F1} s: {reason}."));
    }

    public static void Shutdown()
    {
        _startRequested = false;
        _stopRequested = false;
        _reloadRequested = false;
        _previewRequest = null;
        _requestedTake = null;
        _requestedDry = false;
        _pendingSpeed = null;
        _editRequest = EditRequest.None;
        ShutdownReset();
        try
        {
            End("the mod unloaded");
            // At an unload in a menu pause no later frame gives the old speed back, so the menu
            // gets it instead.
            if (_pendingSpeed is double speed && MenuPause.Active)
                MenuPause.Hand(speed);
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error($"[CineCam] Shutdown of the take failed: {ex.Message}");
        }

        _pendingSpeed = null;
    }

    // A menu pause holds the speed at 0 and gives back the speed of the take when it closes, so
    // the speed from before the take waits until the game runs again.
    private static void RestoreSpeed(double speed)
    {
        if (speed <= 0.0)
            speed = 1.0;
        if (Universe.GetSimulationSpeed() == 0.0)
            _pendingSpeed = speed;
        else
            Universe.SetSimulationSpeed(speed, alert: false);
    }

    private static void ApplyPendingSpeed()
    {
        if (_pendingSpeed is not double speed || _take != null)
            return;
        if (Universe.IsAutoWarpActive)
        {
            _pendingSpeed = null;
            return;
        }

        if (Universe.GetSimulationSpeed() == 0.0)
            return;
        _pendingSpeed = null;
        Universe.SetSimulationSpeed(speed, alert: false);
    }

    private static void Fail(string where, Exception ex)
    {
        LogHelper.ErrorOnce("fail:" + where, $"[CineCam] Error in {where}: {ex}");
        try
        {
            End($"error in {where}: {ex.Message}");
        }
        catch (Exception inner)
        {
            LogHelper.ErrorOnce("fail-end", $"[CineCam] The take could not end cleanly: {inner}");
            _take = null;
        }
    }

    #endregion

    #region Tick

    // A preview has no events, so it drops the marks.
    private static int TakeMarks(ActiveTake take)
    {
        int marks = _pendingMarks;
        _pendingMarks = 0;
        return take.Preview == null ? marks : 0;
    }

    // Returns true when the camera was set.
    private static bool Tick(ActiveTake take)
    {
        if (take.IsPaused)
            return false;

        double t = take.Seconds;
        WorldSample? world = BuildWorld(take, t);
        if (world == null)
            return false;

        if (take.Preview != null)
        {
            TickPreview(take, take.Preview, world, t);
            return true;
        }

        DirectorFrame frame = take.Director.Update(t, world);
        take.LastFrame = frame;
        LogFrame(take, t, frame);
        ApplySpeed(take, frame, t);
        RunActions(take, t);

        CameraPose pose = frame.Pose ?? take.HoldPose;
        take.Camera.Apply(take.Site, pose);

        if (frame.Finished)
        {
            // The picture of this frame is made after the camera hook, so an offline take ends
            // only after it is saved.
            if (take.Offline != null)
                take.Offline.EndAfterPicture = "the last shot is done";
            else
                End("the last shot is done");
        }

        return true;
    }

    // Ends the take and returns null when the vehicle cannot be followed or the editor opened.
    private static WorldSample? BuildWorld(ActiveTake take, double t)
    {
        Vehicle? current = Program.ControlledVehicle;
        if (current == null || current.IsDisposed)
        {
            End("the vehicle is gone");
            return null;
        }

        if (!ReferenceEquals(current, take.Vehicle.Vehicle))
        {
            if (!take.Vehicle.TryFollow(current))
            {
                End("the controlled vehicle changed");
                return null;
            }

            // The separated vehicles were found relative to the vehicle before.
            take.Separation = null;
            DefaultCategory.Log.Info($"[CineCam] The take follows the controlled vehicle {current.Id}, which has parts of the vehicle before (a docking, an undocking or a breakup).");
            WriteRow(take, "info", "vehicle", $"the take follows {current.Id}, which has parts of the vehicle before (a docking, an undocking or a breakup)", t);
        }
        else if (take.Vehicle.Vehicle.IsDisposed)
        {
            End("the vehicle is gone");
            return null;
        }

        if (Program.Editor != null)
        {
            End("the vehicle editor opened");
            return null;
        }

        take.Site.Refresh();
        take.Vehicle.RefreshExtent(t);
        take.Vehicle.ReadHorizon(take.Site, out V3 bodyCentre, out V3 pole);
        V3? sun = take.Vehicle.ReadSun(take.Site, out double bodyRadius);
        SubjectSample? newest = null, nearest = null;
        bool wantsSeparated = take.Preview != null
            ? take.Preview.Shot.Subject == ShotSubject.Separated
            : take.Take.Shots.Any(s => s.Subject == ShotSubject.Separated && Shot.SupportsAim(s.Type));
        if (wantsSeparated)
        {
            take.Separation ??= new SeparationTracker(take.Vehicle.Vehicle);
            take.Separation.Update(t);
            take.Separation.Sample(take.Site, out newest, out nearest);
        }

        return new WorldSample
        {
            BodyRadius = bodyRadius,
            SunDirection = sun,
            SeparatedNewest = newest,
            SeparatedNearest = nearest,
            BodyCentre = bodyCentre,
            Pole = pole,
            Vehicle = take.Vehicle.Sample(TakeMarks(take)),
            VehicleFrame = take.Vehicle.Frame(take.Site),
            VehicleHeight = take.Vehicle.Height,
            VehicleWidth = take.Vehicle.Width,
            SurfaceVelocity = take.Vehicle.SurfaceVelocity(take.Site),
            PartLookup = take.Vehicle.FindPart,
        };
    }

    private static void TickPreview(ActiveTake take, PreviewState preview, WorldSample world, double t)
    {
        double dt = double.IsNaN(preview.LastSeconds) ? 0.0 : Math.Max(t - preview.LastSeconds, 0.0);
        preview.LastSeconds = t;
        Shot shot = preview.Shot;
        if (preview.Playing)
        {
            preview.Raw += dt / Math.Max(shot.Duration, 1e-3);
            if (preview.Raw >= 1.0)
            {
                if (preview.Loop)
                {
                    preview.Raw -= 1.0;
                }
                else
                {
                    preview.Raw = 1.0;
                    preview.Playing = false;
                }
            }
        }

        var warnings = new List<string>();
        take.Director.TrackHeading(world, dt);
        Basis3 smoothed = take.Director.SmoothVehicle(TakeDirector.SubjectOf(shot, world).Frame, shot.Type is ShotType.Chase or ShotType.Part ? shot.Smoothing : 0.0, dt);
        CameraPose pose = take.Director.ShotPoseInSite(shot, 0, Easing.Apply(shot.Ease, preview.Raw), world, smoothed, warnings);
        bool partMissing = shot.Type == ShotType.Part && shot.Part.Length > 0 && world.PartLookup?.Invoke(shot.Part) == null;
        preview.Warning = partMissing ? $"The vehicle has no part \"{shot.Part}\", so the shot uses the base of the vehicle." : null;
        take.Camera.Apply(take.Site, pose);
    }

    private static void HideInterface(ActiveTake take)
    {
        take.SavedDrawUi = Program.DrawUI;
        take.HidUi = true;
        Program.DrawUI = false;
        // The console draws over the picture, so new lines get no display time while the interface is hidden.
        Program.ConsoleWindow?.DisableMessageFading();
    }

    private static void LogFrame(ActiveTake take, double t, DirectorFrame frame)
    {
        foreach (DetectedEvent seen in frame.NewEvents)
        {
            if (seen.Name == "ignition")
                take.IgnitionGameSeconds = GameSeconds();
            WriteRow(take, "event", seen.Name, seen.Detail, t);
            if (take.HideUiEvent != null && seen.Name == take.HideUiEvent && !take.HidUi)
            {
                if (take.IsPaused)
                {
                    take.HideUiDeferred = true;
                    continue;
                }

                HideInterface(take);
                WriteRow(take, "marker", "hideUi", "the game interface is hidden", t);
            }
        }

        foreach (int index in frame.StartedShots)
        {
            Shot shot = take.Take.Shots[index];
            WriteRow(take, "shot", $"camera[{index}]",
                string.Create(CultureInfo.InvariantCulture, $"type={shot.Type}; duration={shot.Duration}; fov={shot.Fov}"), t);
        }

        foreach (int index in frame.StartedSpeedKeys)
        {
            SpeedKey key = take.Take.Speed[index];
            WriteRow(take, "speed", $"speed[{index}]",
                string.Create(CultureInfo.InvariantCulture, $"to={key.To}; ramp={key.Ramp}"), t);
        }

        QueueActions(take, frame);

        foreach (string note in frame.Notes)
        {
            DefaultCategory.Log.Info($"[CineCam] {note}");
            WriteRow(take, "info", "keepOut", note, t);
        }

        foreach (DirectorReport report in frame.Reports)
        {
            DefaultCategory.Log.Info($"[CineCam] {report.Text}");
            WriteRow(take, "info", report.Name, report.Text, t);
        }

        foreach (string warning in frame.Warnings)
        {
            DefaultCategory.Log.Warning($"[CineCam] {warning}");
            WriteRow(take, "warning", "shot", warning, t);
        }
    }

    // Writes the speed track in every frame from the first key on, but does not fight an automatic
    // warp or a menu pause.
    private static void ApplySpeed(ActiveTake take, DirectorFrame frame, double t)
    {
        if (!frame.SpeedActive || take.DryRun)
            return;

        double now = Universe.GetSimulationSpeed();
        SpeedDecision decision = SpeedPolicy.Decide(Universe.IsAutoWarpActive, now, take.LastWrittenSpeed, frame.TargetSpeed);
        if (decision.ReportAutoWarp && !take.ReportedAutoWarp)
        {
            take.ReportedAutoWarp = true;
            WriteRow(take, "info", "autoWarp", "an automatic warp is active, so the speed track waits", t);
        }

        if (decision.Action == SpeedAction.Wait)
            return;

        if (decision.ReportOverride && !take.ReportedSpeedOverride)
        {
            take.ReportedSpeedOverride = true;
            WriteRow(take, "info", "speedOverride", string.Create(CultureInfo.InvariantCulture, $"the sim speed was {now} and not {take.LastWrittenSpeed}; the take sets it again"), t);
        }

        take.SpeedBefore ??= now;
        if (decision.Action == SpeedAction.Write)
            Universe.SetSimulationSpeed(frame.TargetSpeed, alert: false);
        take.LastWrittenSpeed = frame.TargetSpeed;
    }

    #endregion

    #region Log

    private static void WriteRow(ActiveTake take, string kind, string name, string detail, double? realSeconds = null)
    {
        take.Log.Write(realSeconds ?? take.Seconds, GameSeconds(), MissionSeconds(take),
            Universe.GetSimulationSpeed(), take.Offline?.FrameNumber ?? -1, kind, name, detail);
    }

    private static double GameSeconds() => Universe.GetElapsedSeconds();

    // Seconds since the ignition, or 0 before it.
    private static double MissionSeconds(ActiveTake take) =>
        take.IgnitionGameSeconds.HasValue ? GameSeconds() - take.IgnitionGameSeconds.Value : 0.0;

    #endregion
}
