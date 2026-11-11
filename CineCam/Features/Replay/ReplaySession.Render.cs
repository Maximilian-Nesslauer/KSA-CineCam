using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Brutal.Logging;
using CineCam.Core;
using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Editing;
using CineCam.Features.Takes;
using CineCam.Features.Ui;
using HarmonyLib;
using KSA;

namespace CineCam.Features.Replay;

// Each frame moves the replay by one picture step of take time and saves one picture, so the video
// is exact at any render speed. The picture is requested in the prefix of Program.OnFrame and
// collected in the prefix of the next frame. The game time also moves by the picture step, because
// the shaders read it.
internal static partial class ReplaySession
{
    private const int RenderMaxFailuresInRow = 3;

    // The most frames that a picture may wait for the end of a frame that returned early.
    private const int RenderMaxCarryFrames = 120;

    // Frames that the game draws without a picture after the start and after a cut, so the terrain,
    // the clouds and the temporal effects of the new view can catch up.
    private const int StartSettleFrames = 30;
    private const int CutSettleFrames = 8;

    private const int DiskCheckEvery = 200;

    private static readonly TimeSpan RenderCollectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TitleInterval = TimeSpan.FromMilliseconds(500);

    private sealed class RenderRun(ReplayRenderJob job, OfflineCapture capture, EventLogCsv log, string folder,
        ReplayTakePlan plan, double flightStart)
    {
        public ReplayRenderJob Job { get; } = job;
        public OfflineCapture Capture { get; } = capture;
        public EventLogCsv Log { get; } = log;
        public string Folder { get; } = folder;
        public ReplayTakePlan Plan { get; } = plan;
        public double FlightStart { get; } = flightStart;
        public List<ReplayRenderRow> EventRows { get; init; } = [];
        public int NextEventRow { get; set; }
        public int MovedTo { get; set; } = -1;
        public int Settle { get; set; } = StartSettleFrames;
        public string? StopReason { get; set; }
        public long Started { get; } = Stopwatch.GetTimestamp();
        public long LastTitle { get; set; }

        // The state of the replay and the game before the render, given back at its end.
        public double SavedTime { get; init; }
        public bool SavedPlaying { get; init; }
        public double SavedSpeed { get; init; }
        public ReplayCameraMode SavedMode { get; init; }
        public bool SavedDrawUi { get; init; }
        public string? SavedTitle { get; init; }
        public HashSet<TimedAlert>? SavedAlerts { get; init; }

        public ReplayRenderProgress Progress =>
            new(Capture.Count, Job.Total, Stopwatch.GetElapsedTime(Started).TotalSeconds);

        public ReplayRenderStatus Status(string? result)
        {
            ReplayRenderProgress progress = Progress;
            return new ReplayRenderStatus(result == null, progress.Done, progress.Total, progress.Percent,
                progress.ElapsedSeconds, result == null ? progress.SecondsLeft : 0.0, Job.Fps, Job.Start, Job.End,
                Capture.OutputFolder, result);
        }
    }

    private static RenderRun? _render;
    private static ReplayRenderStatus? _lastRender;

    // The capture of a render that ended after its picture was requested and before the frame of the
    // picture ended. The game still saves that picture, so the next frame takes it out of the
    // screenshot folder.
    private static OfflineCapture? _strayCapture;
    private static readonly TimeSpan StrayTimeout = TimeSpan.FromSeconds(5);

    public static bool IsRendering => _render != null;

    /// <summary>The render that runs, or the last render since the mod loaded, or null.</summary>
    public static ReplayRenderStatus? RenderStatus => _render?.Status(null) ?? _lastRender;

    /// <summary>One line about the render for the window, or an empty text.</summary>
    public static string RenderStatusText => _render is { } run
        ? $"Rendering {run.Progress.Text()}."
        : _lastRender?.Result ?? "";

    private const string TakeInvalidText = "Fix the problems of the take to render it.";

    // The replay can still play the last good plan of a take with errors, and the plan can lag one
    // resolve behind an edit, so the errors of the editor block the render on their own. The window
    // and the control bridge both use this rule.
    /// <summary>Why a render cannot start now, or null. The start checks more, such as the disk space and the output folder.</summary>
    public static string? RenderBlockReason =>
        Scene == null ? "Open a replay first."
        : IsRendering ? "A render runs."
        : _strayCapture != null ? "Wait for the last render to end."
        : !OfflineHooks.Available ? "This game build does not support the capture of pictures."
        : CineCamWindow.EditorSession is not { Document: not null, Take: not null } ? "The editor has no take. Make one on the Editor page."
        : Plan?.Take == null || _takeName == null || Scene.Player.Problem.Length > 0
            ? (_takeProblem.Length > 0 ? $"{_takeProblem} Fix the take to render it." : "The replay plays no take.")
        : !CineCamWindow.EditorSession.Validation.IsValid ? TakeInvalidText
        : null;

    public static void StartRender(ReplayRenderRange range) =>
        Submit(new(ReplayOperation.Render, Name: range == ReplayRenderRange.FromCurrent ? "current" : "whole"));

    // The keys and the window run on the main thread, so the stop takes effect at the next frame.
    public static void StopRender()
    {
        if (_render is { } run)
            run.StopReason ??= "stopped by the player";
    }

    private static ReplayRenderRange RenderRange(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        null or "" or "whole" => ReplayRenderRange.Whole,
        "current" => ReplayRenderRange.FromCurrent,
        _ => throw new Refusal("invalid_argument", "Choose the range \"whole\" or \"current\"."),
    };

    private static string RequestRenderStop()
    {
        if (_render is not { } run)
            throw new Refusal("not_rendering", "No render runs.");
        run.StopReason ??= "stopped by the player";
        return "The render stops after the current picture.";
    }

    private static string BeginRender(ReplayScene scene, ReplayRenderRange range)
    {
        if (!OfflineHooks.Available)
            throw new Refusal("render_unavailable", "This game build does not support the capture of pictures, see the log at the start of the game.");
        if (Program.Instance?.IsScreenshotCaptureActive != false)
            throw new Refusal("capture_busy", "Wait for the current game screenshot to finish.");
        if (GameMenuOpen)
            throw new Refusal("menu_open", "Close the game menu before the render.");
        if (_strayCapture != null)
            throw new Refusal("capture_busy", "Wait for the last render to end.");

        EditSession session = CineCamWindow.EditorSession;
        if (session.Document == null || session.Take == null)
            throw new Refusal("no_take", "The editor has no take. Make one on the Editor page.");
        try
        {
            session.CopyForRun();
        }
        catch (ShotFileException ex)
        {
            throw new Refusal("take_errors", $"The take has errors, so it does not render: {ex.Message}");
        }

        SyncTake(scene, true);
        ReplayTakePlan plan = scene.Player.Plan;
        if (plan.Take is not { } take || plan.File is not { } file || scene.Player.Problem.Length > 0 || _takeName == null)
            throw new Refusal("take_errors", _takeProblem.Length > 0 ? _takeProblem : "The replay plays no take.");
        if (RenderBlockReason is { } block)
            throw new Refusal(block == TakeInvalidText ? "take_errors" : "render_blocked", block);

        if (!ReplayRenderJob.TryCreate(range, scene.TakeTime, scene.Duration, file.CaptureFps, out ReplayRenderJob? created, out string why))
            throw new Refusal("nothing_to_render", why);
        ReplayRenderJob job = created!;

        string root = CineCamPaths.RendersFolder;
        string folder = Path.Combine(root, TakeFolders.Unique(ReplayRenderLog.FolderName(_replayId ?? "replay", take.Name),
            name => Directory.Exists(Path.Combine(root, name))));
        string output = OfflineOutput.ResolveFolder(folder, file.Capture.Folder);
        string frames = Path.Combine(output, OfflineOutput.FramesFolderName);
        string diskNote = CheckRenderDisk(output, job.Total);
        if (Directory.Exists(frames) && Directory.EnumerateFiles(frames, "*.png").Any())
            throw new Refusal("output_not_empty", $"The folder {frames} has pictures already. Choose another output folder.");

        EventLogCsv? log = null;
        RenderRun run;
        try
        {
            Directory.CreateDirectory(folder);
            ShotFileJson.Save(file, Path.Combine(folder, "shots.json"));
            log = EventLogCsv.Create(Path.Combine(folder, "events.csv"));
            var collector = new ScreenshotCollector(CineCamPaths.ScreenshotsFolder, frames);
            OfflineOutput.WriteHelpers(output, folder, take.Name, job.Fps);
            var capture = new OfflineCapture(job.Fps, output, frames, collector);
            run = new RenderRun(job, capture, log, folder, plan, scene.Flight.StartTime)
            {
                EventRows = ReplayRenderLog.EventRows(plan.EventTimes, job),
                SavedTime = scene.TakeTime,
                SavedPlaying = scene.IsPlaying,
                SavedSpeed = scene.Player.PreviewSpeed,
                SavedMode = scene.CameraMode,
                SavedDrawUi = Program.DrawUI,
                SavedTitle = Program.GetWindow()?.Title,
                SavedAlerts = RenderAlerts.Snapshot(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log?.Dispose();
            throw new Refusal("file_error", $"The render folder {folder} could not be prepared: {ex.Message}");
        }
        catch
        {
            log?.Dispose();
            throw;
        }

        // From here the run is the render, so FinishRender gives back all that the start changed,
        // whatever fails.
        _render = run;
        try
        {
            if (file.Capture.Scale != 1)
                DefaultCategory.Log.Warning("[CineCam] The capture scale is ignored. The pictures have the size of the game window.");
            ReplayRenderRow start = new(0.0, job.Start, 1, "take", "start", string.Create(CultureInfo.InvariantCulture,
                $"take={take.Name}; source=replay; recording={_replayId}; range={(range == ReplayRenderRange.FromCurrent ? "current" : "whole")}; takeStart={job.Start:F3}; takeEnd={job.End:F3}; fps={job.Fps}; pictures={job.Total}"));
            ReplayRenderLog.Write(log, start, plan, run.FlightStart);
            ReplayRenderLog.Write(log, start with { Kind = "take", Name = "offline", Detail = $"fps={OfflineOutput.FormatFps(job.Fps)}; frames={frames}" },
                plan, run.FlightStart);

            scene.SetPlaying(false);
            scene.SetPreviewSpeed(1.0);
            scene.Seek(job.Start);
            scene.SetCameraMode(ReplayCameraMode.Take);
            Program.DrawUI = false;
            // The console draws its lines over the picture, so new lines get no display time.
            Program.ConsoleWindow?.DisableMessageFading();
        }
        catch (Exception ex)
        {
            FinishRender($"error: {ex.GetBaseException().Message}", true);
            if (ex is IOException or UnauthorizedAccessException)
                throw new Refusal("file_error", $"The event log of the render could not be written: {ex.Message}");
            throw;
        }

        string text = string.Create(CultureInfo.InvariantCulture,
            $"Rendering \"{take.Name}\": {job.Total} pictures at {OfflineOutput.FormatFps(job.Fps)} per second ({job.VideoDuration:F1} s of video) into {output}. Escape or the start and stop key stops the render.{diskNote}");
        DefaultCategory.Log.Info($"[CineCam] {text}");
        return text;
    }

    // Refuses a render on a disk that is almost full, and returns a note when the pictures may not fit.
    private static string CheckRenderDisk(string folder, int pictures)
    {
        long free;
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (root == null)
                return "";
            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return "";
        }

        long gib = 1L << 30;
        if (free < ReplayRenderLog.MinFreeBytes)
            throw new Refusal("disk_low", string.Create(CultureInfo.InvariantCulture,
                $"Only {free / (double)gib:F1} GB are free for the pictures. Free some space or choose another output folder."));
        var size = Program.GetWindow()?.FramebufferSize;
        long need = size is { } s ? ReplayRenderLog.EstimateBytes(pictures, s.X, s.Y) : 0;
        return need > free ? string.Create(CultureInfo.InvariantCulture,
            $" The pictures can need up to {need / (double)gib:F1} GB, but only {free / (double)gib:F1} GB are free. The render stops when the disk is almost full.") : "";
    }

    // Runs in the prefix of Program.OnFrame, after the offline frame of the takes. It collects the
    // picture of the last frame, moves the replay to the time of the next picture and asks for it.
    public static void OnRenderFrame(ref double currentPlayerTime, ref double dtPlayer)
    {
        if (_strayCapture != null)
            DiscardStray();
        if (_render is not { } run)
            return;
        OfflineCapture capture = run.Capture;
        try
        {
            // The stock key that shows and hides the interface works in the key dispatch of the
            // game, so the render hides the interface again in each frame.
            Program.DrawUI = false;

            if (capture.Pending && !capture.Rendered && ++capture.CarryFrames <= RenderMaxCarryFrames)
            {
                // The last frame ended early, so its armed grab saves the picture in this frame.
                RenderStep(capture, ref currentPlayerTime, ref dtPlayer, OfflineCapture.RetryStepSeconds);
                return;
            }

            capture.CarryFrames = 0;
            if (capture.Pending && !CollectRendered(run))
                return;
            if (run.StopReason != null)
            {
                FinishRender(run.StopReason, true);
                return;
            }

            if (capture.Count >= run.Job.Total)
            {
                FinishRender(null, true);
                return;
            }

            if (Scene is not { } scene)
            {
                FinishRender("the replay closed", false);
                return;
            }

            int index = capture.Count;
            if (run.MovedTo != index)
            {
                run.MovedTo = index;
                scene.Player.MoveTo(run.Job.TakeTime(index));
                if (WriteRenderRows(run, scene, index) && index > 0)
                    run.Settle = Math.Max(run.Settle, CutSettleFrames);
            }

            if (run.Settle > 0)
            {
                run.Settle--;
                RenderStep(capture, ref currentPlayerTime, ref dtPlayer, OfflineCapture.RetryStepSeconds);
                return;
            }

            double step = capture.NextStep;
            capture.PostRenderMark = OfflineHooks.PostRenderCount;
            if (!OfflineHooks.RequestPicture())
            {
                // Another capture still runs. This frame makes no picture and stays small.
                RenderStep(capture, ref currentPlayerTime, ref dtPlayer, OfflineCapture.RetryStepSeconds);
                capture.Pending = false;
                return;
            }

            RenderStep(capture, ref currentPlayerTime, ref dtPlayer, step);
            capture.Pending = true;
            capture.Rendered = false;
            ShowRenderProgress(run);
        }
        catch (Exception ex)
        {
            Report(ex);
            try
            {
                FinishRender($"error: {ex.GetBaseException().Message}", true);
            }
            catch (Exception cleanup)
            {
                Report(cleanup);
                _render = null;
            }
        }
    }

    // Runs in the postfix of Program.OnFrame. A frame that returned early never reaches the end-of-frame
    // hook of the screenshot capture, so its picture comes in a later frame.
    public static void OnRenderFrameEnd()
    {
        if (_render?.Capture is { Pending: true } capture
            && (!OfflineHooks.Detects || OfflineHooks.PostRenderCount != capture.PostRenderMark))
            capture.Rendered = true;
    }

    private static void RenderStep(OfflineCapture capture, ref double currentPlayerTime, ref double dtPlayer, double step)
    {
        currentPlayerTime = capture.Advance(step, currentPlayerTime);
        dtPlayer = step;
    }

    // Writes the rows of the events that are due and of the shots and speed keys that start in the
    // frame of a picture. Returns true when a shot starts.
    private static bool WriteRenderRows(RenderRun run, ReplayScene scene, int index)
    {
        int picture = index + 1;
        while (run.NextEventRow < run.EventRows.Count && run.EventRows[run.NextEventRow].Picture <= picture)
            ReplayRenderLog.Write(run.Log, run.EventRows[run.NextEventRow++], run.Plan, run.FlightStart);
        if (scene.Player.Frame is not { } frame || run.Plan.Take is not { } take)
            return false;
        List<ReplayRenderRow> rows = ReplayRenderLog.FrameRows(take, frame, run.Job, index, out bool cut);
        foreach (ReplayRenderRow row in rows)
            ReplayRenderLog.Write(run.Log, row, run.Plan, run.FlightStart);
        return cut;
    }

    // Waits for the picture of the last frame. Returns false when the render ended because the
    // picture failed again and again.
    private static bool CollectRendered(RenderRun run)
    {
        OfflineCapture capture = run.Capture;
        int number = capture.FrameNumber;
        CollectResult result = capture.Collector.Wait(number, RenderCollectTimeout, () => NoRenderPicture(capture));
        capture.Pending = false;
        if (result == CollectResult.Done)
        {
            RenderSaved(run, number);
            return true;
        }

        bool giveUp = capture.RecordLost(RenderMaxFailuresInRow);
        string reason = OfflineHooks.Result == PostRenderResult.NoPicture ? "the game made no picture of the frame" : "no finished file came in time";
        string text = $"Picture {number} was not saved ({reason}). The next frame makes it again.";
        DefaultCategory.Log.Warning($"[CineCam] {text}");
        ReplayRenderLog.Write(run.Log, RenderRow(run, number, "warning", "frameGrab", text), run.Plan, run.FlightStart);
        if (giveUp)
        {
            FinishRender($"the picture grab failed {capture.FailuresInRow} times in a row, see the log", true);
            return false;
        }

        return true;
    }

    // A row at the time of the pictures saved so far.
    private static ReplayRenderRow RenderRow(RenderRun run, int picture, string kind, string name, string detail) =>
        new(run.Capture.Count / run.Capture.Fps, run.Job.TakeTime(run.Capture.Count), picture, kind, name, detail);

    private static bool NoRenderPicture(OfflineCapture capture) =>
        OfflineHooks.Detects && capture.Rendered && OfflineHooks.Result != PostRenderResult.Saving;

    private static void RenderSaved(RenderRun run, int number)
    {
        OfflineCapture capture = run.Capture;
        int before = capture.Count;
        capture.RecordSaved(number);
        try
        {
            capture.BytesWritten += new FileInfo(Path.Combine(capture.FramesFolder, OfflineOutput.FrameName(number))).Length;
        }
        catch (IOException)
        {
            // The size is only information.
        }

        if (ReplayRenderProgress.PassedTenth(before, number, run.Job.Total))
            DefaultCategory.Log.Info(string.Create(CultureInfo.InvariantCulture,
                $"[CineCam] Render: {run.Progress.Text()}, {capture.BytesWritten / (1024.0 * 1024.0):F0} MB."));

        if (number % DiskCheckEvery == 0)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(capture.FramesFolder));
                if (root != null && new DriveInfo(root).AvailableFreeSpace < ReplayRenderLog.StopFreeBytes)
                    run.StopReason ??= "the disk is almost full";
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // The free space is only a guard.
            }
        }
    }

    // The interface is hidden, and anything drawn in the game window is in the pictures. So the
    // progress goes into the title of the window, which is not part of the picture.
    private static void ShowRenderProgress(RenderRun run)
    {
        long now = Stopwatch.GetTimestamp();
        if (run.LastTitle != 0 && Stopwatch.GetElapsedTime(run.LastTitle, now) < TitleInterval)
            return;
        run.LastTitle = now;
        if (Program.GetWindow() is { } window)
            window.Title = $"CineCam render: {run.Progress.Text()} - {run.SavedTitle}";
    }

    // Ends the render and gives back the interface, the window title and the state of the replay. A
    // picture of a frame that finished is still collected first. With restoreScene false the replay
    // closes after this, so its time and camera stay as they are.
    private static void FinishRender(string? reason, bool restoreScene)
    {
        RenderRun? run = _render;
        if (run == null)
            return;
        _render = null;
        OfflineCapture capture = run.Capture;
        bool stray = false;
        try
        {
            if (capture.Pending && !capture.Rendered)
            {
                // The frame of the requested picture has not ended yet, for example when the replay
                // closes in the middle of the frame.
                stray = true;
            }
            else if (capture.Pending)
            {
                int number = capture.FrameNumber;
                if (capture.Collector.Wait(number, RenderCollectTimeout, () => NoRenderPicture(capture)) == CollectResult.Done)
                    RenderSaved(run, number);
                else
                    DefaultCategory.Log.Warning($"[CineCam] Picture {number} was not saved.");
            }
        }
        catch (Exception ex)
        {
            Report(ex);
        }
        finally
        {
            if (stray)
            {
                capture.CarryFrames = 0;
                _strayCapture = capture;
            }
            else
            {
                capture.Pending = false;
                OfflineHooks.Reset();
            }
        }

        string ending = reason ?? "the take end";
        try
        {
            ReplayRenderLog.Write(run.Log, RenderRow(run, capture.Count, "take", "end", ending), run.Plan, run.FlightStart);
            run.Log.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            DefaultCategory.Log.Error($"[CineCam] The event log of the render could not be closed: {ex.Message}");
        }

        try
        {
            if (run.SavedTitle != null && Program.GetWindow() is { } window)
                window.Title = run.SavedTitle;
            Program.DrawUI = run.SavedDrawUi;
            Program.ConsoleWindow?.EnableMessageFading();
            RenderAlerts.RemoveNew(run.SavedAlerts);
            if (restoreScene && Scene is { } scene)
            {
                scene.SetPreviewSpeed(run.SavedSpeed);
                scene.Seek(run.SavedTime);
                scene.SetCameraMode(run.SavedMode);
                scene.SetPlaying(run.SavedPlaying);
            }
        }
        catch (Exception ex)
        {
            Report(ex);
        }

        ReplayRenderProgress progress = run.Progress;
        string verb = reason == null ? "Render done" : $"Render ended ({reason})";
        string result = string.Create(CultureInfo.InvariantCulture,
            $"{verb}: {capture.Count} of {run.Job.Total} pictures ({capture.Count / capture.Fps:F1} s of video, {capture.BytesWritten / (1024.0 * 1024.0):F0} MB) in {ReplayRenderProgress.Span(progress.ElapsedSeconds)}, {capture.PicturesLost} lost and made again. Folder: {capture.OutputFolder}");
        _lastRender = run.Status(result);
        Status = result;
        DefaultCategory.Log.Info($"[CineCam] {result}");
    }

    // Takes the picture of a render that ended in the middle of its frame out of the screenshot
    // folder, so it does not stay there as a screenshot of the player.
    private static void DiscardStray()
    {
        OfflineCapture capture = _strayCapture!;
        bool frameEnded = !OfflineHooks.Detects || OfflineHooks.PostRenderCount != capture.PostRenderMark;
        // A frame that returned early saves the armed picture in a later frame.
        if (!frameEnded && ++capture.CarryFrames <= RenderMaxCarryFrames)
            return;
        _strayCapture = null;
        try
        {
            if (frameEnded && OfflineHooks.Result != PostRenderResult.NoPicture
                && capture.Collector.Discard(StrayTimeout, () => OfflineHooks.Detects && OfflineHooks.Result == PostRenderResult.NoPicture))
                DefaultCategory.Log.Info("[CineCam] The picture of the frame in which the render ended was removed from the screenshot folder.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DefaultCategory.Log.Warning($"[CineCam] The picture of the frame in which the render ended could not be removed: {ex.Message}");
        }
        finally
        {
            capture.Pending = false;
            OfflineHooks.Reset();
        }
    }

    private static void ShutdownRender()
    {
        _lastRender = null;
        if (_strayCapture is { } capture)
        {
            capture.Pending = false;
            OfflineHooks.Reset();
        }

        _strayCapture = null;
    }

    // The alerts that the game makes during a render would show after it, because the interface is
    // hidden. TimedAlert has no public way to remove one alert, so this uses the private list
    // TimedAlert.All. When the list is not found, the alerts stay and end at their own time.
    private static class RenderAlerts
    {
        private static readonly FieldInfo? AllField = AccessTools.Field(typeof(TimedAlert), "All");

        private static List<TimedAlert>? All => AllField?.GetValue(null) as List<TimedAlert>;

        public static HashSet<TimedAlert>? Snapshot() =>
            All is { } all ? new HashSet<TimedAlert>(all, ReferenceEqualityComparer.Instance) : null;

        public static void RemoveNew(HashSet<TimedAlert>? before)
        {
            if (before != null && All is { } all)
                all.RemoveAll(alert => !before.Contains(alert));
        }
    }
}
