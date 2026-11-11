namespace CineCam.Director.Replay;

public enum ReplayOperation
{
    Unknown, Status, List, StartRecording, StopRecording, RetrySave, Open, Close, Seek, SetPlaying, SetSpeed,
    SetCamera, Follow, SetFov, AddKey, SetInterface, Preview, Render, RenderStop
}

/// <summary>
/// Times are take seconds. <see cref="Name"/> is the camera ("take" or "free") for SetCamera, the kind of
/// shot for AddKey, and the range ("whole" or "current") for Render.
/// </summary>
public sealed record ReplayCommand(ReplayOperation Operation, string? Id = null, int? ActorId = null,
    double? Time = null, double? Speed = null, bool? Enabled = null, string? Name = null, double? Fov = null);

public sealed record ReplayResult(bool Ok, string Code, string Message, ReplayStatus? State = null,
    string? PreviewPath = null);

/// <summary>
/// <see cref="Time"/> and <see cref="Duration"/> are take seconds, <see cref="RecordingTime"/> is recording
/// seconds. <see cref="Events"/> and <see cref="Markers"/> hold at most the first 1000 items, and
/// <see cref="EventCount"/> and <see cref="MarkerCount"/> give the whole number.
/// </summary>
public sealed record ReplayStatus(string Mode, bool Available, string? ReplayId, int FormatVersion, string? SelectedVehicle,
    double RecordingSeconds, string? Take, string? TakeProblem, string Camera, double Time, double Duration,
    double RecordingTime, double RecordingDuration, double PreviewSpeed, double PlaybackRate, bool IsPlaying, int ShotIndex,
    bool InterfaceVisible, int? ActorId, double FieldOfView, string? StartBlockReason, string? OpenBlockReason, string Message,
    IReadOnlyList<string> Recordings, IReadOnlyList<ReplayEventStatus> Events, IReadOnlyList<ReplayMarkerStatus> Markers,
    IReadOnlyList<ReplayActorStatus> Actors, bool CanHideInterface, bool CanPreview, ReplayRenderStatus? Render = null,
    int EventCount = 0, int MarkerCount = 0);

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
