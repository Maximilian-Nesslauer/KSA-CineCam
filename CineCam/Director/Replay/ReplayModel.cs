using System.Text.Json.Serialization;

namespace CineCam.Director.Replay;

public sealed record ReplayFlight(string GameVersion, string SystemId, string BodyId, double StartTime,
    List<ReplayActor> Actors, List<ReplayMarker> Markers)
{
    public List<ReplayEvent> Events { get; init; } = new();

    /// <summary>The camera views of a file of version 1, read only for the import into a take.</summary>
    public List<ReplayView> Views { get; init; } = new();

    /// <summary>
    /// The controlled vehicle can change at an undocking, a docking or a breakup. Null or empty means the
    /// first actor for the whole flight.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ReplayFollow>? Follows { get; init; }

    [JsonIgnore]
    public int FormatVersion { get; init; } = ReplayFile.CurrentVersion;
}

public sealed record ReplayFollow(double Time, int ActorId);

public sealed record ReplayActor(int Id, string Name, List<ReplaySegment> Segments)
{
    /// <summary>
    /// True for a vehicle near the followed one that did not split from it, such as a docking target, so the
    /// director does not take it as a separated vehicle.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Neighbor { get; init; }

    /// <summary>Recording time when the vehicle left the recording, or null when it stayed to the end.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? End { get; init; }
}

public sealed record ReplaySegment(double Start, string Definition, List<ReplaySample> Samples)
{
    /// <summary>
    /// In the assembly frame at the start of the segment, or null in a file of version 1. The vehicle frame
    /// of the director has its axis through this point.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public V3? CenterOfMass { get; init; }

    /// <summary>
    /// The channels follow the order of the modules in the part tree, so the replay checks the kinds
    /// against the decoded tree.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ReplayChannel>? Channels { get; init; }

    /// <summary>When it is null, the replay checks only the kinds of the channels.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? PartKey { get; init; }
}

// Poses use the body-fixed frame and the vehicle model origin. Times are relative simulation seconds.
// The state is stored only for the vehicle that the recorder followed at the time of the sample.
public sealed record ReplaySample(double Time, V3 Position, ReplayRotation Rotation, ReplayNozzle[] Nozzles,
    float AmbientPressure = 0, float AirDensity = 0, V3 AirVelocity = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplayState? State = null);

/// <summary>
/// What the event detector needs and the pose cannot give. <see cref="Altitude"/> is barometric,
/// <see cref="Speed"/> is the surface speed and <see cref="Radial"/> the speed away from the body center.
/// </summary>
public readonly record struct ReplayState(int Flags, int Sequence, int Docked, float Altitude, float Speed, float Radial)
{
    public bool Has(int flag) => (Flags & flag) != 0;
}

public static class ReplayStateFlags
{
    public const int EngineOn = 1;
    public const int Contact = 2;
    public const int StableOrbit = 4;
    public const int OutsideAtmosphere = 8;
}

public readonly record struct ReplayRotation(double X, double Y, double Z, double W)
{
    public static ReplayRotation Interpolate(ReplayRotation from, ReplayRotation to, double t) => ReplayTimeline.InterpolateRotation(from, to, t);

    /// <summary>Computes q v q*, the same as double3.Transform with a doubleQuat, and needs a unit quaternion.</summary>
    public V3 Rotate(V3 v)
    {
        var u = new V3(X, Y, Z);
        V3 t = V3.Cross(u, v) * 2.0;
        return v + t * W + V3.Cross(u, t);
    }
}

public readonly record struct ReplayNozzle(float DutyCycle, float ExhaustVelocity, float ExhaustPressure, float Temperature,
    float Gamma, float SpecificGasConstant, float ChamberPressure, float ChamberTemperature, float ExitPressure,
    float ExitTemperature, V3 Position, V3 Axis, V3 RestPosition, V3 RestAxis);

public sealed record ReplayMarker(double Time, string Label);

/// <summary><see cref="Name"/> is canonical (see <see cref="EventName.Canonical"/>), and the time is recording time.</summary>
public sealed record ReplayEvent(double Time, string Name);

// A camera view of a recording of version 1. A mount makes the pose part-local, otherwise an actor ID
// makes it vehicle-local.
public sealed record ReplayView(string Name, double Time, V3 Position, ReplayRotation Rotation, double Fov,
    int? ActorId = null, ReplayMount? Mount = null, string? Id = null, bool Cut = false)
{
    internal static bool SupportsPosition(V3 position, int? actorId) =>
        position.Length <= (actorId.HasValue ? 1e7 : 1e16);
}

public sealed record ReplayMount(double SegmentStart, int PartIndex);
