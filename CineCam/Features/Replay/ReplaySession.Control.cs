using System.Text.Json;
using Brutal.Logging;
using CineCam.Core;
using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Editing;
using CineCam.Features.Sound;
using CineCam.Features.Takes;
using CineCam.Features.TimeOfDay;
using CineCam.Features.Ui;
using CineCam.Patches;
using KSA;

namespace CineCam.Features.Replay;

internal static partial class ReplaySession
{
    private sealed record Request(ReplayCommand Command, Action<ReplayResult>? Completion);
    private sealed class Refusal(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private static readonly Queue<Request> Requests = new();
    public const double MaximumFieldOfView = 150;
    // The window key shows the interface again, so the interface hides only when that key works.
    public static bool CanHideInterface => CineCamPatches.KeysAvailable && CineCamSettings.WindowKey.HasValue;

    public static void StartRecording() => Submit(new(ReplayOperation.StartRecording));
    public static void StopRecording() => Submit(new(ReplayOperation.StopRecording));
    public static void RetrySave() => Submit(new(ReplayOperation.RetrySave));
    /// <summary>Opens a recording. A recording of an older game build opens only with <paramref name="olderConfirmed"/>.</summary>
    public static void Open(string path, bool olderConfirmed = false) =>
        Submit(new(ReplayOperation.Open, Id: Path.GetFileNameWithoutExtension(path), Enabled: olderConfirmed));
    public static void Close() => Submit(new(ReplayOperation.Close));
    public static void Seek(double time) => Submit(new(ReplayOperation.Seek, Time: time));
    public static void Follow(int? actorId) => Submit(new(ReplayOperation.Follow, ActorId: actorId));
    public static void ToggleInterface() => Submit(new(ReplayOperation.SetInterface, Enabled: !Program.DrawUI));
    public static void SetPlaying(bool enabled) => Submit(new(ReplayOperation.SetPlaying, Enabled: enabled));
    public static void SetSpeed(double speed) => Submit(new(ReplayOperation.SetSpeed, Speed: speed));
    public static void SetFov(double fov) => Submit(new(ReplayOperation.SetFov, Fov: fov));
    public static void SetCamera(ReplayCameraMode mode) =>
        Submit(new(ReplayOperation.SetCamera, Name: mode == ReplayCameraMode.Take ? "take" : "free"));

    public static void Submit(ReplayCommand command, Action<ReplayResult>? completion = null)
    {
        if (Requests.Count >= 64)
        {
            Complete(completion, false, "busy", UiText.MessageText("text.4554b90ec48c", "The replay command queue is full."));
            return;
        }
        Requests.Enqueue(new Request(command, completion));
    }

    private static void Dispatch()
    {
        for (int count = 0; count < 16 && Requests.TryDequeue(out Request? request); count++)
        {
            try
            {
                if (_preview != null && request.Command.Operation is not
                    (ReplayOperation.Status or ReplayOperation.List or ReplayOperation.Close))
                    throw new Refusal("busy", UiText.MessageText("text.196eea15fe70", "Wait for the preview frame to finish."));
                if (_render != null && request.Command.Operation is not
                    (ReplayOperation.Status or ReplayOperation.List or ReplayOperation.Close or ReplayOperation.RenderStop))
                    throw new Refusal("rendering", UiText.MessageText("text.07560293d8e6", "A render runs. Stop it first with Escape or the start and stop key."));
                if (request.Command.Operation == ReplayOperation.Preview)
                {
                    BeginPreview(request.Completion);
                    continue;
                }
                string message = Execute(request.Command);
                if (request.Command.Operation is not (ReplayOperation.Status or ReplayOperation.List))
                    Status = message;
                Complete(request.Completion, true, UiText.MessageText("text.2689367b205c", "ok"), message);
            }
            catch (Refusal ex)
            {
                Complete(request.Completion, false, ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                Report(ex);
                string code = ex switch
                {
                    FileNotFoundException => "file_not_found",
                    InvalidDataException or JsonException => "invalid_data",
                    IOException or UnauthorizedAccessException => "file_error",
                    _ => "internal_error"
                };
                Complete(request.Completion, false, code, Status);
            }
        }
    }

    private static string Execute(ReplayCommand command)
    {
        switch (command.Operation)
        {
            case ReplayOperation.Status:
                return "Replay status.";
            case ReplayOperation.List:
                _files = null;
                return "Replay library.";
            case ReplayOperation.StartRecording:
                BeginRecording();
                return Status;
            case ReplayOperation.StopRecording:
                if (!IsRecording)
                    throw new Refusal("not_recording", UiText.MessageText("text.cee26c069c3d", "There is no active flight recording."));
                FinishRecording();
                return Status;
            case ReplayOperation.RetrySave:
                if (!HasUnsavedFlight)
                    throw new Refusal("no_unsaved_flight", UiText.MessageText("text.9ce8066b4963", "There is no flight waiting to be saved."));
                SaveRecording();
                return Status;
            case ReplayOperation.Open:
                OpenCore(Text(command.Id, "recording ID"), command.Enabled == true);
                return Status;
            case ReplayOperation.Close:
                NeedScene();
                CloseCore();
                return Status;
            case ReplayOperation.SetInterface:
                bool show = Flag(command.Enabled);
                if (!show && !CanHideInterface)
                    throw new Refusal("hotkeys_unavailable", UiText.MessageText("text.f3861c703104", "No key can show the interface again. Set a window key on the Settings page, or the key patch is not available on this game build."));
                if (!show)
                    NeedScene();
                OfflineHooks.SetDrawUi(show);
                ApiFeed.SyncInterface();
                return Program.DrawUI ? UiText.MessageText("text.5a12f273895b", "Interface shown.") : UiText.MessageText("text.ff08864fe11c", "Interface hidden.");
            case ReplayOperation.Reload:
                return ReloadShotFile(command.Discard == true);
            case ReplayOperation.SelectTake:
                return SelectTake(Text(command.Name, "take name"));
            case ReplayOperation.Quit:
                return Quit();
            case ReplayOperation.Unknown:
                throw new Refusal("unknown_operation", UiText.MessageText("text.88c8a5d0ca82", "Choose a supported CineCam operation."));
        }

        ReplayScene scene = NeedScene();
        switch (command.Operation)
        {
            case ReplayOperation.Seek:
                _scrubTarget = null;
                scene.Seek(Number(command.Time, UiText.MessageText("text.589043cfaf25", "take time"), 0, scene.Duration));
                return "";
            case ReplayOperation.SetPlaying:
                scene.SetPlaying(Flag(command.Enabled));
                return IsPlaying ? UiText.MessageText("text.0a629c7c205c", "Replay playing.") : UiText.MessageText("text.a7cd145fc248", "Replay paused.");
            case ReplayOperation.SetSpeed:
                scene.SetPreviewSpeed(Number(command.Speed, "preview speed", ReplayTakePlayer.MinPreviewSpeed, ReplayTakePlayer.MaxPreviewSpeed));
                return UiText.MessageText("text.8e217389d671", "Preview speed set.");
            case ReplayOperation.SetCamera:
                ReplayCameraMode mode = command.Name?.Trim().ToLowerInvariant() switch
                {
                    "take" => ReplayCameraMode.Take,
                    "free" => ReplayCameraMode.Free,
                    _ => throw new Refusal("invalid_argument", UiText.MessageText("text.aed8cc0b6559", "Choose the camera \"take\" or \"free\".")),
                };
                scene.SetCameraMode(mode);
                return mode == ReplayCameraMode.Take ? UiText.MessageText("text.720b551d06dd", "The camera follows the take.") : UiText.MessageText("text.fcc629c87a59", "The camera is free.");
            case ReplayOperation.Follow:
                if (command.ActorId is int id && !scene.CanFollow(id))
                    throw new Refusal("actor_unavailable", UiText.MessageText("text.ac7ba79eaf3d", "This vehicle is unavailable at the current replay time."));
                scene.Follow(command.ActorId);
                return command.ActorId == null ? UiText.MessageText("text.36e9caaa4fee", "Free camera selected.") : UiText.MessageText("text.8e517524b82a", "The free camera follows the vehicle.");
            case ReplayOperation.SetFov:
                scene.SetFov(Number(command.Fov, UiText.MessageText("text.9388e97798cb", "field of view"), 1, MaximumFieldOfView));
                return UiText.MessageText("text.e6612d6f1387", "Camera field of view set.");
            case ReplayOperation.Render:
                return BeginRender(scene, RenderRange(command.Name));
            case ReplayOperation.RenderStop:
                return RequestRenderStop();
            case ReplayOperation.AddKey:
                string kindName = Text(command.Name, "kind of shot");
                KeyKind kind = Enum.GetValues<KeyKind>().FirstOrDefault(k => string.Equals(k.ToString(), kindName.Replace("-", ""), StringComparison.OrdinalIgnoreCase), (KeyKind)(-1));
                if (!Enum.IsDefined(kind))
                    throw new Refusal("invalid_argument", UiText.MessageText("text.bde60932b26e", "Choose a kind: fixed, track, chase, orbit, part-mount or path-point."));
                if (!KeyCapture.AddKey(CineCamWindow.EditorSession, kind, out string added))
                    throw new Refusal("key_failed", added);
                return added;
            default:
                throw new Refusal("unknown_operation", UiText.MessageText("text.88c8a5d0ca82", "Choose a supported CineCam operation."));
        }
    }

    // The same rule as the Reload button of the window: the file is read only while no take runs.
    private static string ReloadShotFile(bool discard)
    {
        EditSession session = CineCamWindow.EditorSession;
        string block = ViewText.FileEditBlock(TakeRunner.Current != null);
        if (block.Length > 0)
            throw new Refusal("busy", block);
        if (session.Dirty && !discard)
            throw new Refusal("unsaved_changes", UiText.MessageText("text.b7baca3b1dc4", "The editor has unsaved changes. Save them in the window, or reload with --discard to drop them."));
        if (!File.Exists(session.Path))
            throw new Refusal("file_not_found", UiText.Format("text.063cccdfb337", $"There is no shot file at {session.Path}."));
        if (!session.Reload())
            throw new Refusal("invalid_data", session.Message);
        ShotFileStore.PathOverride = session.Path;
        return UiText.Format("text.09ecf0f42774", $"{session.Message} Selected take: {session.Take?.Name}.");
    }

    private static string SelectTake(string name)
    {
        EditSession session = CineCamWindow.EditorSession;
        if (session.Document is not { } document)
            throw new Refusal("no_file", UiText.MessageText("text.9f6bb4bade69", "The editor has no shot file."));
        if (!session.SelectTakeByName(name))
            throw new Refusal("unknown_take", UiText.Format("text.72d3a14592c3", $"The shot file has no take \"{name}\". Takes: {UiText.Join(", ", document.Takes.Select(t => t.Name))}."));
        return UiText.Format("text.627171825ad4", $"Take \"{session.Take!.Name}\" selected.");
    }

    // The same path as the Quit button of the game menu (EscMenu, ExitPopup): Program.Instance.RequestExit(0)
    // ends the frame loop of App.Run after this frame, so the response of this request is written first.
    // An open replay closes before, so the interface and the camera are as the player left them.
    private static string Quit()
    {
        var state = new QuitState(_render != null, OfflineHooks.IsBusy, IsRecording, HasUnsavedFlight,
            TakeRunner.IsRunning, TakeRunner.Reset.IsActive) { SoundWrite = GameSoundRecorder.IsFinalizing };
        if (ControlRules.QuitBlock(state) is { } block)
            throw new Refusal(block.Code, block.Message);
        if (Program.Instance is not { } program)
            throw new Refusal("unavailable", UiText.MessageText("text.fd9e017ec06c", "The game cannot end now."));
        if (Scene != null)
            CloseCore();
        program.RequestExit(0);
        DefaultCategory.Log.Info("[CineCam] Local control asked the game to end.");
        return UiText.MessageText("text.92dd724b7959", "The game ends.");
    }

    // A replay takes the camera, the sim speed and the vehicles, so it opens only when no take runs.
    private static (string Code, string? Message) ReadyBlock() =>
        SceneBlock() is { Message: not null } block ? block
            : TakeRunner.IsRunning ? ("busy", UiText.MessageText("text.6877895466ce", "Finish the active CineCam operation first.")) : (UiText.MessageText("text.2689367b205c", "ok"), null);

    // A recording only reads the flight, so a take or a preview may run during it. A reset loads a
    // save and the move of the time of day jumps the clock, and both would end it.
    private static (string Code, string? Message) SceneBlock()
    {
        if (!Available)
            return ("unavailable", UiText.MessageText("text.a2bd10a2c816", "Replay is unavailable on this game build."));
        if (IsBusy || TakeRunner.Reset.IsActive || SunWarp.IsRunning)
            return ("busy", UiText.MessageText("text.6877895466ce", "Finish the active CineCam operation first."));
        if (!Program.IsLoaded || Program.EditorFlag || Program.Editor != null || Universe.CurrentSystem == null)
            return ("flight_required", UiText.MessageText("text.8e8ca00447ba", "Open a flight scene to use CineCam replay."));
        if (Universe.IsAutoWarpActive)
            return ("auto_warp_active", UiText.MessageText("text.8259538d0c3f", "Stop auto warp before using CineCam replay."));
        if (GameMenuOpen)
            return ("menu_open", UiText.MessageText("text.de184eb214bc", "Close the game menu before using CineCam replay."));
        return (UiText.MessageText("text.2689367b205c", "ok"), null);
    }

    // The menus that pause the game in Program.UpdateMenuPause. That pause saves and restores
    // the sim speed itself, so a replay does not open over it.
    internal static bool GameMenuOpen => EscMenu.Open || GameSettings.Show || GameSaves.IsOpen || VehicleSaves.IsOpen;

    private static (string Code, string? Message) StartBlock()
    {
        var ready = SceneBlock();
        if (ready.Message != null)
            return ready;
        if (HasUnsavedFlight)
            return ("unsaved_flight", UiText.MessageText("text.08fb058e8548", "Save the previous flight before starting another recording."));
        return Program.ControlledVehicle is { IsDisposed: false, Parent: Celestial } and not KittenEva
            ? (UiText.MessageText("text.2689367b205c", "ok"), null) : ("vehicle_required", UiText.MessageText("text.aaf08980cbae", "Select a vehicle in flight before starting a recording."));
    }

    private static void Require((string Code, string? Message) block)
    {
        if (block.Message != null)
            throw new Refusal(block.Code, block.Message);
    }

    private static ReplayScene NeedScene() => Scene ?? throw new Refusal("no_replay", UiText.MessageText("text.8f3cd85721ea", "Open a replay first."));

    private static string Text(string? value, string label) =>
        value?.Trim() is { Length: > 0 and <= 120 } text ? text : throw new Refusal("invalid_argument", UiText.Format("text.ec68c6a86320", $"Enter a valid {label}."));

    private static double Number(double? value, string label, double min, double max) =>
        value is double number && double.IsFinite(number) && number >= min && number <= max
            ? number : throw new Refusal("invalid_argument", UiText.Format("text.cabad4a6688d", $"Enter a {label} from {min:G} to {max:G}."));

    private static bool Flag(bool? value) => value ?? throw new Refusal("invalid_argument", UiText.MessageText("text.57d1fd5329b9", "Specify enabled or disabled."));

    /// <summary>The events of the recording for the take that plays, with their recording and take times, in the order of time.</summary>
    // The list is made once per plan, because the altitude, speed, ground, slow, q and maxq events scan the whole recording.
    public static IReadOnlyList<ReplayEventStatus> Events()
    {
        if (Scene is not { } scene)
            return [];
        ReplayTakePlan plan = scene.Player.Plan;
        if (ReferenceEquals(plan, _eventsPlan))
            return _events;
        IEnumerable<EventName> watched = plan.Take != null && plan.File != null ? EventDetector.Watched(plan.Take, plan.File.Events) : [];
        _events = ReplayEvents.ForTake(scene.Flight, watched, scene.World.BodyRadius, scene.World.GroundHeight)
            .Select(e => new ReplayEventStatus(e.Name, e.Detail is { } detail ? UiText.Concat(UiText.Concat(UiText.Concat(EventDisplay.Name(e.Name), " ("), EventName.TryParse(e.Name, out _, out _) ? StoredEventText.Describe(detail) : detail), ")") : EventDisplay.Name(e.Name), e.Time,
                plan.Take == null ? plan.TakeTime(e.Time) : plan.EventTimes.TryGetValue(e.Name, out double t) ? t : null))
            .ToArray();
        _eventsPlan = plan;
        return _events;
    }

    private static ReplayTakePlan? _eventsPlan;
    private static IReadOnlyList<ReplayEventStatus> _events = [];

    /// <summary>The markers of the recording that are no event, such as the separations, with their take times.</summary>
    // The list is made once per plan, because the timeline dock draws it on every frame.
    public static IReadOnlyList<ReplayMarkerStatus> Markers()
    {
        if (Scene is not { } scene)
            return [];
        ReplayTakePlan plan = scene.Player.Plan;
        if (ReferenceEquals(plan, _markersPlan))
            return _markers;
        _markers = scene.Flight.Markers.Where(m => m.Label.StartsWith("Separation", StringComparison.Ordinal) || m.Label.StartsWith("Breakup", StringComparison.Ordinal)
                || m.Label.StartsWith(ReplayEffectLog.MarkerLabel, StringComparison.Ordinal))
            .Select(m => new ReplayMarkerStatus(m.Label, m.Time, plan.TakeTime(m.Time))).ToArray();
        _markersPlan = plan;
        return _markers;
    }

    private static ReplayTakePlan? _markersPlan;
    private static IReadOnlyList<ReplayMarkerStatus> _markers = [];

    // The status names at most this many events and markers, so a long flight does not make a
    // response of megabytes. The counts say how many there are.
    private const int StatusListLimit = 1000;

    private static ReplayStatus Snapshot() => new(
        IsRecording ? "recording" : IsPlayingBack ? "replay" : HasUnsavedFlight ? "save_pending" : "idle",
        Available, ReplayId, Flight?.FormatVersion ?? 0, SelectedVehicleName, RecordingSeconds, TakeName,
        TakeProblem.Length > 0 ? TakeProblem : null, CameraMode == ReplayCameraMode.Take ? "take" : "free",
        Time, Duration, RecordingTime, RecordingDuration, Speed, (Plan?.RecordingRate(Time) ?? 0) * Speed, IsPlaying,
        Frame?.ShotIndex ?? -1, Program.DrawUI, FollowActorId, FieldOfView, StartBlockReason, OpenBlockReason, Status,
        ListFiles().Select(path => Path.GetFileNameWithoutExtension(path)!).ToArray(),
        Events().Take(StatusListLimit).ToArray(), Markers().Take(StatusListLimit).ToArray(),
        Flight?.Actors.Select(actor => new ReplayActorStatus(actor.Id, actor.Name, CanFollow(actor.Id))).ToArray() ?? [],
        CanHideInterface, OfflineHooks.Available, RenderStatus, Events().Count, Markers().Count,
        CineCamWindow.EditorSession.Document?.Takes.Select(t => t.Name).ToArray() ?? [], CineCamWindow.EditorSession.Take?.Name,
        CineCamWindow.EditorSession.Path, CineCamWindow.EditorSession.Dirty,
        RecordingBuild, VersionInfo.Current.VersionString, OpenWarning.Length > 0 ? OpenWarning : null);

    private static void Complete(Action<ReplayResult>? completion, bool ok, string code, string message, string? previewPath = null)
    {
        if (!ok)
            Status = message;
        if (completion == null)
            return;
        try
        {
            completion(new ReplayResult(ok, code, message, Snapshot(), previewPath));
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Warning($"[CineCam] Replay response could not be delivered: {ex.Message}");
        }
    }

    private static void CancelRequests()
    {
        while (Requests.TryDequeue(out Request? request))
            Complete(request.Completion, false, "world_changed", UiText.MessageText("text.97b3ee074f50", "The game scene changed before the command ran."));
    }
}
