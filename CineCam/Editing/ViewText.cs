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
        ? UiText.Format("text.b7e558c8f303", $"{What}, {Starts}, in about {ViewText.Seconds(Math.Round(Eta.Value))}")
        : UiText.Format("", $"{What}, {Starts}");
}

/// <summary>Texts of the window that do not need the game, so the window only draws them.</summary>
public static class ViewText
{
    public static readonly string FramesHelp =
        UiText.Concat(UiText.Concat(UiText.Concat(UiText.MessageText("text.a2dfbedbbc18", "Site frame (target Site): +X is up, +Y is east, +Z is north, origin at the world point of the take (a Landmark, the anchor of the take, or the ground under the vehicle at the take start). "), UiText.MessageText("text.1d4edc272517", "Vehicle frame (target Vehicle): +X points to the nose. ")), UiText.MessageText("text.f8c509c26ed5", "Local frame (frame Local of a chase or part shot): +X is away from the body center, +Y is east, +Z is north, and it moves with the vehicle. ")), UiText.MessageText("text.6d24a3decb65", "Angles are in degrees and lengths in meters. The azimuth of an orbit or a crane is 0 deg at +Z (north) and 90 deg at +Y (east)."));

    public static string Num(double value) => value.ToString("0.0#", CultureInfo.InvariantCulture);

    public static string Seconds(double value) => UiText.Concat(Num(value), " s");

    public static string SpeedText(double value) => UiText.Concat(value.ToString("0.##", CultureInfo.InvariantCulture), "x");

    /// <summary>The second line of the live readout in the take setup, with the values of the flight state events.</summary>
    public static string FlightState(in VehicleSample s)
    {
        string control = s.Controllable switch
        {
            true => UiText.MessageText("text.8a798890fe93", "yes"),
            false => s.ControlReason is { Length: > 0 } reason ? UiText.Concat(UiText.Concat(UiText.MessageText("text.5c4b82f06ee6", "no ("), StoredEventText.Describe(EventKind.ControlLost, reason)), ")") : UiText.MessageText("text.9390298f3fb0", "no"),
            null => UiText.MessageText("text.7f756ecf273b", "no control module"),
        };
        string probes = s.ProbesFailed > 0 || s.ProbesRebooting > 0
            ? UiText.Format("text.77cefc55360b", $"{s.ProbesFailed} failed, {s.ProbesRebooting} rebooting")
            : UiText.MessageText("text.2689367b205c", "ok");
        string mission = s.MissionBody is { } body ? UiText.Concat((s.MissionArrived ? UiText.MessageText("text.c39a4df79b71", "arrived at ") : UiText.MessageText("text.4b00b1b86bfd", "to ")), body) : UiText.MessageText("text.140bedbf9c3f", "none");
        return UiText.Format("text.f40ca3a0a57e", $"control {control}, probe cores {probes}, probe power {UiText.Lower(UiText.EnumValue(s.ProbePower))}, life support {UiText.Lower(UiText.EnumValue(s.LifeSupport))}, incapacitated {s.Incapacitated}, mission {mission}, gimbal {s.GimbalLimit * 100.0:F0} % of limit, dynamic pressure {s.DynamicPressure / 1000.0:F2} kPa");

    }

    /// <param name="index">The place of the item in its list, to name the item that "after" refers to.</param>
    public static string TriggerText(Trigger trigger, int index)
    {
        switch (trigger.Kind)
        {
            case TriggerKind.Time:
                return UiText.Concat(UiText.MessageText("text.c6841a08f37d", "at "), Seconds(trigger.Time));
            case TriggerKind.Event:
                string name = trigger.Event.Length == 0 ? UiText.MessageText("text.8dd598d338e0", "(no event)") : trigger.Event;
                if (trigger.Delay == 0.0)
                    return name;
                return UiText.Concat(UiText.Concat(name, (trigger.Delay > 0.0 ? " +" : " -")), Seconds(Math.Abs(trigger.Delay)));
            default:
                return index <= 0 ? UiText.MessageText("text.5b52a7980f8d", "at the start") : UiText.Format("text.1f0c3f446f2c", $"after #{index}");
        }
    }

    public static TrackRow ShotRow(int index, Shot shot) =>
        new((index + 1).ToString(CultureInfo.InvariantCulture), UiText.Lower(UiText.EnumValue(shot.Type)),
            TriggerText(shot.At, index), Seconds(shot.Duration));

    public static TrackRow KeyRow(int index, SpeedKey key) =>
        new((index + 1).ToString(CultureInfo.InvariantCulture), UiText.Concat(UiText.MessageText("text.4b00b1b86bfd", "to "), SpeedText(key.To)),
            TriggerText(key.At, index), key.Ramp > 0.0 ? UiText.Concat(UiText.MessageText("text.038e24ece858", "ramp "), Seconds(key.Ramp)) : UiText.MessageText("text.6bdaf554e420", "jump"));

    public static TrackRow ActionRow(int index, TakeAction action) =>
        new((index + 1).ToString(CultureInfo.InvariantCulture), UiText.Lower(UiText.EnumValue(action.Kind)),
            TriggerText(action.At, index), action.Kind == ActionKind.Cue ? action.Name
                : action.Sequence.HasValue ? UiText.Concat(UiText.MessageText("text.b1e16dc874be", "sequence "), action.Sequence.Value.ToString(CultureInfo.InvariantCulture)) : UiText.MessageText("text.a078a416c0f2", "next sequence"));

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
            return take.Shots.Count == 0 ? UiText.MessageText("text.9b85a1bbcff2", "no shots") : UiText.MessageText("text.9aa4e091b0e4", "no shot yet");
        string percent = Math.Round(Math.Clamp(progress, 0.0, 1.0) * 100.0).ToString("0", CultureInfo.InvariantCulture);
        return UiText.Format("text.00bbb484f7c8", $"shot {index + 1} of {take.Shots.Count}, {UiText.Lower(UiText.EnumValue(take.Shots[index].Type))}, {percent} %");
    }

    public static string SimSpeedText(double current, double? target)
    {
        string text = SpeedText(current);
        if (current <= 0.0)
            text = UiText.MessageText("text.8322e87d2458", "stopped");
        return target.HasValue && Math.Abs(target.Value - current) > 0.005 ? UiText.Format("text.1a211b61c45e", $"{text} (target {SpeedText(target.Value)})") : text;
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
            Consider(UiText.Format("text.9c7d0081350c", $"shot {i + 1} ({UiText.Lower(UiText.EnumValue(take.Shots[i].Type))})"), take.Shots[i].At, i);
        for (int i = 0; i < take.Speed.Count; i++)
            Consider(UiText.Format("text.b4ba3c0f290a", $"speed key {i + 1} ({SpeedText(take.Speed[i].To)})"), take.Speed[i].At, i);
        for (int i = 0; i < take.Actions.Count; i++)
            Consider(UiText.Format("text.04e2e6ecbc0b", $"action {i + 1} ({UiText.Lower(UiText.EnumValue(take.Actions[i].Kind))})"), take.Actions[i].At, i);

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
            rows.Add(new EventRow(pair.Value.ToString("F1", CultureInfo.InvariantCulture), pair.Key, UiText.MessageText("text.7208794c984e", "seen"), true));
        foreach (string name in awaited.Distinct().Where(n => !seen.ContainsKey(n)))
        {
            string status = predictions.TryGetValue(name, out double eta) ? UiText.Concat(UiText.MessageText("text.ef537d80c589", "in about "), Seconds(Math.Round(eta))) : UiText.MessageText("text.80cfa3e7f28d", "waiting");
            rows.Add(new EventRow("", name, status, false));
        }

        return rows;
    }

    #endregion

    #region Reasons

    /// <summary>An empty text when the take can start.</summary>
    public static string StartBlock(bool running, bool resetActive, bool hasVehicle, bool sunMoving, bool fileValid) =>
        running ? UiText.MessageText("text.a931789ed590", "A take or a preview runs already.")
        : resetActive ? UiText.MessageText("text.cb65247d8a88", "A reset runs.")
        : !hasVehicle ? UiText.MessageText("text.484a738d6fee", "No controlled vehicle in flight.")
        : sunMoving ? UiText.MessageText("text.6c6277101f28", "The game time is moving.")
        : !fileValid ? UiText.MessageText("text.d8b8d8e62bd1", "Fix the problems of the file first.")
        : "";

    public static string CheckpointBlock(bool running, bool resetActive, bool sunMoving) =>
        running ? UiText.MessageText("text.ddbbf6a924b5", "A take or a preview runs.")
        : resetActive ? UiText.MessageText("text.cb65247d8a88", "A reset runs.")
        : sunMoving ? UiText.MessageText("text.6c6277101f28", "The game time is moving.")
        : "";

    public static string FileEditBlock(bool running) =>
        running ? UiText.MessageText("text.be11bd7ee809", "Stop the take to load the file or to add, remove or move takes and items.") : "";

    #endregion

    public static string FieldTip(ShotField field) => field switch
    {
        ShotField.Target => UiText.Concat(UiText.MessageText("text.760fadc4e48d", "World point: positions are in the frame of the world point of the take. Vehicle: positions are in the vehicle frame. "), FramesHelp),
        ShotField.Center => UiText.MessageText("text.c8d7ada74cd8", "A point on the axis of the orbit, in the frame of the target (m)."),
        ShotField.Radius => UiText.MessageText("text.043c1d40c54e", "Distance from the axis (m)."),
        ShotField.Height => UiText.MessageText("text.d101c001c0c0", "Height along the +X axis of the frame (m)."),
        ShotField.StartDeg => UiText.MessageText("text.145276b6a0d1", "Angle of the camera at the start of the orbit (deg). 0 is +Z (north), 90 is +Y (east)."),
        ShotField.DegPerSec => UiText.MessageText("text.dc47574c7762", "Turn speed (deg/s). A negative value turns the other way."),
        ShotField.LookAt => UiText.MessageText("text.d83c929fdfa2", "The point that the camera looks at, in the frame of the target (m). For a chase shot it is in the vehicle frame, for a part shot relative to the part."),
        ShotField.From => UiText.MessageText("text.aefe4c348af6", "Height of the camera at the start of the crane (m)."),
        ShotField.To => UiText.MessageText("text.747d7b2daa28", "Height of the camera at the end of the crane (m)."),
        ShotField.Azimuth => UiText.MessageText("text.5a32d488049d", "Direction of the camera from the axis (deg). 0 is +Z (north), 90 is +Y (east)."),
        ShotField.Position => UiText.MessageText("text.80cc36408fb5", "Camera position in the frame of the target (m)."),
        ShotField.Fit => UiText.MessageText("text.97ad3f03cebf", "The share of the picture height that the whole vehicle should fill, for example 0.7. The field of view follows the distance every frame (5 to 90 deg). A part shot frames the whole vehicle, not the part. Off leaves the field of view alone."),
        ShotField.FitBy => UiText.MessageText("text.97001854bc8b", "Size: the fit frames the diagonal of the length and the width of the vehicle, so the field of view does not change when the vehicle turns. Height: it frames only the length along the nose axis, the height of a rocket on the pad, so a slim vehicle fills more of the picture."),
        ShotField.KeepSize => UiText.MessageText("text.79954ea86419", "The field of view follows the distance, so the vehicle keeps its size in the picture."),
        ShotField.Offset => UiText.MessageText("text.c5b0352e4085", "Camera position in the vehicle frame (m). For a part shot it is relative to the part. +X points to the nose."),
        ShotField.Frame => UiText.MessageText("text.a42b4e7cfb15", "Vehicle: the offset, look point and path points are in the vehicle frame (+X toward the nose). Local: they are in the local horizon frame at the vehicle or the part (+X away from the body center, +Y east, +Z north), so the shot keeps its view of the Earth while the vehicle pitches over. Flight: the local frame turned so that +Z points along the horizontal direction of flight, so a camera at +Z looks back at the vehicle with its path and the Earth behind it. Below a few m/s of horizontal speed the direction is north."),
        ShotField.Up => UiText.MessageText("text.4d79d5eb6989", "Vehicle: the camera up follows the frame of the shot. Horizon: the camera up points away from the body center, so the horizon is level and the Earth is at the bottom. Horizon is the usual choice after the pitch-over and is the default for the local frame."),
        ShotField.Points => UiText.MessageText("text.3cbb1fa7e7bd", "The keyframes of the camera move. T is the share of the path from 0 to 1 at which the camera is at the position and looks at the look point. The camera runs through the points on a smooth curve, and the ease of the shot shapes the time."),
        ShotField.Aim => UiText.MessageText("text.2242816219c5", "Look: the camera looks at the look point or the path of the shot. Vehicle: it looks at the center of the bounding box of the vehicle, which is measured again when parts leave, so the shot follows the current vehicle. With a fit, the whole vehicle is framed in the center."),
        ShotField.AimOffset => UiText.MessageText("text.260ff7b15655", "Moves the look point away from the center of the vehicle, in the frame of the shot (m). Used with the aim at the vehicle."),
        ShotField.AvoidSun => UiText.MessageText("text.8cc8824424a9", "The smallest angle in degrees between the view and the sun, for example 30. When the camera looks closer to the sun, it moves on a circle around its look point until the angle is reached. The move is eased over about a second. The check is skipped while the body hides the sun. Off leaves the view alone."),
        ShotField.Subject => UiText.MessageText("text.500dda330dae", "Controlled: the shot follows the vehicle that the player controls. Separated: it follows the vehicle that a decoupler split off from it during the take (within 200 m), and the controlled vehicle until there is one. Fit and aim apply to the followed vehicle."),
        ShotField.Pick => UiText.MessageText("text.547ad540bd8c", "Newest: the vehicle that separated last. Nearest: the separated vehicle that is closest to the controlled vehicle."),
        ShotField.Smoothing => UiText.MessageText("text.130a6279a524", "Seconds that the camera needs to follow a turn of the vehicle. 0 is stiff."),
        _ => UiText.MessageText("text.7e1725f7fef9", "The id or display name of a part. Empty uses the base of the vehicle."),
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
