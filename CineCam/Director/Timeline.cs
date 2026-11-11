namespace CineCam.Director;

public sealed class TimelineState
{
    /// <summary>-1 when no shot has started yet.</summary>
    public int ShotIndex { get; init; } = -1;

    /// <summary>Seconds since the active shot started, limited to its duration.</summary>
    public double LocalTime { get; init; }

    /// <summary>Progress before the ease, 0 to 1.</summary>
    public double RawProgress { get; init; }

    /// <summary>Progress after the ease, 0 to 1. A shot that is over holds 1.</summary>
    public double Progress { get; init; }

    /// <summary>The shot to blend from, or -1 for a cut.</summary>
    public int PreviousShotIndex { get; init; } = -1;

    public double PreviousProgress { get; init; }

    /// <summary>0 shows the previous shot, 1 shows the active shot.</summary>
    public double BlendWeight { get; init; } = 1.0;

    public double TargetSpeed { get; init; } = 1.0;

    /// <summary>True when every shot has started and the last one is over.</summary>
    public bool Finished { get; init; }

    /// <summary>Shots whose start was reached for the first time in this update.</summary>
    public IReadOnlyList<int> StartedShots { get; init; } = Array.Empty<int>();

    public IReadOnlyList<int> StartedSpeedKeys { get; init; } = Array.Empty<int>();

    public IReadOnlyList<int> StartedActions { get; init; } = Array.Empty<int>();
}

/// <summary>
/// Starts in take seconds, and null for a start that waits for its trigger. The starts of the shots of a
/// pre-roll loop move with each pass, so the runner computes them again from the take time.
/// </summary>
public sealed record TimelineStarts(double?[] Shots, double?[] Speed, double?[] Actions);

/// <summary>
/// Plays a take. It is stateful, because the start of a shot or speed key that waits for an event is
/// fixed when the event comes and must not move afterward. Call <see cref="Update"/> once per frame
/// with a take time that does not go back.
/// </summary>
/// <remarks>
/// A negative delay works only for altitude and speed events, which the caller predicts. The start is
/// then fixed on the frame in which the predicted time is reached, or in which the event comes first.
/// <para>
/// The start of each pre-roll loop shot moves to its latest pass, so the active shot and the shot to
/// blend from follow from the starts as for any other shot.
/// </para>
/// </remarks>
public sealed class TimelineRunner
{
    private readonly Take _take;
    private readonly EventName?[] _shotEvents;
    private readonly EventName?[] _speedEvents;
    private readonly EventName?[] _actionEvents;
    private readonly double?[] _shotStart;
    private readonly double?[] _speedStart;
    private readonly double?[] _actionStart;
    private readonly bool[] _shotAnnounced;
    private readonly bool[] _speedAnnounced;
    private readonly bool[] _actionAnnounced;
    private readonly int _loopWait = -1;
    private readonly double _loopOrigin;
    private readonly double _loopLength;
    private readonly double[] _loopOffsets = Array.Empty<double>();

    public TimelineRunner(Take take, IReadOnlyDictionary<string, string> aliases)
    {
        _take = take;
        _shotEvents = new EventName?[take.Shots.Count];
        _speedEvents = new EventName?[take.Speed.Count];
        for (int i = 0; i < take.Shots.Count; i++)
            _shotEvents[i] = ResolveEvent(take.Shots[i].At, aliases, $"camera[{i}].at");
        for (int i = 0; i < take.Speed.Count; i++)
            _speedEvents[i] = ResolveEvent(take.Speed[i].At, aliases, $"speed[{i}].at");
        _actionEvents = new EventName?[take.Actions.Count];
        for (int i = 0; i < take.Actions.Count; i++)
            _actionEvents[i] = ResolveEvent(take.Actions[i].At, aliases, $"actions[{i}].at");

        _shotStart = new double?[take.Shots.Count];
        _speedStart = new double?[take.Speed.Count];
        _shotAnnounced = new bool[take.Shots.Count];
        _speedAnnounced = new bool[take.Speed.Count];
        _actionStart = new double?[take.Actions.Count];
        _actionAnnounced = new bool[take.Actions.Count];

        if (take.PreRoll is { Loop: true } preRoll)
        {
            _loopWait = FindLoopWait(take, preRoll, aliases);
            _loopOffsets = new double[_loopWait];
            _loopOrigin = take.Shots[0].At.Kind == TriggerKind.Time ? take.Shots[0].At.Time : 0.0;
            double offset = 0.0;
            for (int j = 0; j < _loopWait; j++)
            {
                _loopOffsets[j] = offset;
                offset += take.Shots[j].Duration;
            }

            _loopLength = offset;
        }
    }

    // The loop ends at the first shot that waits for the event. The shots before it must follow each
    // other without a trigger of their own, because a pass of the loop has no fixed time to trigger on.
    private int FindLoopWait(Take take, PreRoll preRoll, IReadOnlyDictionary<string, string> aliases)
    {
        if (!EventName.TryResolve(preRoll.Until, aliases, out EventName until, out string error))
            throw new ArgumentException($"preRoll.until: {error}");
        for (int i = 0; i < take.Shots.Count; i++)
        {
            if (_shotEvents[i] is not EventName name || name.Canonical != until.Canonical)
                continue;
            if (i == 0)
                throw new ArgumentException($"preRoll.until: the first shot waits for \"{preRoll.Until}\", so no shot is left to loop");
            for (int j = 0; j < i; j++)
            {
                TriggerKind kind = take.Shots[j].At.Kind;
                if (kind == TriggerKind.Event || (j > 0 && kind == TriggerKind.Time))
                    throw new ArgumentException($"camera[{j}].at: a shot of the pre-roll loop starts after the one before (only the first may start at a time)");
            }

            return i;
        }

        throw new ArgumentException($"preRoll.until: no shot starts on the event \"{preRoll.Until}\", so the loop would never end");
    }

    public Take Take => _take;

    public int LoopShots => Math.Max(_loopWait, 0);

    /// <summary>In take seconds.</summary>
    public double LoopLength => _loopLength;

    /// <summary>In take seconds, or null while the shot waits for its trigger.</summary>
    public double? ShotStart(int index) => _shotStart[index];

    public double? SpeedKeyStart(int index) => _speedStart[index];

    public double? ActionStart(int index) => _actionStart[index];

    public bool ActionStarted(int index) => _actionAnnounced[index];

    public TimelineStarts Starts => new((double?[])_shotStart.Clone(), (double?[])_speedStart.Clone(), (double?[])_actionStart.Clone());

    /// <summary>A start that is null still waits for its trigger. No start counts as reported yet.</summary>
    public void Preset(TimelineStarts starts)
    {
        if (starts.Shots.Length != _shotStart.Length || starts.Speed.Length != _speedStart.Length || starts.Actions.Length != _actionStart.Length)
            throw new ArgumentException("the starts belong to another take");
        Reset();
        Array.Copy(starts.Shots, _shotStart, _shotStart.Length);
        Array.Copy(starts.Speed, _speedStart, _speedStart.Length);
        Array.Copy(starts.Actions, _actionStart, _actionStart.Length);
    }

    public void Reset()
    {
        Array.Clear(_shotStart);
        Array.Clear(_speedStart);
        Array.Clear(_shotAnnounced);
        Array.Clear(_speedAnnounced);
        Array.Clear(_actionStart);
        Array.Clear(_actionAnnounced);
    }

    /// <param name="eventTimes">Canonical event names and the take time at which each was seen.</param>
    /// <param name="predictions">Canonical names of altitude and speed events and the seconds until each, from now.</param>
    public TimelineState Update(double takeTime,
        IReadOnlyDictionary<string, double> eventTimes,
        IReadOnlyDictionary<string, double>? predictions = null)
    {
        for (int i = 0; i < _take.Shots.Count; i++)
        {
            if (_shotStart[i].HasValue || i < _loopWait)
                continue;
            Shot shot = _take.Shots[i];
            double? previousEnd = i == 0 ? 0.0
                : _shotStart[i - 1] + _take.Shots[i - 1].Duration;
            _shotStart[i] = ResolveStart(shot.At, _shotEvents[i], previousEnd, takeTime, eventTimes, predictions);
        }

        if (_loopWait >= 0)
            UpdateLoop(takeTime);

        for (int i = 0; i < _take.Speed.Count; i++)
        {
            if (_speedStart[i].HasValue)
                continue;
            SpeedKey key = _take.Speed[i];
            double? previousEnd = i == 0 ? 0.0
                : _speedStart[i - 1] + _take.Speed[i - 1].Ramp;
            _speedStart[i] = ResolveStart(key.At, _speedEvents[i], previousEnd, takeTime, eventTimes, predictions);
        }

        // A trigger of the kind "after" cannot come from a file for an action, and it counts as take time 0.
        for (int i = 0; i < _take.Actions.Count; i++)
        {
            if (_actionStart[i].HasValue)
                continue;
            _actionStart[i] = ResolveStart(_take.Actions[i].At, _actionEvents[i], 0.0, takeTime, eventTimes, predictions);
        }

        IReadOnlyList<int> startedShots = Announce(_shotStart, _shotAnnounced, takeTime);
        IReadOnlyList<int> startedKeys = Announce(_speedStart, _speedAnnounced, takeTime);
        IReadOnlyList<int> startedActions = Announce(_actionStart, _actionAnnounced, takeTime);

        double speed = EvaluateSpeed(takeTime);
        bool finished = Finished(takeTime);

        int active = ActiveShot(takeTime);
        if (active < 0)
        {
            return new TimelineState
            {
                TargetSpeed = speed,
                Finished = finished,
                StartedShots = startedShots,
                StartedSpeedKeys = startedKeys,
                StartedActions = startedActions,
            };
        }

        Shot current = _take.Shots[active];
        double activeStart = _shotStart[active]!.Value;
        double local = Math.Clamp(takeTime - activeStart, 0.0, current.Duration);
        double raw = local / current.Duration;

        int previous = PreviousShot(active, takeTime);
        double weight = 1.0;
        double previousProgress = 0.0;
        if (previous >= 0)
        {
            Shot before = _take.Shots[previous];
            previousProgress = Easing.Apply(before.Ease,
                Math.Clamp(takeTime - _shotStart[previous]!.Value, 0.0, before.Duration) / before.Duration);
            if (current.Transition.Kind == TransitionKind.Blend && local < current.Transition.Seconds)
                weight = Easing.Smootherstep((takeTime - activeStart) / current.Transition.Seconds);
            else
                previous = -1;
        }

        return new TimelineState
        {
            ShotIndex = active,
            LocalTime = local,
            RawProgress = raw,
            Progress = Easing.Apply(current.Ease, raw),
            PreviousShotIndex = previous,
            PreviousProgress = previousProgress,
            BlendWeight = weight,
            TargetSpeed = speed,
            Finished = finished,
            StartedShots = startedShots,
            StartedSpeedKeys = startedKeys,
            StartedActions = startedActions,
        };
    }

    // A pass of the loop ends where the waiting shot takes over, even when its duration is longer.
    private double ShotEnd(int index, double start)
    {
        double end = start + _take.Shots[index].Duration;
        if (index < _loopWait && _shotStart[_loopWait] is double waitStart)
            end = Math.Min(end, waitStart);
        return end;
    }

    // Once the waiting shot has started, the passes stop at its start, so the shot that was active then
    // stays the one that it blends from.
    private void UpdateLoop(double takeTime)
    {
        double? waitStart = _shotStart[_loopWait];
        bool frozen = waitStart.HasValue && waitStart.Value <= takeTime;
        double at = frozen ? waitStart!.Value : takeTime;
        for (int j = 0; j < _loopWait; j++)
        {
            double start = LoopStart(j, at);

            // A loop shot whose first pass had not started when the event came never starts, so it
            // cannot cut in on the waiting shot later.
            if (frozen && start > at)
                start = double.PositiveInfinity;
            if (_shotStart[j] is double old && old != start)
                _shotAnnounced[j] = false;
            _shotStart[j] = start;
        }
    }

    private double LoopStart(int j, double takeTime)
    {
        double first = _loopOrigin + _loopOffsets[j];
        if (takeTime <= first)
            return first;
        return first + Math.Floor((takeTime - first) / _loopLength) * _loopLength;
    }

    private int ActiveShot(double takeTime)
    {
        int best = -1;
        for (int i = 0; i < _shotStart.Length; i++)
        {
            if (_shotStart[i] is not double start || start > takeTime)
                continue;
            if (best < 0 || start >= _shotStart[best]!.Value)
                best = i;
        }

        return best;
    }

    private int PreviousShot(int active, double takeTime)
    {
        double activeStart = _shotStart[active]!.Value;
        int best = -1;
        for (int i = 0; i < _shotStart.Length; i++)
        {
            if (i == active || _shotStart[i] is not double start || start > takeTime || start > activeStart)
                continue;
            if (best < 0 || start >= _shotStart[best]!.Value)
                best = i;
        }

        return best;
    }

    private static IReadOnlyList<int> Announce(double?[] starts, bool[] announced, double takeTime)
    {
        List<int>? started = null;
        for (int i = 0; i < starts.Length; i++)
        {
            if (!announced[i] && starts[i] <= takeTime)
            {
                announced[i] = true;
                (started ??= new List<int>()).Add(i);
            }
        }

        return started ?? (IReadOnlyList<int>)Array.Empty<int>();
    }

    private bool Finished(double takeTime)
    {
        double last = double.NegativeInfinity;
        for (int i = 0; i < _shotStart.Length; i++)
        {
            if (_shotStart[i] is not double start)
                return false;
            last = Math.Max(last, ShotEnd(i, start));
        }

        return _shotStart.Length > 0 && takeTime >= last;
    }

    private double EvaluateSpeed(double takeTime)
    {
        // The started keys in the order of their start, then of their index. A stable insertion sort
        // on the stack avoids an allocation per frame.
        int count = 0;
        Span<int> started = _speedStart.Length <= 64 ? stackalloc int[_speedStart.Length] : new int[_speedStart.Length];
        for (int i = 0; i < _speedStart.Length; i++)
        {
            if (_speedStart[i] is not double start || start > takeTime)
                continue;
            int at = count++;
            while (at > 0 && _speedStart[started[at - 1]]!.Value > start)
            {
                started[at] = started[at - 1];
                at--;
            }

            started[at] = i;
        }

        double speed = 1.0;
        for (int k = 0; k < count; k++)
        {
            SpeedKey key = _take.Speed[started[k]];
            double start = _speedStart[started[k]]!.Value;
            double until = k + 1 < count ? _speedStart[started[k + 1]]!.Value : takeTime;
            double elapsed = Math.Min(until, takeTime) - start;
            double t = key.Ramp <= 1e-9 ? 1.0 : Math.Clamp(elapsed / key.Ramp, 0.0, 1.0);
            speed += (key.To - speed) * Easing.Apply(key.Ease, t);
        }

        return speed;
    }

    private static double? ResolveStart(Trigger trigger, EventName? eventName, double? previousEnd, double now,
        IReadOnlyDictionary<string, double> eventTimes, IReadOnlyDictionary<string, double>? predictions)
    {
        switch (trigger.Kind)
        {
            case TriggerKind.After:
                return previousEnd;
            case TriggerKind.Time:
                return trigger.Time;
            default:
                string name = eventName!.Value.Canonical;
                if (eventTimes.TryGetValue(name, out double seen))
                {
                    double start = seen + trigger.Delay;
                    return trigger.Delay < 0.0 ? Math.Max(start, now) : start;
                }

                if (trigger.Delay < 0.0 && eventName.Value.IsPredictable
                    && predictions != null && predictions.TryGetValue(name, out double seconds))
                {
                    double predicted = now + seconds + trigger.Delay;
                    if (predicted <= now)
                        return now;
                }

                return null;
        }
    }

    private static EventName? ResolveEvent(Trigger trigger, IReadOnlyDictionary<string, string> aliases, string path)
    {
        if (trigger.Kind != TriggerKind.Event)
            return null;
        if (!EventName.TryResolve(trigger.Event, aliases, out EventName name, out string error))
            throw new ArgumentException($"{path}: {error}");
        return name;
    }
}
