using System.Globalization;
using CineCam.Director;

namespace CineCam.Editing;

public enum RunState
{
    Idle,
    Ready,
    Recording,
    DryRun,
    Preview,
    Capturing,
    Paused,
    Resetting,
}

/// <param name="HasRun">True while a take, a dry run or a preview exists.</param>
/// <param name="ResetReady">True when a reset ended and the take did not start since.</param>
public readonly record struct RunStateInput(
    bool HasRun, bool DryRun, bool Preview, bool Capturing, bool Paused, bool ResetActive, bool ResetReady);

public readonly record struct TrackRow(string Number, string Name, string Starts, string Extra);

public readonly record struct EventRow(string Time, string Name, string Status, bool Seen);

/// <summary><see cref="Eta"/> is null when it cannot be predicted.</summary>
public readonly record struct NextTrigger(string What, string Starts, double? Eta)
{
    public string Text => Eta.HasValue
        ? $"{What}, {Starts}, in about {ViewText.Seconds(Math.Round(Eta.Value))}"
        : $"{What}, {Starts}";
}

/// <summary>Texts of the window that do not need the game, so the window only draws them.</summary>
public static class ViewText
{
    public const string FramesHelp =
        "Site frame (target Site): +X is up, +Y is east, +Z is north, origin at the world point of the take (a Landmark, the anchor of the take, or the ground under the vehicle at the take start). " +
        "Vehicle frame (target Vehicle): +X points to the nose. " +
        "Local frame (frame Local of a chase or part shot): +X is away from the body center, +Y is east, +Z is north, and it moves with the vehicle. " +
        "Angles are in degrees and lengths in meters. The azimuth of an orbit or a crane is 0 deg at +Z (north) and 90 deg at +Y (east).";

    public static string Num(double value) => value.ToString("0.0#", CultureInfo.InvariantCulture);

    public static string Seconds(double value) => Num(value) + " s";

    public static string SpeedText(double value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "x";

    /// <param name="index">The place of the item in its list, to name the item that "after" refers to.</param>
    public static string TriggerText(Trigger trigger, int index)
    {
        switch (trigger.Kind)
        {
            case TriggerKind.Time:
                return "at " + Seconds(trigger.Time);
            case TriggerKind.Event:
                string name = trigger.Event.Length == 0 ? "(no event)" : trigger.Event;
                if (trigger.Delay == 0.0)
                    return name;
                return name + (trigger.Delay > 0.0 ? " +" : " -") + Seconds(Math.Abs(trigger.Delay));
            default:
                return index <= 0 ? "at the start" : $"after #{index}";
        }
    }

    public static TrackRow ShotRow(int index, Shot shot) =>
        new((index + 1).ToString(CultureInfo.InvariantCulture), shot.Type.ToString().ToLowerInvariant(),
            TriggerText(shot.At, index), Seconds(shot.Duration));

    public static TrackRow KeyRow(int index, SpeedKey key) =>
        new((index + 1).ToString(CultureInfo.InvariantCulture), "to " + SpeedText(key.To),
            TriggerText(key.At, index), key.Ramp > 0.0 ? "ramp " + Seconds(key.Ramp) : "jump");

    public static TrackRow ActionRow(int index, TakeAction action) =>
        new((index + 1).ToString(CultureInfo.InvariantCulture), action.Kind.ToString().ToLowerInvariant(),
            TriggerText(action.At, index), action.Sequence.HasValue ? "sequence " + action.Sequence.Value.ToString(CultureInfo.InvariantCulture) : "next sequence");

    #region State

    public static RunState StateOf(RunStateInput input)
    {
        if (input.Paused)
            return RunState.Paused;
        if (input.HasRun)
        {
            if (input.Preview)
                return RunState.Preview;
            if (input.DryRun)
                return RunState.DryRun;
            return input.Capturing ? RunState.Capturing : RunState.Recording;
        }

        if (input.ResetActive)
            return RunState.Resetting;
        return input.ResetReady ? RunState.Ready : RunState.Idle;
    }

    public static string ShotText(Take take, int index, double progress)
    {
        if (index < 0 || index >= take.Shots.Count)
            return take.Shots.Count == 0 ? "no shots" : "no shot yet";
        string percent = Math.Round(Math.Clamp(progress, 0.0, 1.0) * 100.0).ToString("0", CultureInfo.InvariantCulture);
        return $"shot {index + 1} of {take.Shots.Count}, {take.Shots[index].Type.ToString().ToLowerInvariant()}, {percent} %";
    }

    public static string SimSpeedText(double current, double? target)
    {
        string text = SpeedText(current);
        if (current <= 0.0)
            text = "stopped";
        return target.HasValue && Math.Abs(target.Value - current) > 0.005 ? $"{text} (target {SpeedText(target.Value)})" : text;
    }

    #endregion

    #region Next trigger

    /// <summary>
    /// The earliest item with a known time wins, else the first item that waits for an event. An item that
    /// follows the one before has no time of its own and is left out.
    /// </summary>
    public static NextTrigger? FindNext(Take take, IReadOnlyDictionary<string, string> aliases,
        IReadOnlyDictionary<string, double> seen, IReadOnlyDictionary<string, double> predictions,
        double now, int currentShot)
    {
        NextTrigger? bestKnown = null;
        NextTrigger? firstUnknown = null;

        void Consider(string what, Trigger trigger, int index)
        {
            if (!StartsIn(trigger, aliases, seen, predictions, now, out double? eta, out bool started) || started)
                return;
            var candidate = new NextTrigger(what, TriggerText(trigger, index), eta);
            if (eta.HasValue)
            {
                if (bestKnown == null || eta.Value < bestKnown.Value.Eta!.Value)
                    bestKnown = candidate;
            }
            else
            {
                firstUnknown ??= candidate;
            }
        }

        for (int i = Math.Max(currentShot + 1, 0); i < take.Shots.Count; i++)
            Consider($"shot {i + 1} ({take.Shots[i].Type.ToString().ToLowerInvariant()})", take.Shots[i].At, i);
        for (int i = 0; i < take.Speed.Count; i++)
            Consider($"speed key {i + 1} ({SpeedText(take.Speed[i].To)})", take.Speed[i].At, i);
        for (int i = 0; i < take.Actions.Count; i++)
            Consider($"action {i + 1} ({take.Actions[i].Kind.ToString().ToLowerInvariant()})", take.Actions[i].At, i);

        return bestKnown ?? firstUnknown;
    }

    // Returns false for a trigger that has no time of its own.
    private static bool StartsIn(Trigger trigger, IReadOnlyDictionary<string, string> aliases,
        IReadOnlyDictionary<string, double> seen, IReadOnlyDictionary<string, double> predictions,
        double now, out double? eta, out bool started)
    {
        eta = null;
        started = false;
        switch (trigger.Kind)
        {
            case TriggerKind.Time:
                started = trigger.Time <= now;
                eta = Math.Max(trigger.Time - now, 0.0);
                return true;
            case TriggerKind.Event:
                if (!EventName.TryResolve(trigger.Event, aliases, out EventName name, out _))
                    return false;
                string canonical = name.Canonical;
                if (seen.TryGetValue(canonical, out double at))
                {
                    double left = at + trigger.Delay - now;
                    started = left <= 0.0;
                    eta = Math.Max(left, 0.0);
                    return true;
                }

                if (predictions.TryGetValue(canonical, out double predicted))
                    eta = Math.Max(predicted + trigger.Delay, 0.0);
                return true;
            default:
                return false;
        }
    }

    #endregion

    #region Events

    public static List<EventRow> EventRows(IReadOnlyDictionary<string, double> seen,
        IReadOnlyDictionary<string, double> predictions, IEnumerable<string> awaited)
    {
        var rows = new List<EventRow>();
        foreach (var pair in seen.OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
            rows.Add(new EventRow(pair.Value.ToString("F1", CultureInfo.InvariantCulture), pair.Key, "seen", true));
        foreach (string name in awaited.Distinct().Where(n => !seen.ContainsKey(n)))
        {
            string status = predictions.TryGetValue(name, out double eta) ? "in about " + Seconds(Math.Round(eta)) : "waiting";
            rows.Add(new EventRow("", name, status, false));
        }

        return rows;
    }

    #endregion

    #region Reasons

    /// <summary>An empty text when the take can start.</summary>
    public static string StartBlock(bool running, bool resetActive, bool hasVehicle, bool sunMoving, bool fileValid) =>
        running ? "A take or a preview runs already."
        : resetActive ? "A reset runs."
        : !hasVehicle ? "No controlled vehicle in flight."
        : sunMoving ? "The game time is moving."
        : !fileValid ? "Fix the problems of the file first."
        : "";

    public static string CheckpointBlock(bool running, bool resetActive, bool sunMoving) =>
        running ? "A take or a preview runs."
        : resetActive ? "A reset runs."
        : sunMoving ? "The game time is moving."
        : "";

    public static string FileEditBlock(bool running) =>
        running ? "Stop the take to load the file or to add, remove or move takes and items." : "";

    #endregion

    public static string FieldTip(ShotField field) => field switch
    {
        ShotField.Target => "World point: positions are in the frame of the world point of the take. Vehicle: positions are in the vehicle frame. " + FramesHelp,
        ShotField.Center => "A point on the axis of the orbit, in the frame of the target (m).",
        ShotField.Radius => "Distance from the axis (m).",
        ShotField.Height => "Height along the +X axis of the frame (m).",
        ShotField.StartDeg => "Angle of the camera at the start of the orbit (deg). 0 is +Z (north), 90 is +Y (east).",
        ShotField.DegPerSec => "Turn speed (deg/s). A negative value turns the other way.",
        ShotField.LookAt => "The point that the camera looks at, in the frame of the target (m). For a chase shot it is in the vehicle frame, for a part shot relative to the part.",
        ShotField.From => "Height of the camera at the start of the crane (m).",
        ShotField.To => "Height of the camera at the end of the crane (m).",
        ShotField.Azimuth => "Direction of the camera from the axis (deg). 0 is +Z (north), 90 is +Y (east).",
        ShotField.Position => "Camera position in the frame of the target (m).",
        ShotField.Fit => "The share of the picture height that the whole vehicle should fill, for example 0.7. The field of view follows the distance every frame (5 to 90 deg). A part shot frames the whole vehicle, not the part. Off leaves the field of view alone.",
        ShotField.KeepSize => "The field of view follows the distance, so the vehicle keeps its size in the picture.",
        ShotField.Offset => "Camera position in the vehicle frame (m). For a part shot it is relative to the part. +X points to the nose.",
        ShotField.Frame => "Vehicle: the offset, look point and path points are in the vehicle frame (+X toward the nose). Local: they are in the local horizon frame at the vehicle or the part (+X away from the body center, +Y east, +Z north), so the shot keeps its view of the Earth while the vehicle pitches over. Flight: the local frame turned so that +Z points along the horizontal direction of flight, so a camera at +Z looks back at the vehicle with its path and the Earth behind it. Below a few m/s of horizontal speed the direction is north.",
        ShotField.Up => "Vehicle: the camera up follows the frame of the shot. Horizon: the camera up points away from the body center, so the horizon is level and the Earth is at the bottom. Horizon is the usual choice after the pitch-over and is the default for the local frame.",
        ShotField.Points => "The keyframes of the camera move. T is the share of the path from 0 to 1 at which the camera is at the position and looks at the look point. The camera runs through the points on a smooth curve, and the ease of the shot shapes the time.",
        ShotField.Aim => "Look: the camera looks at the look point or the path of the shot. Vehicle: it looks at the center of the bounding box of the vehicle, which is measured again when parts leave, so the shot follows the current vehicle. With a fit, the whole vehicle is framed in the center.",
        ShotField.AimOffset => "Moves the look point away from the center of the vehicle, in the frame of the shot (m). Used with the aim at the vehicle.",
        ShotField.AvoidSun => "The smallest angle in degrees between the view and the sun, for example 30. When the camera looks closer to the sun, it moves on a circle around its look point until the angle is reached. The move is eased over about a second. The check is skipped while the body hides the sun. Off leaves the view alone.",
        ShotField.Subject => "Controlled: the shot follows the vehicle that the player controls. Separated: it follows the vehicle that a decoupler split off from it during the take (within 200 m), and the controlled vehicle until there is one. Fit and aim apply to the followed vehicle.",
        ShotField.Pick => "Newest: the vehicle that separated last. Nearest: the separated vehicle that is closest to the controlled vehicle.",
        ShotField.Smoothing => "Seconds that the camera needs to follow a turn of the vehicle. 0 is stiff.",
        _ => "The id or display name of a part. Empty uses the base of the vehicle.",
    };
}

/// <summary>
/// Keeps "ready" true from the end of a reset until the next take starts, because the reset flow itself
/// stays in its last phase for good.
/// </summary>
public sealed class ReadyLatch
{
    public bool Ready { get; private set; }

    public void Update(ResetPhase phase, bool takeRuns)
    {
        if (takeRuns || phase != ResetPhase.Done)
            Ready = false;
        else if (_previous != ResetPhase.Done)
            Ready = true;
        _previous = phase;
    }

    private ResetPhase _previous = ResetPhase.Idle;
}
