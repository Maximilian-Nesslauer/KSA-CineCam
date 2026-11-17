using System.Globalization;

namespace CineCam.Director;

public enum EventKind
{
    Ignition, Liftoff, Burnout, Sequence, Altitude, Speed, Landed, Soi, Apoapsis, Periapsis, Docked, Undocked, Mark, Ground, Slow,
    ControlLost, ControlBack, Reboot, ProbeFailed, PowerLow, LifeSupportLow, Incapacitated, Arrival, Eva, Board, Explosion,
    GimbalLimit, MaxQ, Q,
}

/// <summary>
/// A parsed canonical event name. For a counted event (ignition, burnout, landed, soi, apoapsis,
/// periapsis, docked, undocked, mark and the flight state events such as control-lost or maxq) the value is
/// the count: 1 (or 0) for the first time, 2 for the second time, and so on.
/// </summary>
public readonly record struct EventName(EventKind Kind, double Value)
{
    /// <summary>
    /// A predictable event can have a negative delay, because the take predicts it from the rate of a value, and a
    /// replay finds it from the samples of the recording.
    /// </summary>
    public bool IsPredictable => Kind is EventKind.Altitude or EventKind.Speed or EventKind.Ground or EventKind.Slow
        or EventKind.Q or EventKind.MaxQ;

    /// <summary>
    /// A max Q has the time of the peak, but a take finds it only when the pressure has fallen below the peak, so
    /// its time is in the past when it comes. A start in the past would jump into the middle of the shot, so the
    /// timeline starts the shot when the event comes.
    /// </summary>
    public bool IsFoundLate => Kind == EventKind.MaxQ;

    /// <summary>
    /// True for the events that came with CineCam 0.4.0. A shot file of an older version can have an alias with
    /// such a name, and that alias keeps its meaning (see <see cref="TryResolve"/>).
    /// </summary>
    public bool AliasMayShadow => Kind >= EventKind.ControlLost;

    /// <summary>
    /// The value above which a ground or slow event arms, see <see cref="EventDetector"/>. The margin is
    /// 1 m or 2 percent of the height for a ground event and 0.5 m/s for a slow event, so the noise of a
    /// vehicle that settles or stands on a slope cannot arm it, and a vehicle in flight passes it at once.
    /// </summary>
    public double ArmLevel => Kind == EventKind.Ground ? Value + Math.Max(1.0, 0.02 * Value) : Value + 0.5;

    public string Canonical => Kind switch
    {
        EventKind.Ignition => Counted("ignition", Value),
        EventKind.Liftoff => "liftoff",
        EventKind.Burnout => Counted("burnout", Value),
        EventKind.Sequence => UiText.Concat("sequence:", Value.ToString("0", CultureInfo.InvariantCulture)),
        EventKind.Altitude => UiText.Concat("altitude:", Value.ToString("R", CultureInfo.InvariantCulture)),
        EventKind.Speed => UiText.Concat("speed:", Value.ToString("R", CultureInfo.InvariantCulture)),
        EventKind.Ground => UiText.Concat("ground:", Value.ToString("R", CultureInfo.InvariantCulture)),
        EventKind.Slow => UiText.Concat("slow:", Value.ToString("R", CultureInfo.InvariantCulture)),
        EventKind.Q => UiText.Concat("q:", Value.ToString("R", CultureInfo.InvariantCulture)),
        _ => Counted(CountedHead(Kind), Value),
    };

    // A new counted kind goes here. Ignition and burnout have their own cases in the switches.
    private static readonly (string Head, EventKind Kind)[] CountedKinds =
    {
        ("landed", EventKind.Landed),
        ("soi", EventKind.Soi),
        ("apoapsis", EventKind.Apoapsis),
        ("periapsis", EventKind.Periapsis),
        ("docked", EventKind.Docked),
        ("undocked", EventKind.Undocked),
        ("mark", EventKind.Mark),
        ("control-lost", EventKind.ControlLost),
        ("control-back", EventKind.ControlBack),
        ("reboot", EventKind.Reboot),
        ("probe-failed", EventKind.ProbeFailed),
        ("power-low", EventKind.PowerLow),
        ("life-support-low", EventKind.LifeSupportLow),
        ("incapacitated", EventKind.Incapacitated),
        ("arrival", EventKind.Arrival),
        ("eva", EventKind.Eva),
        ("board", EventKind.Board),
        ("explosion", EventKind.Explosion),
        ("gimbal-limit", EventKind.GimbalLimit),
        ("maxq", EventKind.MaxQ),
    };

    private static string CountedHead(EventKind kind)
    {
        foreach ((string head, EventKind k) in CountedKinds)
        {
            if (k == kind)
                return head;
        }

        throw new ArgumentOutOfRangeException(nameof(kind));
    }

    private static EventKind? CountedKind(string head) => head switch
    {
        "ignition" => EventKind.Ignition,
        "burnout" => EventKind.Burnout,
        _ => CountedKinds.Where(c => c.Head == head).Select(c => (EventKind?)c.Kind).FirstOrDefault(),
    };

    /// <summary>The canonical name of the Nth time of a counted event. The first one has no number.</summary>
    public static string Counted(string head, double count) =>
        count < 1.5 ? head : UiText.Concat(UiText.Concat(head, ":"), count.ToString("0", CultureInfo.InvariantCulture));

    /// <summary>
    /// The names are case insensitive. Altitude and the height over the ground are in meters, speed is in
    /// meters per second, and the dynamic pressure of "q" is in pascals.
    /// </summary>
    public static bool TryParse(string? text, out EventName name, out string error)
    {
        name = default;
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = UiText.MessageText("text.19d7ee4fd564", "the event name is empty");
            return false;
        }

        string trimmed = text.Trim();
        int colon = trimmed.IndexOf(':');
        string head = (colon < 0 ? trimmed : trimmed[..colon]).ToLowerInvariant();
        string tail = colon < 0 ? "" : trimmed[(colon + 1)..].Trim();

        switch (head)
        {
            case "liftoff":
                if (colon >= 0)
                {
                    error = UiText.MessageText("text.cf4a642d051d", "the event \"liftoff\" takes no number");
                    return false;
                }

                name = new EventName(EventKind.Liftoff, 0.0);
                return true;
            case var counted when CountedKind(counted) is EventKind countedKind:
            {
                double count = 1.0;
                if (colon >= 0
                    && (!double.TryParse(tail, NumberStyles.Float, CultureInfo.InvariantCulture, out count)
                        || double.IsNaN(count) || double.IsInfinity(count) || count < 1 || count != Math.Floor(count)))
                {
                    error = UiText.Format("text.ec6ec82fb0e5", $"the event \"{head}\" takes a whole number from 1 after the colon, for example \"{head}:2\"");
                    return false;
                }

                name = new EventName(countedKind, count);
                return true;
            }
            case "sequence" or "altitude" or "speed" or "ground" or "slow" or "q":
                if (!double.TryParse(tail, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    || double.IsNaN(value) || double.IsInfinity(value))
                {
                    error = UiText.Format("text.d84ab71ab93b", $"the event \"{head}\" needs a number after the colon, for example \"{head}:3\"");
                    return false;
                }

                if (head == UiText.MessageText("text.bf37c7c20871", "sequence") && (value < 1 || value != Math.Floor(value)))
                {
                    error = UiText.MessageText("text.2f3d1d9d943b", "a sequence number is a whole number from 1");
                    return false;
                }

                if (head == "ground" && value < 0)
                {
                    error = UiText.MessageText("text.d3a1efdba16f", "the height over the ground is 0 or more meters");
                    return false;
                }

                if (head == "slow" && value <= 0)
                {
                    error = UiText.MessageText("text.06cb2106bb4b", "the speed of \"slow\" is more than 0 meters per second, because a vehicle seldom stands fully still");
                    return false;
                }

                if (head == "q" && value <= 0)
                {
                    error = UiText.MessageText("text.4356768a179a", "the dynamic pressure of \"q\" is more than 0 pascals, for example \"q:20000\"");
                    return false;
                }

                name = new EventName(head switch
                {
                    "sequence" => EventKind.Sequence,
                    "altitude" => EventKind.Altitude,
                    "speed" => EventKind.Speed,
                    "ground" => EventKind.Ground,
                    "q" => EventKind.Q,
                    _ => EventKind.Slow,
                }, value);
                return true;
            default:
                error = UiText.Format("text.69105f47e2de", $"unknown event \"{trimmed}\" (known: ignition, liftoff, burnout, landed, soi, apoapsis, periapsis, docked, undocked, mark, control-lost, control-back, reboot, probe-failed, power-low, life-support-low, incapacitated, arrival, eva, board, explosion, gimbal-limit and maxq, each counted with :N, and sequence:N, altitude:M, speed:V, ground:M, slow:V, q:PASCALS, or an alias from \"events\")");
                return false;
        }
    }

    /// <summary>Resolves an alias of the file to a canonical event. A name that is not an alias must itself be canonical.</summary>
    public static bool TryResolve(string text, IReadOnlyDictionary<string, string> aliases,
        out EventName name, out string error)
    {
        string key = text.Trim();
        if (aliases.TryGetValue(key, out string? target))
            return TryParse(target, out name, out error);

        return TryParse(key, out name, out error);
    }
}

public static class EventPrediction
{
    /// <summary>Seconds until a value that changes at a constant rate reaches a target, or null when it moves away or stands still.</summary>
    public static double? SecondsUntil(double current, double target, double rate)
    {
        double distance = target - current;
        if (distance <= 0.0)
            return 0.0;
        if (rate <= 1e-9)
            return null;
        return distance / rate;
    }

    /// <summary>Seconds until a value that changes at a constant rate falls to a target, or null when it rises or stands still.</summary>
    public static double? SecondsUntilFall(double current, double target, double rate) =>
        SecondsUntil(-current, -target, -rate);
}

/// <summary>Player-facing names of events, for the window and the timeline.</summary>
public static class EventDisplay
{
    private static readonly string[] BuiltInChoices =
    [
        "ignition", "ignition:2", "liftoff", "burnout", "burnout:2", "sequence:1", "sequence:2", "sequence:3",
        "ground:100", "landed", "soi", "apoapsis", "periapsis", "docked", "undocked", "mark", "maxq", "q:20000",
        "gimbal-limit", "explosion", "control-lost", "control-back", "reboot", "probe-failed", "power-low",
        "life-support-low", "incapacitated", "arrival", "eva", "board",
    ];

    public static IEnumerable<(string Key, string Label)> Choices(IReadOnlyDictionary<string, string>? aliases)
    {
        foreach (string builtIn in BuiltInChoices)
        {
            string key = builtIn;
            if (aliases?.ContainsKey(key) == true)
            {
                // The unnumbered form of a counted event means its first occurrence.
                key = builtIn + ":1";
                if (aliases.ContainsKey(key) || !EventName.TryParse(builtIn, out EventName expected, out _)
                    || !EventName.TryResolve(key, aliases, out EventName actual, out _) || actual != expected)
                    continue;
            }
            yield return (key, Name(key));
        }

        if (aliases != null)
            foreach (string key in aliases.Keys)
                if (key == key.Trim() && EventName.TryResolve(key, aliases, out _, out _))
                    yield return (key, Name(key, aliases));
    }

    public static string Name(string eventName, IReadOnlyDictionary<string, string>? aliases = null)
    {
        if (aliases != null && aliases.TryGetValue(eventName.Trim(), out string? target))
            return UiText.Format("", $"{eventName} = {Name(target)}");

        if (!EventName.TryParse(eventName, out EventName name, out _))
            return eventName;

        return name.Kind switch
        {
            EventKind.Ignition => Counted(UiText.MessageText("text.ae1c32802c31", "Ignition"), name.Value),
            EventKind.Liftoff => UiText.MessageText("text.cf343cf67ee7", "Lift-off"),
            EventKind.Burnout => Counted(UiText.MessageText("text.efbc3e50f6b3", "Burnout"), name.Value),
            EventKind.Sequence => UiText.Concat(UiText.MessageText("text.fa33951b7cba", "Stage "), Whole(name.Value)),
            EventKind.Altitude => UiText.Concat(UiText.MessageText("text.523ebac116d3", "Altitude "), Distance(name.Value)),
            EventKind.Speed => UiText.Concat(UiText.Concat(UiText.MessageText("text.3d896822b102", "Speed "), Short(name.Value)), " m/s"),
            EventKind.Ground => UiText.Concat(UiText.MessageText("text.adcd2aabca3b", "Over ground "), Distance(name.Value)),
            EventKind.Slow => UiText.Concat(UiText.Concat(UiText.MessageText("text.f4475b17b777", "Slow to "), Short(name.Value)), " m/s"),
            EventKind.Landed => Counted("Landed", name.Value),
            EventKind.Soi => Counted(UiText.MessageText("text.e8894c1924d5", "SOI change"), name.Value),
            EventKind.Apoapsis => Counted("Apoapsis", name.Value),
            EventKind.Periapsis => Counted(UiText.MessageText("text.7e64e2e442b6", "Periapsis"), name.Value),
            EventKind.Docked => Counted(UiText.MessageText("text.5e055c3d1755", "Docked"), name.Value),
            EventKind.Undocked => Counted(UiText.MessageText("text.654dcc689d68", "Undocked"), name.Value),
            EventKind.Mark => Counted(UiText.MessageText("text.d7cda0ca2c85", "Mark"), name.Value),
            EventKind.ControlLost => Counted(UiText.MessageText("text.4756a4f3516a", "Control lost"), name.Value),
            EventKind.ControlBack => Counted(UiText.MessageText("text.60d9e7bd5242", "Control back"), name.Value),
            EventKind.Reboot => Counted(UiText.MessageText("text.84b7f5d9562b", "Probe core reboot"), name.Value),
            EventKind.ProbeFailed => Counted(UiText.MessageText("text.33d0b54bbf76", "Probe core failed"), name.Value),
            EventKind.PowerLow => Counted(UiText.MessageText("text.b26d5dbb38a2", "Probe power low"), name.Value),
            EventKind.LifeSupportLow => Counted(UiText.MessageText("text.afeae943e602", "Life support low"), name.Value),
            EventKind.Incapacitated => Counted(UiText.MessageText("text.883919dead2c", "Crew incapacitated"), name.Value),
            EventKind.Arrival => Counted(UiText.MessageText("text.9d47b93d2e16", "Mission arrival"), name.Value),
            EventKind.Eva => Counted(UiText.MessageText("text.5517bab65366", "EVA"), name.Value),
            EventKind.Board => Counted(UiText.MessageText("text.e74749876753", "Boarding"), name.Value),
            EventKind.Explosion => Counted(UiText.MessageText("text.ca6482a42bab", "Explosion"), name.Value),
            EventKind.GimbalLimit => Counted(UiText.MessageText("text.e7aacd7a39c7", "Gimbal at limit"), name.Value),
            EventKind.MaxQ => Counted(UiText.MessageText("text.ec9422135b02", "Max Q"), name.Value),
            EventKind.Q => UiText.Concat(UiText.MessageText("text.3e44f4366ef1", "Dynamic pressure "), Pressure(name.Value)),
            _ => eventName,
        };
    }

    private static string Counted(string head, double count) => count < 1.5 ? head : UiText.Format("", $"{head} {Whole(count)}");

    private static string Whole(double value) => value.ToString("0", CultureInfo.InvariantCulture);

    private static string Short(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Pressure(double pascals) =>
        Math.Abs(pascals) >= 1000.0 ? UiText.Concat(Short(pascals / 1000.0), " kPa") : UiText.Concat(Short(pascals), " Pa");

    private static string Distance(double meters) =>
        Math.Abs(meters) >= 1000.0 ? UiText.Concat(Short(meters / 1000.0), " km") : UiText.Concat(Short(meters), " m");
}
