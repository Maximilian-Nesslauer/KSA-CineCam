using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CineCam.Director;

namespace CineCam.Editing;

public enum EventTimeSource
{
    Live,

    /// <summary>The event log of the latest finished run of the take has the event.</summary>
    LastRun,

    /// <summary>No run has a time for the event, so the timeline places it at an even spacing.</summary>
    Estimated,

    /// <summary>The recording of a replay has the event, so its take time is exact.</summary>
    Recording,
}

/// <summary><see cref="Key"/> is the canonical name, or the trimmed text of a name that does not resolve.</summary>
public sealed record TimelineEvent(string Key, double Time, EventTimeSource Source)
{
    public bool Estimated => Source == EventTimeSource.Estimated;
}

/// <summary>A blend fades in over <see cref="BlendSeconds"/> from <see cref="Start"/>.</summary>
public sealed record TimelineShot(int Index, double Start, double End, double BlendSeconds, bool Estimated, bool Loop);

public sealed record TimelineKey(int Index, double Time, double RampEnd, bool Estimated);

public sealed record TimelineAction(int Index, double Time, bool Estimated);

/// <summary>A mark of a recording that is no event, such as a separation, at its take time.</summary>
public sealed record TimelineMarker(double Time, string Label);

public enum TimelineItemKind { Shot, Key, Action }

/// <summary>An item that never starts on a recording, because the event that it waits for is not in it.</summary>
public sealed record TimelineUnreached(TimelineItemKind Kind, int Index, string EventKey);

/// <summary>
/// Times are take seconds. A start on an event uses the time that the running take saw, else the time from
/// the latest finished run, else an estimate, and on a recording the recorded time only. Nothing here
/// touches the game.
/// </summary>
/// <remarks>
/// Estimated events are placed in the order in which the take first names them, shots before speed keys
/// before actions, each at least <see cref="EstimateSpacing"/> after the one before and after the running
/// take time. A shot that waits for an estimated event does not start before the shot before it ends, so
/// the blocks keep their order.
/// </remarks>
public sealed class TimelineLayout
{
    public const double EstimateSpacing = 10.0;

    /// <summary>The shortest duration that a drag on the timeline gives a shot.</summary>
    public const double MinDuration = 0.1;

    public IReadOnlyList<TimelineShot> Shots { get; private init; } = Array.Empty<TimelineShot>();

    public IReadOnlyList<TimelineKey> Keys { get; private init; } = Array.Empty<TimelineKey>();

    public IReadOnlyList<TimelineAction> Actions { get; private init; } = Array.Empty<TimelineAction>();

    public IReadOnlyList<TimelineEvent> Events { get; private init; } = Array.Empty<TimelineEvent>();

    /// <summary>At least one second.</summary>
    public double Duration { get; private init; } = 1.0;

    public bool HasEstimates { get; private init; }

    public IReadOnlyList<TimelineMarker> Markers { get; private init; } = Array.Empty<TimelineMarker>();

    /// <summary>Only a layout on a recording has them.</summary>
    public IReadOnlyList<TimelineUnreached> Unreached { get; private init; } = Array.Empty<TimelineUnreached>();

    #region Build

    /// <param name="now">The take time of the running take, or 0. Events that it did not see yet are estimated after it.</param>
    /// <param name="recorded">
    /// When it is set, it is the only source of event times, nothing is estimated, and an item that waits for
    /// an event that is not in it goes to <see cref="Unreached"/>.
    /// </param>
    public static TimelineLayout Build(Take take, IReadOnlyDictionary<string, string> aliases,
        IReadOnlyDictionary<string, double>? lastRun = null, IReadOnlyDictionary<string, double>? live = null, double now = 0.0,
        IReadOnlyDictionary<string, double>? recorded = null, IEnumerable<TimelineMarker>? markers = null)
    {
        var times = new Dictionary<string, TimelineEvent>(StringComparer.Ordinal);
        var unreached = new List<TimelineUnreached>();
        double lastEstimate = double.NaN;
        if (recorded != null)
        {
            lastRun = null;
            live = null;
        }

        TimelineEvent? Resolve(string key, double? hint)
        {
            if (times.TryGetValue(key, out TimelineEvent? known))
                return known;
            TimelineEvent result;
            if (recorded != null)
            {
                if (!recorded.TryGetValue(key, out double at))
                    return null;
                result = new TimelineEvent(key, at, EventTimeSource.Recording);
            }
            else if (live != null && live.TryGetValue(key, out double seen))
                result = new TimelineEvent(key, seen, EventTimeSource.Live);
            else if (lastRun != null && lastRun.TryGetValue(key, out double logged))
                result = new TimelineEvent(key, logged, EventTimeSource.LastRun);
            else
            {
                double floor = Math.Max(double.IsNaN(lastEstimate) ? 0.0 : lastEstimate, Math.Max(now, 0.0)) + EstimateSpacing;
                double estimate = Math.Max(hint ?? floor, floor);
                lastEstimate = estimate;
                result = new TimelineEvent(key, estimate, EventTimeSource.Estimated);
            }

            times[key] = result;
            return result;
        }

        int loopWait = LoopWait(take, aliases);
        var shots = new List<TimelineShot>(take.Shots.Count);
        // Null after an item that never starts, because the item after it never starts either.
        double? previousEnd = 0.0;
        for (int i = 0; i < take.Shots.Count; i++)
        {
            Shot shot = take.Shots[i];
            double? start;
            bool estimated = false;
            switch (shot.At.Kind)
            {
                case TriggerKind.Time:
                    start = shot.At.Time;
                    break;
                case TriggerKind.Event:
                    string key = EventKey(shot.At.Event, aliases);
                    TimelineEvent? at = Resolve(key, previousEnd - shot.At.Delay);
                    if (at == null)
                        unreached.Add(new TimelineUnreached(TimelineItemKind.Shot, i, key));
                    estimated = at?.Estimated ?? false;
                    start = at == null ? null : Math.Max(at.Time + shot.At.Delay, 0.0);
                    break;
                default:
                    start = previousEnd;
                    break;
            }

            if (start is not double begin)
            {
                previousEnd = null;
                continue;
            }

            double duration = Math.Max(shot.Duration, 0.0);
            double blend = shot.Transition.Kind == TransitionKind.Blend && i > 0 ? Math.Max(shot.Transition.Seconds, 0.0) : 0.0;
            shots.Add(new TimelineShot(i, begin, begin + duration, blend, estimated, i < loopWait));
            previousEnd = begin + duration;
        }

        var keys = new List<TimelineKey>(take.Speed.Count);
        previousEnd = 0.0;
        for (int i = 0; i < take.Speed.Count; i++)
        {
            SpeedKey key = take.Speed[i];
            (double? time, bool estimated) = Start(key.At, previousEnd, aliases, Resolve, TimelineItemKind.Key, i, unreached);
            if (time is not double begin)
            {
                previousEnd = null;
                continue;
            }

            double ramp = Math.Max(key.Ramp, 0.0);
            keys.Add(new TimelineKey(i, begin, begin + ramp, estimated));
            previousEnd = begin + ramp;
        }

        // An action has no "after" in a file, and the timeline runner counts one as take time 0.
        var actions = new List<TimelineAction>(take.Actions.Count);
        for (int i = 0; i < take.Actions.Count; i++)
        {
            (double? time, bool estimated) = Start(take.Actions[i].At, 0.0, aliases, Resolve, TimelineItemKind.Action, i, unreached);
            if (time is double begin)
                actions.Add(new TimelineAction(i, begin, estimated));
        }

        // Events that no item names still show, as a reference for the player.
        foreach (var source in new[] { live, lastRun, recorded })
        {
            if (source == null)
                continue;
            foreach (string key in source.Keys.OrderBy(k => source[k]).ThenBy(k => k, StringComparer.Ordinal))
                Resolve(key, null);
        }

        List<TimelineEvent> events = times.Values.OrderBy(e => e.Time).ThenBy(e => e.Key, StringComparer.Ordinal).ToList();
        double end = 1.0;
        foreach (TimelineShot shot in shots)
            end = Math.Max(end, shot.End);
        foreach (TimelineKey key in keys)
            end = Math.Max(end, key.RampEnd);
        foreach (TimelineAction action in actions)
            end = Math.Max(end, action.Time);
        foreach (TimelineEvent e in events)
            end = Math.Max(end, e.Time);
        List<TimelineMarker> marks = markers?.OrderBy(m => m.Time).ToList() ?? new List<TimelineMarker>();
        foreach (TimelineMarker mark in marks)
            end = Math.Max(end, mark.Time);

        return new TimelineLayout
        {
            Shots = shots,
            Keys = keys,
            Actions = actions,
            Events = events,
            Duration = end,
            HasEstimates = events.Any(e => e.Estimated),
            Markers = marks,
            Unreached = unreached,
        };
    }

    private static (double? Time, bool Estimated) Start(Trigger trigger, double? previousEnd,
        IReadOnlyDictionary<string, string> aliases, Func<string, double?, TimelineEvent?> resolve,
        TimelineItemKind kind, int index, List<TimelineUnreached> unreached)
    {
        switch (trigger.Kind)
        {
            case TriggerKind.Time:
                return (trigger.Time, false);
            case TriggerKind.Event:
                string key = EventKey(trigger.Event, aliases);
                TimelineEvent? at = resolve(key, null);
                if (at == null)
                {
                    unreached.Add(new TimelineUnreached(kind, index, key));
                    return (null, false);
                }

                return (Math.Max(at.Time + trigger.Delay, 0.0), at.Estimated);
            default:
                return (previousEnd, false);
        }
    }

    // The index of the shot that ends a pre-roll loop, or -1.
    private static int LoopWait(Take take, IReadOnlyDictionary<string, string> aliases)
    {
        if (take.PreRoll is not { Loop: true } preRoll)
            return -1;
        string until = EventKey(preRoll.Until, aliases);
        for (int i = 1; i < take.Shots.Count; i++)
        {
            Trigger at = take.Shots[i].At;
            if (at.Kind == TriggerKind.Event && EventKey(at.Event, aliases) == until)
                return i;
        }

        return -1;
    }

    /// <summary>A name that does not resolve keeps its trimmed text, so it still shows.</summary>
    public static string EventKey(string text, IReadOnlyDictionary<string, string> aliases) =>
        EventName.TryResolve(text ?? "", aliases, out EventName name, out _) ? name.Canonical : (text ?? "").Trim();

    #endregion

    #region Event log of an earlier run

    /// <summary>Matches the names that <see cref="TakeFolders.FolderName"/> and <see cref="TakeFolders.Unique"/> make.</summary>
    public static IReadOnlyList<string> RunFoldersNewestFirst(IEnumerable<string> folderNames, string takeName)
    {
        var pattern = new Regex(@"^(\d{8}-\d{6})-" + Regex.Escape(TakeFolders.SafeName(takeName)) + @"(?:~(\d+))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var runs = new List<(string Name, string Stamp, int Number)>();
        foreach (string name in folderNames)
        {
            Match match = pattern.Match(name);
            if (!match.Success)
                continue;
            int number = match.Groups[2].Success && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 1;
            runs.Add((name, match.Groups[1].Value, number));
        }

        return runs
            .OrderByDescending(r => r.Stamp, StringComparer.Ordinal)
            .ThenByDescending(r => r.Number)
            .Select(r => r.Name)
            .ToList();
    }

    /// <summary>
    /// Reads an <see cref="EventLogCsv"/> log, and the take time of the first row of each event counts.
    /// <paramref name="finished"/> is true when the log has the row of the take end, so the run did not crash.
    /// </summary>
    public static Dictionary<string, double> ReadEventTimes(TextReader reader, out bool finished)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        finished = false;
        bool header = true;
        foreach (List<string> row in CsvRows(reader))
        {
            if (header)
            {
                header = false;
                continue;
            }

            if (row.Count < 7)
                continue;
            string kind = row[5];
            string name = row[6];
            if (kind == "take" && name == "end")
                finished = true;
            if (kind != "event" || name.Length == 0 || result.ContainsKey(name))
                continue;
            if (double.TryParse(row[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                && !double.IsNaN(seconds) && !double.IsInfinity(seconds))
                result[name] = seconds;
        }

        return result;
    }

    // A quoted field may hold commas, doubled quotes and line breaks.
    private static IEnumerable<List<string>> CsvRows(TextReader reader)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        bool any = false;
        int c;
        while ((c = reader.Read()) >= 0)
        {
            char ch = (char)c;
            any = true;
            if (quoted)
            {
                if (ch != '"')
                    field.Append(ch);
                else if (reader.Peek() == '"')
                {
                    reader.Read();
                    field.Append('"');
                }
                else
                    quoted = false;
                continue;
            }

            switch (ch)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    yield return row;
                    row = new List<string>();
                    any = false;
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        if (any)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }

    #endregion

    #region View math

    public static double SnapDuration(double seconds) =>
        Math.Max(MinDuration, Math.Round(seconds * 10.0, MidpointRounding.AwayFromZero) / 10.0);

    public static double SnapTime(double seconds) =>
        Math.Max(0.0, Math.Round(seconds * 10.0, MidpointRounding.AwayFromZero) / 10.0);

    /// <summary>Keeps <paramref name="anchor"/> under the cursor, and a factor below 1 zooms in.</summary>
    public static (double Start, double Span) ZoomAround(double start, double span, double anchor, double factor,
        double minSpan, double maxSpan)
    {
        double newSpan = Math.Clamp(span * factor, minSpan, Math.Max(minSpan, maxSpan));
        double share = span > 0.0 ? (anchor - start) / span : 0.0;
        double newStart = Math.Max(0.0, anchor - share * newSpan);
        return (newStart, newSpan);
    }

    /// <summary>
    /// <paramref name="recordingEnd"/> is the recording time at the end of the take. A mark after it has no
    /// take time of its own, so it is left out.
    /// </summary>
    public static List<TimelineMarker> MarkersBefore(IEnumerable<(string Label, double RecordingTime, double TakeTime)> marks, double recordingEnd) =>
        marks.Where(m => m.RecordingTime <= recordingEnd + 1e-9 && double.IsFinite(m.TakeTime))
            .Select(m => new TimelineMarker(Math.Max(m.TakeTime, 0.0), m.Label))
            .OrderBy(m => m.Time)
            .ToList();

    public static double ScrubTime(double timeUnderMouse, double duration) =>
        double.IsFinite(timeUnderMouse) ? Math.Clamp(timeUnderMouse, 0.0, Math.Max(duration, 0.0)) : 0.0;

    /// <summary>
    /// The view stays until the play head leaves its first 95 percent, then it turns a page so the play head
    /// is a tenth of the span from the left edge.
    /// </summary>
    public static double FollowStart(double start, double span, double playHead)
    {
        if (!(span > 0.0) || !double.IsFinite(playHead))
            return start;
        if (playHead >= start && playHead <= start + span * 0.95)
            return start;
        return Math.Max(0.0, playHead - span * 0.1);
    }

    private static readonly double[] TickSteps =
        { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200, 1800, 3600 };

    public static double TickStep(double pixelsPerSecond, double minPixels)
    {
        foreach (double step in TickSteps)
        {
            if (step * pixelsPerSecond >= minPixels)
                return step;
        }

        return TickSteps[^1];
    }

    #endregion
}

/// <summary>
/// Compares the inputs by reference. A replay layout does not depend on the play head, so the dock builds it
/// again only after an edit or a new plan.
/// </summary>
public sealed class TimelineLayoutCache
{
    private int _version;
    private object? _plan;
    private object? _take;
    private object? _aliases;
    private TimelineLayout? _layout;

    public TimelineLayout Get(int version, object? plan, object take, object aliases, Func<TimelineLayout> build)
    {
        if (_layout != null && _version == version && ReferenceEquals(_plan, plan) && ReferenceEquals(_take, take)
            && ReferenceEquals(_aliases, aliases))
            return _layout;
        _layout = build();
        _version = version;
        _plan = plan;
        _take = take;
        _aliases = aliases;
        return _layout;
    }

    public void Clear()
    {
        _layout = null;
        _plan = null;
        _take = null;
        _aliases = null;
    }
}
