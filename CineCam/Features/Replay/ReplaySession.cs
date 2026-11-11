using System.Diagnostics;
using Brutal.Logging;
using CineCam.Core;
using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Editing;
using CineCam.Features.Takes;
using CineCam.Features.Ui;
using KSA;

namespace CineCam.Features.Replay;

// Records flights and plays the take selected in the editor on a recording. Each change in the
// editor resolves the take again on a worker thread, and the replay plays the last plan until the
// new one is ready.
internal static partial class ReplaySession
{
    // A rough seek or an apply during an edit is made exact when nothing moved it for this long.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);

    private sealed record PendingResolve(int Version, ReplayScene.TakeRequest Request, CancellationTokenSource Cancel,
        Task<(ReplayTakePlan Plan, string Problem)> Task);

    private static FlightRecorder? _recorder;
    private static ReplayFlight? _unsavedFlight;
    private static string? _unsavedPath;
    private static IReadOnlyList<string>? _files;
    private static string? _replayId;
    private static int _editorVersion = -1;
    private static int _appliedVersion = -1;
    private static PendingResolve? _resolving;
    private static double? _scrubTarget;
    private static long _lastRough;
    private static (string Id, string Path, int Saves)? _pendingImport;
    private static string? _takeName;
    private static string? _recordingTake;
    private static bool _recordingForTake;
    private static string _takeProblem = "";
    private static string Folder => Path.Combine(CineCamPaths.Root, "replays");
    private static string ProjectFolder => Path.Combine(CineCamPaths.Root, "projects");
    private const string Extension = ".ccreplay";

    internal static ReplayScene? Scene { get; private set; }
    public static bool Available { get; set; }
    public static bool IsRecording => _recorder != null;
    public static bool HasUnsavedFlight => _unsavedFlight != null;
    public static bool IsPlayingBack => Scene != null;
    /// <summary>True while a recording runs or a replay is open. A recording alone does not block a live take, see <see cref="IsPlayingBack"/>.</summary>
    public static bool IsBusy => IsRecording || IsPlayingBack;

    /// <summary>
    /// Why a checkpoint save or a reset cannot run now, or an empty text. A reset loads a save, which
    /// ends a recording, so a recording that the player started blocks it. The recording of a take is
    /// saved when the reset ends the take.
    /// </summary>
    public static string CheckpointBlock(bool reset) =>
        IsPlayingBack ? "Exit the replay first."
        : reset && IsRecording && !_recordingForTake ? "Stop the recording first. A reset loads a save, which ends the recording."
        : "";
    public static bool IsPlaying => Scene?.IsPlaying == true;

    /// <summary>The take time of the replay, the time of the video.</summary>
    public static double Time => Scene?.TakeTime ?? 0;

    /// <summary>The take seconds until the end of the recording.</summary>
    public static double Duration => Scene?.Duration ?? 0;

    public static double RecordingTime => Scene?.Time ?? 0;
    public static double RecordingDuration => Scene?.RecordingDuration ?? 0;
    public static double Speed => Scene?.Player.PreviewSpeed ?? 1;
    public static double RecordingSeconds => _recorder?.Duration ?? 0;
    public static string? SelectedVehicleName => _recorder?.FollowedName ?? Program.ControlledVehicle?.Id;
    public static string? ReplayId => Scene == null ? null : _replayId;
    public static double FieldOfView => Scene?.FieldOfView ?? 0;
    public static string Status { get; private set; } = "";
    public static ReplayFlight? Flight => Scene?.Flight;
    public static int? FollowActorId => Scene?.FollowActorId;
    public static ReplayCameraMode CameraMode => Scene?.CameraMode ?? ReplayCameraMode.Free;
    public static ReplayTakePlan? Plan => Scene?.Player.Plan;
    public static DirectorFrame? Frame => Scene?.Player.Frame;

    /// <summary>The name of the take that the replay plays, or null when it plays none.</summary>
    public static string? TakeName => Scene == null ? null : _takeName;

    /// <summary>Why the take of the editor does not play, or an empty text.</summary>
    public static string TakeProblem => Scene == null ? "" : _takeProblem;

    public static string? OpenBlockReason => ReadyBlock().Message;
    public static string? StartBlockReason => StartBlock().Message;

    public static IReadOnlyList<string> ListFiles()
    {
        try
        {
            return _files ??= Directory.Exists(Folder)
                ? Directory.GetFiles(Folder, "*" + Extension).OrderDescending(StringComparer.Ordinal).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"The replay folder could not be read: {ex.Message}";
            return [];
        }
    }

    public static bool CanFollow(int actorId) => Scene?.CanFollow(actorId) == true;

    public static void UpdateFlight()
    {
        if (Scene is { } scene && (!ReferenceEquals(Universe.CurrentSystem, scene.System)
                || Program.EditorFlag || Program.Editor != null || !Program.IsLoaded))
            OnWorldChange();

        ReplayBridge.Poll();
        Dispatch();
        RememberImport();

        if (_recorder != null)
        {
            try
            {
                if (_recorder.Capture() is { } reason)
                    FinishRecording(reason);
                else if (_recorder.TakeNote() is { } note)
                    Status = $"{Status} {note}";
            }
            catch (Exception ex)
            {
                Report(ex);
                FinishAfterError("The recording stopped after an error.");
            }
        }
    }

    public static void UpdatePlayback(double frameTime)
    {
        if (Scene is { } scene)
        {
            try
            {
                if (_render == null)
                {
                    SyncTake(scene, false);
                    ApplyScrub(scene);
                }

                scene.Update(frameTime);
            }
            catch (Exception ex)
            {
                Abort(ex);
            }
        }
    }

    public static void ObserveSplit(Vehicle source, Vehicle child) => _recorder?.Split(source, child);

    /// <summary>Gives a mark to the recording that runs. Returns false when no recording runs.</summary>
    public static bool RequestMark()
    {
        if (_recorder == null)
            return false;
        _recorder.AddMark();
        return true;
    }

    public static void OnWorldChange()
    {
        CancelRequests();
        CancelPreview("The scene changed.");
        FinishAfterError("The recording ended when the scene changed.");
        try
        {
            CloseCore();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    public static void Abort(Exception error)
    {
        try
        {
            CloseCore();
        }
        catch (Exception cleanup)
        {
            Report(cleanup);
        }
        finally
        {
            Report(error);
        }
    }

    public static void Shutdown()
    {
        OnWorldChange();
        if (_unsavedFlight != null)
        {
            try
            {
                SaveRecording();
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        }
        _unsavedFlight = null;
        _unsavedPath = null;
        Available = false;
        _files = null;
        ShutdownLibrary();
        ShutdownRender();
        _pendingImport = null;
        Status = "";
    }

    private static void BeginRecording(string? takeName = null)
    {
        Require(StartBlock());
        CelestialSystem system = Universe.CurrentSystem!;
        Vehicle vehicle = Program.ControlledVehicle!;
        var body = (Celestial)vehicle.Parent;
        _recorder = new FlightRecorder(system, body, vehicle);
        _recordingTake = takeName;
        _recordingForTake = false;
        Status = takeName == null ? $"Recording {vehicle.Id}." : $"Recording {vehicle.Id} with the take \"{takeName}\".";
    }

    /// <summary>
    /// Starts a recording that ends with the take of this name, see <see cref="EndTakeRecording"/>.
    /// Returns why it did not start, or null.
    /// </summary>
    public static string? BeginTakeRecording(string takeName)
    {
        try
        {
            BeginRecording(takeName);
            _recordingForTake = true;
            return null;
        }
        catch (Refusal refusal)
        {
            return refusal.Message;
        }
        catch (Exception ex)
        {
            Report(ex);
            return ex.GetBaseException().Message;
        }
    }

    /// <summary>Saves the recording that <see cref="BeginTakeRecording"/> started. A recording that the player started goes on.</summary>
    public static void EndTakeRecording()
    {
        if (!_recordingForTake || _recorder == null)
            return;
        try
        {
            FinishRecording();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private static void FinishRecording(string? reason = null)
    {
        FlightRecorder? recorder = _recorder;
        string? takeName = _recordingTake;
        _recorder = null;
        _recordingTake = null;
        _recordingForTake = false;
        if (recorder == null)
            return;
        ReplayFlight flight = recorder.Flight;
        if (flight.Actors.Count == 0 || flight.Actors[0].Segments.Count == 0)
        {
            Status = reason ?? "The recording contained no flight samples.";
            return;
        }
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string take = takeName != null && RecordingTakePart(takeName) is { Length: > 0 } part ? " " + part : "";
        string name = $"Flight {DateTime.Now:yyyy-MM-dd HH-mm-ss}{take} {suffix}";
        _unsavedFlight = flight;
        _unsavedPath = Path.Combine(Folder, name + Extension);
        SaveRecording();
        Status = reason == null ? "Flight saved. It is at the top of the list below." : $"{reason} The recorded portion was saved.";
    }

    // The take name in the name of a recording: safe in a file name and short enough for the id of a recording.
    private static string RecordingTakePart(string takeName)
    {
        string safe = TakeFolders.SafeName(takeName.Trim()).Trim('.');
        return safe.Length > 60 ? safe[..60] : safe;
    }

    private static void SaveRecording()
    {
        if (_unsavedFlight == null || _unsavedPath == null)
            return;
        ReplayFile.Save(_unsavedPath, _unsavedFlight);
        _unsavedFlight = null;
        _unsavedPath = null;
        _files = null;
        Status = "Flight saved. It is at the top of the list below.";
    }

    private static void FinishAfterError(string reason)
    {
        try
        {
            FinishRecording(reason);
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private static void OpenCore(string id)
    {
        Require(ReadyBlock());
        string? path = IsRecordingId(id) ? ListFiles().FirstOrDefault(path =>
            string.Equals(Path.GetFileNameWithoutExtension(path), id, StringComparison.Ordinal)) : null;
        if (path == null)
            throw new Refusal("recording_not_found", "Choose a recording from the library.");
        CelestialSystem system = Universe.CurrentSystem!;
        ReplayFlight flight = ReplayFile.Load(path);
        string gameVersion = VersionInfo.Current.VersionString;
        if (!string.Equals(flight.GameVersion, gameVersion, StringComparison.Ordinal))
            throw new Refusal("game_version_mismatch", $"This replay needs KSA {flight.GameVersion}. The current version is {gameVersion}.");
        if (!string.Equals(flight.SystemId, system.Id, StringComparison.Ordinal) || system.Get<Celestial>(flight.BodyId) is not { } body)
            throw new Refusal("system_mismatch", $"Open the {flight.SystemId} system with {flight.BodyId} to play this replay.");

        Quick.QuickCamera.Stop("a replay opens");
        var scene = new ReplayScene(flight, system, body);
        try
        {
            scene.Enter();
            Scene = scene;
            _replayId = id;
            string imported = ImportLegacy(scene, id);
            SyncTake(scene, true);
            scene.SetCameraMode(scene.Player.Plan.Take is { Shots.Count: > 0 } ? ReplayCameraMode.Take : ReplayCameraMode.Free);
            string note = flight.FormatVersion < ReplayFile.CurrentVersion
                ? " This recording is from an older version: only the lift-off and the ignitions are known as events."
                : "";
            Status = $"Opened {id}.{note}{imported}";
        }
        catch
        {
            Scene = null;
            _replayId = null;
            scene.Dispose();
            throw;
        }
    }

    private static void CloseCore()
    {
        CancelPreview("The replay closed.");
        bool rendering = _render != null;
        FinishRender("the replay closed", false);
        ReplayScene? scene = Scene;
        Scene = null;
        _replayId = null;
        _takeName = null;
        _takeProblem = "";
        _editorVersion = -1;
        _appliedVersion = -1;
        _scrubTarget = null;
        CancelResolve();
        _eventsPlan = null;
        _events = [];
        _markersPlan = null;
        _markers = [];
        if (scene != null)
        {
            scene.Dispose();
            // The result of a render that the close ended stays in the status.
            Status = rendering ? $"Replay closed. {Status}" : "Replay closed. Returned to the live flight.";
        }
    }

    /// <summary>Moves the replay to a take time with a rough seek, for a drag of the play head. The last call of a frame wins.</summary>
    public static void Scrub(double time)
    {
        if (Scene != null && _render == null && _preview == null && double.IsFinite(time))
            _scrubTarget = time;
    }

    /// <summary>Resolves the take of the editor now when the replay plays an older version of it, so a new shot uses the timeline of the take as it is.</summary>
    public static void EnsureCurrentTake()
    {
        if (Scene is { } scene && _render == null && CineCamWindow.EditorSession.Version != _appliedVersion)
            SyncTake(scene, true);
    }

    // A drag of the play head seeks roughly on each frame, and the replay makes the seek exact when
    // the play head stands still for the settle delay.
    private static void ApplyScrub(ReplayScene scene)
    {
        long now = Stopwatch.GetTimestamp();
        if (_scrubTarget is double target)
        {
            _scrubTarget = null;
            scene.Seek(target, true);
            _lastRough = now;
        }
        else if (scene.Player.IsRough && Stopwatch.GetElapsedTime(_lastRough, now) >= SettleDelay)
        {
            scene.Settle();
        }
    }

    // A change during a resolve starts the next one when it is done, so a drag in the editor gives
    // new plans as fast as the worker makes them. A forced sync resolves at once on the main thread.
    private static void SyncTake(ReplayScene scene, bool force)
    {
        EditSession session = CineCamWindow.EditorSession;
        if (_resolving is { } running && (force || running.Task.IsCompleted))
        {
            _resolving = null;
            if (force)
                running.Cancel.Cancel();
            else
                FinishResolve(scene, running);
            running.Cancel.Dispose();
        }

        if (!force && (session.Version == _editorVersion || _resolving != null))
            return;
        _editorVersion = session.Version;
        if (session.Document == null || session.Take is not { } selected)
        {
            scene.LoadTake(null, null);
            _appliedVersion = session.Version;
            _takeName = null;
            _takeProblem = "The editor has no take. Make one on the Editor page.";
            return;
        }

        ShotFile copy;
        try
        {
            copy = session.CopyForRun();
        }
        catch (ShotFileException ex)
        {
            _appliedVersion = session.Version;
            _takeName = null;
            _takeProblem = $"The take has errors, so the replay plays it as it was before: {ex.Message}";
            return;
        }

        ReplayScene.TakeRequest request = scene.PrepareTake(copy.FindTake(selected.Name), copy);
        if (force)
        {
            ReplayTakePlan plan = ReplayTakePlayer.Resolve(scene.World, request.Take, request.File, out string problem);
            Apply(scene, session.Version, request, plan, problem, false);
            return;
        }

        var cancel = new CancellationTokenSource();
        CancellationToken token = cancel.Token;
        ReplayWorld world = scene.World;
        Task<(ReplayTakePlan, string)> task = Task.Run(() =>
        {
            ReplayTakePlan plan = ReplayTakePlayer.Resolve(world, request.Take, request.File, out string problem, token);
            return (plan, problem);
        }, token);
        _resolving = new PendingResolve(session.Version, request, cancel, task);
    }

    private static void FinishResolve(ReplayScene scene, PendingResolve resolve)
    {
        if (resolve.Task.IsCanceled)
            return;
        if (resolve.Task.Exception is { } error)
        {
            Report(error);
            return;
        }

        (ReplayTakePlan plan, string problem) = resolve.Task.Result;
        Apply(scene, resolve.Version, resolve.Request, plan, problem, true);
    }

    // A take that an edit made impossible to play keeps the last good plan of the same take, as a
    // take with errors does, so the picture does not jump to the free camera while the player fixes it.
    private static void Apply(ReplayScene scene, int version, ReplayScene.TakeRequest request, ReplayTakePlan plan, string problem, bool rough)
    {
        _appliedVersion = version;
        string site = request.SiteNote.Length > 0 ? $" Site: {request.SiteNote}." : "";
        if (problem.Length > 0 && scene.Player.Problem.Length == 0 && scene.Player.Plan.Take is { } last
            && request.Take != null && string.Equals(last.Name, request.Take.Name, StringComparison.OrdinalIgnoreCase))
        {
            _takeName = null;
            _takeProblem = $"The take cannot play, so the replay plays it as it was before: {problem}." + site;
            return;
        }

        scene.ApplyTake(request, plan, problem, rough);
        if (rough)
            _lastRough = Stopwatch.GetTimestamp();
        _takeName = problem.Length == 0 ? request.Take?.Name : null;
        _takeProblem = problem.Length > 0 ? $"The take cannot play: {problem}." + site
            : plan.Truncated ? "The take is longer than the replay can plan, so it ends early." + site
            : site.Trim();
    }

    private static void CancelResolve()
    {
        if (_resolving is not { } running)
            return;
        _resolving = null;
        running.Cancel.Cancel();
        running.Cancel.Dispose();
    }

    // Each old replay project of this recording becomes a take in the editor. The import comes again
    // on each open until the player saves the editor, and then a marker file stops it, so a take that
    // the player deleted stays deleted.
    private static string ImportLegacy(ReplayScene scene, string id)
    {
        if (File.Exists(ImportMarker(id)))
            return "";
        var notes = new List<string>();
        List<ReplayLegacyProject> projects;
        try
        {
            projects = ReplayLegacy.Read(scene.Flight, ProjectFolder, id, notes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $" The old replay projects could not be read: {ex.Message}";
        }

        if (projects.Count == 0)
            return notes.Count > 0 ? " " + notes[0] : "";
        EditSession session = CineCamWindow.EditorSession;
        if (session.Document == null)
            return " Old replay views were found. Open the editor to import them.";
        _pendingImport = (id, session.Path, session.SaveCount);

        int added = 0, skipped = 0;
        string? first = null;
        foreach (ReplayLegacyProject project in projects)
        {
            string name = $"Replay {id} {project.Name}";
            if (session.Document.FindTake(name) != null)
                continue;
            var cameras = new List<LegacyCamera>();
            foreach (ReplayView view in project.Views)
            {
                if (scene.LegacyCameraOf(view) is { } camera)
                    cameras.Add(camera);
                else
                    skipped++;
            }

            SiteFrame site = scene.SiteFor(null, null, out _);
            Take take = ReplayLegacy.ToTake(name, cameras, project.SpeedKeys, scene.World, site.ToReplaySite());
            take.Anchor = site.Landmark == null ? site.Anchor?.Clone() : null;
            if (session.ImportTake(take))
            {
                added++;
                first ??= name;
            }
        }

        if (first != null)
            session.SelectTakeByName(first);
        string text = added == 0 ? "" : added == 1
            ? $" The views of an old replay project became the take \"{first}\". Save the editor to keep it."
            : $" The views of {added} old replay projects became takes, the first is \"{first}\". Save the editor to keep them.";
        if (skipped > 0)
            text += $" {skipped} view(s) were left out because their vehicle or part is not in the recording at their time.";
        return text + (notes.Count > 0 ? " " + notes[0] : "");
    }

    private static string ImportMarker(string id) => Path.Combine(ProjectFolder, id + ".imported");

    // Writes the marker of an import once the editor file that took the imported takes is saved.
    // Another file in the editor drops the import, so it comes again on the next open.
    private static void RememberImport()
    {
        if (_pendingImport is not { } pending)
            return;
        EditSession session = CineCamWindow.EditorSession;
        if (!string.Equals(session.Path, pending.Path, StringComparison.OrdinalIgnoreCase) || session.Document == null)
        {
            _pendingImport = null;
            return;
        }

        // Only a save keeps the imported takes. A revert or a reload also leaves a clean document,
        // and then the import must come again on the next open.
        if (session.SaveCount == pending.Saves)
        {
            if (!session.Dirty)
                _pendingImport = null;
            return;
        }

        _pendingImport = null;
        try
        {
            Directory.CreateDirectory(ProjectFolder);
            File.WriteAllText(ImportMarker(pending.Id), "The old replay projects of this recording were imported into " + pending.Path + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DefaultCategory.Log.Warning($"[CineCam] The import of the old replay projects could not be remembered: {ex.Message}");
        }
    }

    private static bool IsRecordingId(string? id) => id is { Length: > 0 and <= 120 }
        && id == id.Trim() && !id.EndsWith('.') && id is not "." and not ".."
        && !id.Any(c => c < ' ' || c is '/' or '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*');

    private static void Report(Exception error)
    {
        Status = error.GetBaseException().Message;
        DefaultCategory.Log.Warning($"[CineCam] Replay: {error}");
    }
}
