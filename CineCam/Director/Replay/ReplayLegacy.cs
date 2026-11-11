using System.Text.Json;
using CineCam.Editing;

namespace CineCam.Director.Replay;

/// <summary>A speed key of a legacy replay project, which sets the rate from its recording time on.</summary>
public sealed record ReplaySpeedKey(double Time, double Speed);

public sealed record ReplayLegacyProject(string Name, List<ReplayView> Views, List<ReplaySpeedKey> SpeedKeys);

/// <summary>
/// The pose is in the body-fixed frame. <see cref="OnVehicle"/> is true for a view that moved with the
/// first vehicle of the flight.
/// </summary>
public readonly record struct LegacyCamera(double Time, V3 PositionCcf, V3 ForwardCcf, double Fov, bool OnVehicle, bool Cut);

/// <summary>
/// Turns the views of legacy replay projects into a take. That format held each view until the next one
/// and moved the camera on a straight line between two views of the same anchor unless the later one was a cut.
/// </summary>
public static class ReplayLegacy
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private const int ItemLimit = 512;
    private const string MainProject = "main";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16,
    };

    private sealed record Document(int Version, Project? Project);

    private sealed record Project(string? RecordingId, List<ReplayView>? Views, List<ReplaySpeedKey>? SpeedKeys);

    /// <summary>
    /// The views inside a recording of version 1 count as the project "main" unless a project file of that
    /// name replaces them. A file that cannot be read is left out with a note.
    /// </summary>
    public static List<ReplayLegacyProject> Read(ReplayFlight flight, string projectRoot, string recordingId, List<string> notes)
    {
        var projects = new List<ReplayLegacyProject>();
        string folder = Path.Combine(projectRoot, recordingId);
        bool mainFile = false;
        if (IsFileName(recordingId) && Directory.Exists(folder))
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase).Take(64))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                try
                {
                    if (new FileInfo(path).Length > MaxBytes)
                        throw new InvalidDataException("the file is too large");
                    Document? document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Json);
                    if (document is not { Version: 1, Project: { } project })
                        throw new InvalidDataException("the version is not supported");
                    if (string.Equals(name, MainProject, StringComparison.OrdinalIgnoreCase))
                        mainFile = true;
                    projects.Add(new ReplayLegacyProject(name,
                        (project.Views ?? []).Where(v => v != null).Take(ItemLimit).ToList(),
                        (project.SpeedKeys ?? []).Where(k => k != null).Take(ItemLimit).ToList()));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    notes.Add($"The old replay project \"{name}\" could not be read: {ex.Message}");
                }
            }
        }

        if (!mainFile && flight.Views.Count > 0)
            projects.Insert(0, new ReplayLegacyProject(MainProject, flight.Views.Take(ItemLimit).ToList(), []));
        return projects.Where(p => p.Views.Count > 0 || p.SpeedKeys.Count > 0).ToList();
    }

    /// <summary>The rate is 1 before the first key and changes at each key without a ramp.</summary>
    public static double TakeTimeOf(double recordingTime, IReadOnlyList<ReplaySpeedKey> keys)
    {
        double take = 0.0, at = 0.0, rate = 1.0;
        foreach (ReplaySpeedKey key in keys.OrderBy(k => k.Time))
        {
            if (key.Time >= recordingTime)
                break;
            take += (Math.Max(key.Time, 0.0) - at) / rate;
            at = Math.Max(key.Time, 0.0);
            rate = Math.Clamp(key.Speed, 0.1, 4.0);
        }

        return take + (recordingTime - at) / rate;
    }

    /// <summary>
    /// A view whose next view has the same anchor and is no cut becomes a path shot to the next view, the
    /// others a tripod shot. A view on the first vehicle stays in the vehicle frame, any other view in the site frame.
    /// </summary>
    public static Take ToTake(string name, IReadOnlyList<LegacyCamera> cameras, IReadOnlyList<ReplaySpeedKey> keys,
        ReplayWorld world, ReplaySite site)
    {
        var take = new Take { Name = name };
        var sorted = cameras.OrderBy(c => c.Time).ToList();
        double end = TakeTimeOf(world.Duration, keys);
        for (int i = 0; i < sorted.Count; i++)
        {
            LegacyCamera camera = sorted[i];
            LegacyCamera? next = i + 1 < sorted.Count ? sorted[i + 1] : null;
            double start = TakeTimeOf(camera.Time, keys);
            double until = next is { } n ? TakeTimeOf(n.Time, keys) : Math.Max(end, start + 1.0);
            if (until - start < 0.05)
                continue;

            Shot shot = next is { } following && !following.Cut && following.OnVehicle == camera.OnVehicle
                ? PathShot(camera, following, world, site)
                : TripodShot(camera, world, site);
            shot.At = Trigger.AtTime(Math.Round(start, 3));
            shot.Duration = Math.Round(until - start, 3);
            shot.Transition = new Transition { Kind = TransitionKind.Cut };
            take.Shots.Add(shot);
        }

        foreach (ReplaySpeedKey key in keys.OrderBy(k => k.Time))
        {
            take.Speed.Add(new SpeedKey
            {
                At = Trigger.AtTime(Math.Round(TakeTimeOf(Math.Max(key.Time, 0.0), keys), 3)),
                To = Math.Clamp(key.Speed, 0.1, 4.0),
                Ramp = 0.0,
                Ease = Ease.None,
            });
        }

        return take;
    }

    private static Shot TripodShot(LegacyCamera camera, ReplayWorld world, ReplaySite site)
    {
        (CapturedCamera captured, Basis3 vehicle, double height) = Capture(camera, world, site);
        Shot shot = ShotEdit.NewShot(ShotType.Tripod);
        shot.Target = camera.OnVehicle ? ShotTarget.Vehicle : ShotTarget.Site;
        ShotCapture.Apply(shot, captured, vehicle, height, null, out _);
        return shot;
    }

    private static Shot PathShot(LegacyCamera from, LegacyCamera to, ReplayWorld world, ReplaySite site)
    {
        Shot shot = ShotEdit.NewShot(ShotType.Path);
        shot.Target = from.OnVehicle ? ShotTarget.Vehicle : ShotTarget.Site;
        shot.Frame = ShotFrame.Vehicle;
        shot.Ease = Ease.None;
        shot.Points.Clear();
        foreach ((LegacyCamera camera, double t) in new[] { (from, 0.0), (to, 1.0) })
        {
            (CapturedCamera captured, Basis3 vehicle, double height) = Capture(camera, world, site);
            Basis3 frame = from.OnVehicle ? vehicle : Basis3.Identity;
            V3 look = ShotCapture.LookPoint(captured, vehicle, height);
            shot.Points.Add(new PathPoint
            {
                T = t,
                Position = Round(ShotCapture.ToLocal(frame, captured.Position)),
                LookAt = Round(ShotCapture.ToLocal(frame, look)),
                Fov = Math.Clamp(Math.Round(captured.Fov, 1), 1.0, 150.0),
            });
        }

        shot.Fov = shot.Points[0].Fov!.Value;
        return shot;
    }

    private static (CapturedCamera Camera, Basis3 Vehicle, double Height) Capture(LegacyCamera camera, ReplayWorld world, ReplaySite site)
    {
        Basis3 vehicle = world.FrameOf(world.Primary, camera.Time, site) ?? Basis3.Identity;
        double height = world.ShapeOf(world.Primary, camera.Time)?.Height ?? 1.0;
        var captured = new CapturedCamera(site.ToSite(camera.PositionCcf), site.DirectionToSite(camera.ForwardCcf).NormalizedOr(V3.UnitX), camera.Fov);
        return (captured, vehicle, height);
    }

    private static V3 Round(V3 v) => new(Math.Round(v.X, 2), Math.Round(v.Y, 2), Math.Round(v.Z, 2));

    private static bool IsFileName(string id) => id is { Length: > 0 and <= 200 } && id == id.Trim()
        && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && id is not "." and not "..";
}
