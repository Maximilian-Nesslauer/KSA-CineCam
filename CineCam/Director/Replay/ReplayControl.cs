namespace CineCam.Director.Replay;

public enum ReplayOperation
{
    Unknown, Status, List, StartRecording, StopRecording, RetrySave, Open, Close, Seek, SetPlaying, SetSpeed,
    SetCamera, Follow, SetFov, AddKey, SetInterface, Preview, Render, RenderStop, Reload, SelectTake, Quit
}

/// <summary>
/// Times are take seconds. <see cref="Name"/> is the camera ("take" or "free") for SetCamera, the kind of
/// shot for AddKey, the range ("whole" or "current") for Render and the take for SelectTake.
/// <see cref="Discard"/> lets Reload drop the unsaved changes of the editor.
/// <see cref="Enabled"/> true for Open
/// confirms the open of a recording of an older game build.
/// </summary>
public sealed record ReplayCommand(ReplayOperation Operation, string? Id = null, int? ActorId = null,
    double? Time = null, double? Speed = null, bool? Enabled = null, string? Name = null, double? Fov = null,
    bool? Discard = null);

/// <summary>What the game writes when a quit request comes, in the order of the checks.</summary>
public readonly record struct QuitState(bool Rendering, bool PictureWrite, bool Recording, bool UnsavedFlight,
    bool TakeRuns, bool ResetRuns);

public static class ControlRules
{
    public static string? TakeName(string? name) => string.IsNullOrWhiteSpace(name) ? null : name;

    /// <summary>
    /// The code and the message of the refusal of a quit, or null when the game can end now. A quit ends
    /// nothing that writes files, so a render, an offline take, a recording, a flight that is not saved yet,
    /// a take with its event log and a reset that loads a save all refuse it.
    /// </summary>
    public static (string Code, string Message)? QuitBlock(QuitState state) => state switch
    {
        { Rendering: true } => ("rendering", UiText.MessageText("text.4553aacd9b1f", "A render writes pictures. Stop it first with render-stop.")),
        { PictureWrite: true } => ("capture_busy", UiText.MessageText("capture.pending_picture", "A picture is being captured or saved. Try again in a moment.")),
        { Recording: true } => ("recording", UiText.MessageText("text.bff0c5e1ebe2", "A flight recording runs. Stop it first with stop, so the flight is saved.")),
        { UnsavedFlight: true } => ("unsaved_flight", UiText.MessageText("text.b077aa2674a7", "A recorded flight is not saved yet. Save it with retry-save first.")),
        { TakeRuns: true } => ("busy", UiText.MessageText("text.7b73186bc865", "A take runs. Stop it first, so its event log and its pictures are complete.")),
        { ResetRuns: true } => ("busy", UiText.MessageText("text.70f20622b31b", "A reset loads the checkpoint. Wait until it is done.")),
        _ => null,
    };
}

public sealed record ReplayResult(bool Ok, string Code, string Message, ReplayStatus? State = null,
    string? PreviewPath = null);

/// <summary>
/// <see cref="Time"/> and <see cref="Duration"/> are take seconds, <see cref="RecordingTime"/> is recording
/// seconds. <see cref="Events"/> and <see cref="Markers"/> hold at most the first 1000 items, and
/// <see cref="EventCount"/> and <see cref="MarkerCount"/> give the whole number. <see cref="RecordingBuild"/> is the
/// game build of the open recording, and <see cref="Warning"/> says what the replay shows differently from the flight.
/// </summary>
public sealed record ReplayStatus(string Mode, bool Available, string? ReplayId, int FormatVersion, string? SelectedVehicle,
    double RecordingSeconds, string? Take, string? TakeProblem, string Camera, double Time, double Duration,
    double RecordingTime, double RecordingDuration, double PreviewSpeed, double PlaybackRate, bool IsPlaying, int ShotIndex,
    bool InterfaceVisible, int? ActorId, double FieldOfView, string? StartBlockReason, string? OpenBlockReason, string Message,
    IReadOnlyList<string> Recordings, IReadOnlyList<ReplayEventStatus> Events, IReadOnlyList<ReplayMarkerStatus> Markers,
    IReadOnlyList<ReplayActorStatus> Actors, bool CanHideInterface, bool CanPreview, ReplayRenderStatus? Render = null,
    int EventCount = 0, int MarkerCount = 0, IReadOnlyList<string>? Takes = null, string? SelectedTake = null,
    string? ShotFile = null, bool UnsavedChanges = false,
    string? RecordingBuild = null, string? GameBuild = null, string? Warning = null);

/// <summary>The take time is null when the take ends before the event.</summary>
public sealed record ReplayEventStatus(string Name, string Label, double RecordingTime, double? TakeTime);

public sealed record ReplayMarkerStatus(string Label, double RecordingTime, double TakeTime);

public sealed record ReplayActorStatus(int Id, string Name, bool Available);

/// <summary>
/// <see cref="Start"/> and <see cref="End"/> are take seconds. <see cref="Result"/> is null while the render
/// runs and says how it ended after that.
/// </summary>
public sealed record ReplayRenderStatus(bool Active, int Pictures, int Total, double Percent, double ElapsedSeconds,
    double? SecondsLeft, double Fps, double Start, double End, string Folder, string? Result);
