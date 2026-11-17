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
        EventKind.Sequence => "sequence:" + Value.ToString("0", CultureInfo.InvariantCulture),
        EventKind.Altitude => "altitude:" + Value.ToString("R", CultureInfo.InvariantCulture),
        EventKind.Speed => "speed:" + Value.ToString("R", CultureInfo.InvariantCulture),
        EventKind.Ground => "ground:" + Value.ToString("R", CultureInfo.InvariantCulture),
        EventKind.Slow => "slow:" + Value.ToString("R", CultureInfo.InvariantCulture),
        EventKind.Q => "q:" + Value.ToString("R", CultureInfo.InvariantCulture),
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
        count < 1.5 ? head : head + ":" + count.ToString("0", CultureInfo.InvariantCulture);

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
            error = "the event name is empty";
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
                    error = "the event \"liftoff\" takes no number";
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
                    error = $"the event \"{head}\" takes a whole number from 1 after the colon, for example \"{head}:2\"";
                    return false;
                }

                name = new EventName(countedKind, count);
                return true;
            }
            case "sequence" or "altitude" or "speed" or "ground" or "slow" or "q":
                if (!double.TryParse(tail, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    || double.IsNaN(value) || double.IsInfinity(value))
                {
                    error = $"the event \"{head}\" needs a number after the colon, for example \"{head}:3\"";
                    return false;
                }

                if (head == "sequence" && (value < 1 || value != Math.Floor(value)))
                {
                    error = "a sequence number is a whole number from 1";
                    return false;
                }

                if (head == "ground" && value < 0)
                {
                    error = "the height over the ground is 0 or more meters";
                    return false;
                }

                if (head == "slow" && value <= 0)
                {
                    error = "the speed of \"slow\" is more than 0 meters per second, because a vehicle seldom stands fully still";
                    return false;
                }

                if (head == "q" && value <= 0)
                {
                    error = "the dynamic pressure of \"q\" is more than 0 pascals, for example \"q:20000\"";
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
                error = $"unknown event \"{trimmed}\" (known: ignition, liftoff, burnout, landed, soi, apoapsis, periapsis, docked, undocked, mark, control-lost, control-back, reboot, probe-failed, power-low, life-support-low, incapacitated, arrival, eva, board, explosion, gimbal-limit and maxq, each counted with :N, and sequence:N, altitude:M, speed:V, ground:M, slow:V, q:PASCALS, or an alias from \"events\")";
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
    /// <summary>An alias or a text that is not an event name comes back as it is.</summary>
    public static string Name(string eventName)
    {
        if (!EventName.TryParse(eventName, out EventName name, out _))
            return eventName;

        return name.Kind switch
        {
            EventKind.Ignition => Counted("Ignition", name.Value),
            EventKind.Liftoff => "Lift-off",
            EventKind.Burnout => Counted("Burnout", name.Value),
            EventKind.Sequence => "Stage " + Whole(name.Value),
            EventKind.Altitude => "Altitude " + Distance(name.Value),
            EventKind.Speed => "Speed " + Short(name.Value) + " m/s",
            EventKind.Ground => "Over ground " + Distance(name.Value),
            EventKind.Slow => "Slow to " + Short(name.Value) + " m/s",
            EventKind.Landed => Counted("Landed", name.Value),
            EventKind.Soi => Counted("SOI change", name.Value),
            EventKind.Apoapsis => Counted("Apoapsis", name.Value),
            EventKind.Periapsis => Counted("Periapsis", name.Value),
            EventKind.Docked => Counted("Docked", name.Value),
            EventKind.Undocked => Counted("Undocked", name.Value),
            EventKind.Mark => Counted("Mark", name.Value),
            EventKind.ControlLost => Counted("Control lost", name.Value),
            EventKind.ControlBack => Counted("Control back", name.Value),
            EventKind.Reboot => Counted("Probe core reboot", name.Value),
            EventKind.ProbeFailed => Counted("Probe core failed", name.Value),
            EventKind.PowerLow => Counted("Probe power low", name.Value),
            EventKind.LifeSupportLow => Counted("Life support low", name.Value),
            EventKind.Incapacitated => Counted("Crew incapacitated", name.Value),
            EventKind.Arrival => Counted("Mission arrival", name.Value),
            EventKind.Eva => Counted("EVA", name.Value),
            EventKind.Board => Counted("Boarding", name.Value),
            EventKind.Explosion => Counted("Explosion", name.Value),
            EventKind.GimbalLimit => Counted("Gimbal at limit", name.Value),
            EventKind.MaxQ => Counted("Max Q", name.Value),
            EventKind.Q => "Dynamic pressure " + Pressure(name.Value),
            _ => eventName,
        };
    }

    private static string Counted(string head, double count) => count < 1.5 ? head : head + " " + Whole(count);

    private static string Whole(double value) => value.ToString("0", CultureInfo.InvariantCulture);

    private static string Short(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Pressure(double pascals) =>
        Math.Abs(pascals) >= 1000.0 ? Short(pascals / 1000.0) + " kPa" : Short(pascals) + " Pa";

    private static string Distance(double meters) =>
        Math.Abs(meters) >= 1000.0 ? Short(meters / 1000.0) + " km" : Short(meters) + " m";
}
