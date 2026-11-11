using System.Text.Json;

namespace CineCam.Director;

public enum ShotType { Orbit, Crane, Tripod, Track, Chase, Part, Path }

public enum Ease { None, In, Out, InOut }

public enum ShotTarget { Site, Vehicle }

/// <summary>
/// <see cref="Vehicle"/> has +X toward the nose. <see cref="Local"/> is the local horizon frame at the
/// vehicle or the part, with +X away from the body centre, +Y east and +Z north. <see cref="Flight"/>
/// is the local frame turned about +X so that +Z points along the horizontal direction of flight.
/// </summary>
public enum ShotFrame { Vehicle, Local, Flight }

/// <summary>
/// <see cref="Vehicle"/> keeps the up of the frame of the shot. <see cref="Horizon"/> uses the
/// direction away from the body centre at the camera.
/// </summary>
public enum ShotUp { Vehicle, Horizon }

public enum ShotAim { Look, Vehicle }

/// <summary><see cref="Separated"/> is the controlled vehicle until a decoupler splits a vehicle off during the take.</summary>
public enum ShotSubject { Controlled, Separated }

public enum SeparatedPick { Newest, Nearest }

public enum TransitionKind { Cut, Blend }

public enum TriggerKind { After, Time, Event }

public sealed class Trigger
{
    public TriggerKind Kind { get; set; } = TriggerKind.After;

    /// <summary>Take time in seconds, for <see cref="TriggerKind.Time"/>.</summary>
    public double Time { get; set; }

    public string Event { get; set; } = "";

    /// <summary>Seconds after the event. A negative value needs a predictable event.</summary>
    public double Delay { get; set; }

    public static Trigger After() => new();

    public static Trigger AtTime(double seconds) => new() { Kind = TriggerKind.Time, Time = seconds };

    public static Trigger OnEvent(string name, double delay = 0.0) =>
        new() { Kind = TriggerKind.Event, Event = name, Delay = delay };
}

public sealed class Transition
{
    public TransitionKind Kind { get; set; } = TransitionKind.Cut;

    /// <summary>The length of a blend that the file asks for without a length.</summary>
    public const double DefaultSeconds = 3.0;

    /// <summary>Length of the blend in seconds. It is 0 for a cut.</summary>
    public double Seconds { get; set; }
}

/// <summary>
/// One keyframe of a path shot. <see cref="T"/> is the share of the path from 0 to 1, and the points
/// are in the frame of the shot.
/// </summary>
public sealed class PathPoint
{
    public double T { get; set; }
    public V3 Position { get; set; }
    public V3 LookAt { get; set; }
    public double? Fov { get; set; }
    public double? Roll { get; set; }

    public PathPoint Clone() => new() { T = T, Position = Position, LookAt = LookAt, Fov = Fov, Roll = Roll };
}

/// <summary>
/// Positions are in the frame of the target, the launch site frame (+X up, +Y east, +Z north) or the
/// vehicle frame (+X toward the nose). Angles are in degrees, lengths in meters.
/// </summary>
public sealed class Shot
{
    public ShotType Type { get; set; }
    public double Duration { get; set; }
    public Ease Ease { get; set; } = Ease.None;

    /// <summary>Vertical field of view in degrees.</summary>
    public double Fov { get; set; } = 40.0;

    /// <summary>Turn of the camera about its view direction in degrees. Zero keeps the horizon level.</summary>
    public double Roll { get; set; }

    public ShotTarget Target { get; set; }

    public ShotFrame Frame { get; set; } = ShotFrame.Vehicle;

    /// <summary>Null when the file leaves it to the default.</summary>
    public ShotUp? Up { get; set; }

    public ShotUp EffectiveUp => Up ?? (UsesHorizonFrame ? ShotUp.Horizon : ShotUp.Vehicle);

    // Only these shots read Frame, and the others ignore it.
    private bool TakesFrame => Type is ShotType.Chase or ShotType.Part || (Type == ShotType.Path && Target == ShotTarget.Vehicle);

    public bool UsesLocalFrame => Frame == ShotFrame.Local && TakesFrame;

    public bool UsesFlightFrame => Frame == ShotFrame.Flight && TakesFrame;

    public bool UsesHorizonFrame => UsesLocalFrame || UsesFlightFrame;

    public Transition Transition { get; set; } = new();
    public Trigger At { get; set; } = new();

    // Orbit. The camera turns around the +X axis of the frame, 0 degrees is +Z and 90 is +Y.
    public V3 Center { get; set; }
    public double Radius { get; set; }
    public double Height { get; set; }
    public double StartDeg { get; set; }
    public double DegPerSec { get; set; }

    // Orbit, tripod, chase and part. For orbit and tripod this is a point in the target frame,
    // for chase it is a point in the vehicle frame and for part it is relative to the part.
    public V3 LookAt { get; set; }

    // Crane. Heights are along the +X axis of the frame, Azimuth is in degrees as for the orbit.
    public double From { get; set; }
    public double To { get; set; }
    public double Azimuth { get; set; }

    // Tripod and track.
    public V3 Position { get; set; }

    // Track. When set, the field of view follows the distance so the vehicle keeps its size.
    public bool KeepSize { get; set; }

    // Path. The keyframes are in the order of their T.
    public List<PathPoint> Points { get; } = new();

    /// <summary>The share of the picture height that the whole vehicle fills, or null for off.</summary>
    public double? Fit { get; set; }

    // Chase and part.
    public V3 Offset { get; set; }

    public ShotAim Aim { get; set; } = ShotAim.Look;

    /// <summary>In the frame of the shot.</summary>
    public V3 AimOffset { get; set; }

    /// <summary>The smallest angle in degrees between the view direction and the sun, or null for off.</summary>
    public double? AvoidSun { get; set; }

    public ShotSubject Subject { get; set; } = ShotSubject.Controlled;

    public SeparatedPick Pick { get; set; } = SeparatedPick.Newest;

    /// <summary>Also the shot types that can take a separated vehicle as their subject.</summary>
    public static bool SupportsAim(ShotType type) => type is ShotType.Chase or ShotType.Part or ShotType.Path or ShotType.Track;

    /// <summary>Seconds of turn smoothing for chase and part shots.</summary>
    public double Smoothing { get; set; } = 0.5;

    /// <summary>Part id for a part shot. An empty id means the whole vehicle.</summary>
    public string Part { get; set; } = "";

    /// <summary>Fields of the file that this version does not know. They are written back.</summary>
    public Dictionary<string, JsonElement> Extras { get; } = new();

    public static ShotTarget DefaultTarget(ShotType type) =>
        type is ShotType.Orbit or ShotType.Tripod or ShotType.Path ? ShotTarget.Site : ShotTarget.Vehicle;
}

/// <summary>A change of the simulation speed. The value ramps from the speed at the start.</summary>
public sealed class SpeedKey
{
    public Trigger At { get; set; } = new();
    public double To { get; set; } = 1.0;
    public double Ramp { get; set; }
    public Ease Ease { get; set; } = Ease.InOut;
    public Dictionary<string, JsonElement> Extras { get; } = new();
}

public enum ActionKind { Stage }

/// <summary>
/// An action runs once per run. It always has a trigger of its own, because "after the one before"
/// has no meaning for something that changes the flight.
/// </summary>
public sealed class TakeAction
{
    public Trigger At { get; set; } = Trigger.AtTime(0.0);

    public ActionKind Kind { get; set; } = ActionKind.Stage;

    /// <summary>
    /// The sequence that must be the next one when the action fires, or null for any. The staging key
    /// cannot pick a number, so a wrong number is skipped and logged instead of staging too early.
    /// </summary>
    public int? Sequence { get; set; }

    public Dictionary<string, JsonElement> Extras { get; } = new();
}

/// <summary>A box in the site frame that the camera is pushed out of. <see cref="Min"/> is at most <see cref="Max"/> on every axis.</summary>
public sealed class KeepOutBox
{
    public string Name { get; set; } = "";
    public V3 Min { get; set; }
    public V3 Max { get; set; }

    public KeepOutBox Clone() => new() { Name = Name, Min = Min, Max = Max };

    public void Normalize()
    {
        V3 lo = new(Math.Min(Min.X, Max.X), Math.Min(Min.Y, Max.Y), Math.Min(Min.Z, Max.Z));
        V3 hi = new(Math.Max(Min.X, Max.X), Math.Max(Min.Y, Max.Y), Math.Max(Min.Z, Max.Z));
        Min = lo;
        Max = hi;
    }
}

/// <summary>
/// The shots before the first shot that waits for <see cref="Until"/> repeat as a loop until the event
/// comes. Then the waiting shot starts through its own transition.
/// </summary>
public sealed class PreRoll
{
    public string Until { get; set; } = "";

    public bool Loop { get; set; } = true;

    public PreRoll Clone() => new() { Until = Until, Loop = Loop };
}

public sealed class Take
{
    public string Name { get; set; } = "default";
    public bool IsDefault { get; set; }

    public string? Site { get; set; }

    /// <summary>The origin of the site frame when no Landmark is named, because a Landmark wins over it.</summary>
    public WorldAnchor? Anchor { get; set; }

    /// <summary>Null to use the value of the file.</summary>
    public string? HideUiAt { get; set; }

    public const double DefaultAutoStartDelay = 2.0;

    /// <summary>The name of the game save that a reset loads, valid by <see cref="CheckpointName"/>.</summary>
    public string? Checkpoint { get; set; }

    public bool AutoStart { get; set; }

    /// <summary>Seconds between the end of a reset and the automatic take start.</summary>
    public double AutoStartDelay { get; set; } = DefaultAutoStartDelay;

    /// <summary>Null to use the boxes of the file. An empty list turns them off for the take.</summary>
    public List<KeepOutBox>? KeepOut { get; set; }

    public PreRoll? PreRoll { get; set; }

    public List<Shot> Shots { get; } = new();
    public List<SpeedKey> Speed { get; } = new();
    public List<TakeAction> Actions { get; } = new();
    public Dictionary<string, JsonElement> Extras { get; } = new();
}

public enum CaptureMode
{
    Realtime,
    Offline,
}

/// <summary>In the offline mode the game steps by a fixed time per picture and saves every picture as a numbered file.</summary>
public sealed class CaptureOptions
{
    public CaptureMode Mode { get; set; } = CaptureMode.Realtime;

    /// <summary>Null to use the fps of the file.</summary>
    public double? Fps { get; set; }

    /// <summary>Relative to the take folder, and null for the take folder itself.</summary>
    public string? Folder { get; set; }

    /// <summary>Only 1 works, because the swapchain grab has the size of the game window.</summary>
    public int Scale { get; set; } = 1;

    public Dictionary<string, JsonElement> Extras { get; } = new();

    /// <summary>True when the writer can leave the object out.</summary>
    public bool IsDefault =>
        Mode == CaptureMode.Realtime && Fps == null && Folder == null && Scale == 1 && Extras.Count == 0;
}

public sealed class ShotFile
{
    public const int CurrentFormatVersion = 2;

    /// <summary>
    /// A file without the field is version 1. The writer writes <see cref="CurrentFormatVersion"/>, or
    /// the version of the file when that is higher.
    /// </summary>
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public string? Site { get; set; }
    public double Fps { get; set; } = 60.0;

    public bool HideUi { get; set; }

    /// <summary>
    /// The event at which <see cref="HideUi"/> hides the interface, or null or "start" for the take
    /// start. The interface stays usable until then.
    /// </summary>
    public string? HideUiAt { get; set; }

    public static bool IsHideUiAtStart(string? value) =>
        value == null || value.Trim().Equals("start", StringComparison.OrdinalIgnoreCase);

    public string? HideUiAtFor(Take take) => take.HideUiAt ?? HideUiAt;

    /// <summary>True when the first frame of a take is a full white frame, as a mark for the cut.</summary>
    public bool SyncFlash { get; set; }

    public CaptureOptions Capture { get; set; } = new();

    public double CaptureFps => Capture.Fps ?? Fps;

    /// <summary>Aliases. Each value is a canonical event name such as "sequence:3".</summary>
    public Dictionary<string, string> Events { get; } = new(StringComparer.Ordinal);

    public List<KeepOutBox> KeepOut { get; } = new();

    public IReadOnlyList<KeepOutBox> KeepOutFor(Take take) => take.KeepOut ?? KeepOut;

    public List<Take> Takes { get; } = new();

    /// <summary>
    /// True when the file lists its takes under "takes". A file with one take may instead keep
    /// "camera" and "speed" at the top level, and is written back in that form.
    /// </summary>
    public bool UsesTakeList { get; set; }

    public Dictionary<string, JsonElement> Extras { get; } = new();

    public Take DefaultTake =>
        Takes.FirstOrDefault(t => t.IsDefault) ?? Takes[0];

    public Take? FindTake(string name) =>
        Takes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed record ShotFileReadResult(ShotFile File, IReadOnlyList<string> Warnings);

/// <summary>Raised for a shot file that cannot be used. <see cref="FieldPath"/> names the bad field.</summary>
public sealed class ShotFileException : Exception
{
    public string FieldPath { get; }

    public ShotFileException(string fieldPath, string message)
        : base(fieldPath.Length == 0 ? message : $"{fieldPath}: {message}")
    {
        FieldPath = fieldPath;
    }
}
