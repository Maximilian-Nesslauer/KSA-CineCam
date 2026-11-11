using System.Text.Json;
using Brutal.Logging;
using CineCam.Core;
using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Editing;
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
    public static void Open(string path) => Submit(new(ReplayOperation.Open, Id: Path.GetFileNameWithoutExtension(path)));
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
            Complete(completion, false, "busy", "The replay command queue is full.");
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
                    throw new Refusal("busy", "Wait for the preview frame to finish.");
                if (_render != null && request.Command.Operation is not
                    (ReplayOperation.Status or ReplayOperation.List or ReplayOperation.Close or ReplayOperation.RenderStop))
                    throw new Refusal("rendering", "A render runs. Stop it first with Escape or the start and stop key.");
                if (request.Command.Operation == ReplayOperation.Preview)
                {
                    BeginPreview(request.Completion);
                    continue;
                }
                string message = Execute(request.Command);
                if (request.Command.Operation is not (ReplayOperation.Status or ReplayOperation.List))
                    Status = message;
                Complete(request.Completion, true, "ok", message);
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
                    throw new Refusal("not_recording", "There is no active flight recording.");
                FinishRecording();
                return Status;
            case ReplayOperation.RetrySave:
                if (!HasUnsavedFlight)
                    throw new Refusal("no_unsaved_flight", "There is no flight waiting to be saved.");
                SaveRecording();
                return Status;
            case ReplayOperation.Open:
                OpenCore(Text(command.Id, "recording ID"));
                return Status;
            case ReplayOperation.Close:
                NeedScene();
                CloseCore();
                return Status;
            case ReplayOperation.SetInterface:
                bool show = Flag(command.Enabled);
                if (!show && !CanHideInterface)
                    throw new Refusal("hotkeys_unavailable", "No key can show the interface again. Set a window key on the Settings page, or the key patch is not available on this game build.");
                if (!show)
                    NeedScene();
                Program.DrawUI = show;
                return Program.DrawUI ? "Interface shown." : "Interface hidden.";
            case ReplayOperation.Unknown:
                throw new Refusal("unknown_operation", "Choose a supported CineCam operation.");
        }

        ReplayScene scene = NeedScene();
        switch (command.Operation)
        {
            case ReplayOperation.Seek:
                _scrubTarget = null;
                scene.Seek(Number(command.Time, "take time", 0, scene.Duration));
                return "";
            case ReplayOperation.SetPlaying:
                scene.SetPlaying(Flag(command.Enabled));
                return IsPlaying ? "Replay playing." : "Replay paused.";
            case ReplayOperation.SetSpeed:
                scene.SetPreviewSpeed(Number(command.Speed, "preview speed", ReplayTakePlayer.MinPreviewSpeed, ReplayTakePlayer.MaxPreviewSpeed));
                return "Preview speed set.";
            case ReplayOperation.SetCamera:
                ReplayCameraMode mode = command.Name?.Trim().ToLowerInvariant() switch
                {
                    "take" => ReplayCameraMode.Take,
                    "free" => ReplayCameraMode.Free,
                    _ => throw new Refusal("invalid_argument", "Choose the camera \"take\" or \"free\"."),
                };
                scene.SetCameraMode(mode);
                return mode == ReplayCameraMode.Take ? "The camera follows the take." : "The camera is free.";
            case ReplayOperation.Follow:
                if (command.ActorId is int id && !scene.CanFollow(id))
                    throw new Refusal("actor_unavailable", "This vehicle is unavailable at the current replay time.");
                scene.Follow(command.ActorId);
                return command.ActorId == null ? "Free camera selected." : "The free camera follows the vehicle.";
            case ReplayOperation.SetFov:
                scene.SetFov(Number(command.Fov, "field of view", 1, MaximumFieldOfView));
                return "Camera field of view set.";
            case ReplayOperation.Render:
                return BeginRender(scene, RenderRange(command.Name));
            case ReplayOperation.RenderStop:
                return RequestRenderStop();
            case ReplayOperation.AddKey:
                string kindName = Text(command.Name, "kind of shot");
                KeyKind kind = Enum.GetValues<KeyKind>().FirstOrDefault(k => string.Equals(k.ToString(), kindName.Replace("-", ""), StringComparison.OrdinalIgnoreCase), (KeyKind)(-1));
                if (!Enum.IsDefined(kind))
                    throw new Refusal("invalid_argument", "Choose a kind: fixed, track, chase, orbit, part-mount or path-point.");
                if (!KeyCapture.AddKey(CineCamWindow.EditorSession, kind, out string added))
                    throw new Refusal("key_failed", added);
                return added;
            default:
                throw new Refusal("unknown_operation", "Choose a supported CineCam operation.");
        }
    }

    // A replay takes the camera, the sim speed and the vehicles, so it opens only when no take runs.
    private static (string Code, string? Message) ReadyBlock() =>
        SceneBlock() is { Message: not null } block ? block
            : TakeRunner.IsRunning ? ("busy", "Finish the active CineCam operation first.") : ("ok", null);

    // A recording only reads the flight, so a take or a preview may run during it. A reset loads a
    // save and the move of the time of day jumps the clock, and both would end it.
    private static (string Code, string? Message) SceneBlock()
    {
        if (!Available)
            return ("unavailable", "Replay is unavailable on this game build.");
        if (IsBusy || TakeRunner.Reset.IsActive || SunWarp.IsRunning)
            return ("busy", "Finish the active CineCam operation first.");
        if (!Program.IsLoaded || Program.EditorFlag || Program.Editor != null || Universe.CurrentSystem == null)
            return ("flight_required", "Open a flight scene to use CineCam replay.");
        if (Universe.IsAutoWarpActive)
            return ("auto_warp_active", "Stop auto warp before using CineCam replay.");
        if (GameMenuOpen)
            return ("menu_open", "Close the game menu before using CineCam replay.");
        return ("ok", null);
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
            return ("unsaved_flight", "Save the previous flight before starting another recording.");
        return Program.ControlledVehicle is { IsDisposed: false, Parent: Celestial } and not KittenEva
            ? ("ok", null) : ("vehicle_required", "Select a vehicle in flight before starting a recording.");
    }

    private static void Require((string Code, string? Message) block)
    {
        if (block.Message != null)
            throw new Refusal(block.Code, block.Message);
    }

    private static ReplayScene NeedScene() => Scene ?? throw new Refusal("no_replay", "Open a replay first.");

    private static string Text(string? value, string label) =>
        value?.Trim() is { Length: > 0 and <= 120 } text ? text : throw new Refusal("invalid_argument", $"Enter a valid {label}.");

    private static double Number(double? value, string label, double min, double max) =>
        value is double number && double.IsFinite(number) && number >= min && number <= max
            ? number : throw new Refusal("invalid_argument", $"Enter a {label} from {min:G} to {max:G}.");

    private static bool Flag(bool? value) => value ?? throw new Refusal("invalid_argument", "Specify enabled or disabled.");

    /// <summary>The events of the recording for the take that plays, with their recording and take times, in the order of time.</summary>
    // The list is made once per plan, because the altitude and speed events scan the whole recording.
    public static IReadOnlyList<ReplayEventStatus> Events()
    {
        if (Scene is not { } scene)
            return [];
        ReplayTakePlan plan = scene.Player.Plan;
        if (ReferenceEquals(plan, _eventsPlan))
            return _events;
        IEnumerable<EventName> watched = plan.Take != null && plan.File != null ? EventDetector.Watched(plan.Take, plan.File.Events) : [];
        _events = ReplayEvents.ForTake(scene.Flight, watched, scene.World.BodyRadius)
            .Select(e => new ReplayEventStatus(e.Name, EventDisplay.Name(e.Name), e.Time,
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
        _markers = scene.Flight.Markers.Where(m => m.Label.StartsWith("Separation", StringComparison.Ordinal) || m.Label.StartsWith("Breakup", StringComparison.Ordinal))
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
        CanHideInterface, OfflineHooks.Available, RenderStatus, Events().Count, Markers().Count);

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
            Complete(request.Completion, false, "world_changed", "The game scene changed before the command ran.");
    }
}
