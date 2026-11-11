namespace CineCam.Director.Replay;

/// <summary>
/// Take time is the time of the video, and the speed track sets how many recording seconds pass per take
/// second. The plan runs the timeline on a fixed grid of take time, as the frames of a live take, so every
/// start is fixed as a live take would fix it. Between two grid steps at which something can change the
/// speed is constant, so the plan crosses them in one update, and the table of the updates maps any take
/// time to recording time.
/// </summary>
public sealed class ReplayTakePlan
{
    public const double DefaultStep = 1.0 / 60.0;

    /// <summary>Bounds the cost of a resolve, because a speed ramp takes one update per grid step.</summary>
    public const int MaxSteps = 720_000;

    /// <summary>One day of video in take seconds.</summary>
    public const double MaxDuration = 86_400.0;

    // The recording time is linear between two updates, because the speed is constant between them.
    private readonly double[]? _times;
    private readonly double[]? _recording;

    private ReplayTakePlan(double recordingDuration, double step, double[]? times, double[]? recording)
    {
        RecordingDuration = recordingDuration;
        Step = step;
        _times = times;
        _recording = recording;
        Duration = recordingDuration;
    }

    /// <summary>Null for a plan without a take, in which the take time is the recording time.</summary>
    public Take? Take { get; private init; }

    public ShotFile? File { get; private init; }

    public double Step { get; }

    /// <summary>Take seconds until the end of the recording, or until the plan stopped.</summary>
    public double Duration { get; private set; }

    public double RecordingDuration { get; }

    /// <summary>True when the plan stopped at <see cref="MaxSteps"/> or <see cref="MaxDuration"/> before the end of the recording.</summary>
    public bool Truncated { get; private set; }

    public int Updates { get; private init; }

    /// <summary>Canonical event names with their take times.</summary>
    public IReadOnlyDictionary<string, double> EventTimes { get; private init; } = new Dictionary<string, double>();

    public TimelineStarts? Starts { get; private set; }

    /// <summary>For each shot the first take time at which it showed, alone or in a blend, or null.</summary>
    public IReadOnlyList<double?> FirstShown { get; private set; } = Array.Empty<double?>();

    /// <summary>Null when no shot uses the flight frame or the plan was not made by <see cref="ReplayTakePlayer.Resolve"/>.</summary>
    public ReplayHeadings? Headings { get; internal set; }

    public bool WantsSeparated { get; private init; }

    public static ReplayTakePlan Identity(double recordingDuration) => new(Math.Max(recordingDuration, 0.0), DefaultStep, null, null);

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when the take has a trigger that the timeline does not accept.
    /// It reads only its arguments, so it can run on a worker thread.
    /// </summary>
    /// <param name="events">Recording times, see <see cref="ReplayEvents.ForTake"/>.</param>
    public static ReplayTakePlan Resolve(Take take, ShotFile file, IReadOnlyList<ReplayEvent> events, double recordingDuration,
        double step = DefaultStep, CancellationToken cancel = default)
    {
        recordingDuration = Math.Max(recordingDuration, 0.0);
        var runner = new TimelineRunner(take, file.Events);
        var sorted = events.Where(e => e.Time <= recordingDuration).OrderBy(e => e.Time).ToList();
        var eventTimes = new Dictionary<string, double>(StringComparer.Ordinal);
        var predictions = new Predictions(take, file);
        foreach (ReplayEvent item in sorted)
            predictions.Expect(item.Name, item.Time);
        var times = new List<double> { 0.0 };
        var table = new List<double> { 0.0 };
        var first = new double?[take.Shots.Count];
        int next = 0, updates = 1;
        long k = 0;
        double time = 0.0, recording = 0.0, end;
        bool truncated = false;

        while (next < sorted.Count && sorted[next].Time <= 0.0)
            Arrive(sorted[next++], 0.0);
        predictions.Move(0.0, 1.0);
        TimelineState state = runner.Update(0.0, eventTimes, predictions.Active);
        MarkShown(first, state, 0.0);

        while (true)
        {
            if (recording >= recordingDuration)
            {
                end = time;
                break;
            }

            if (updates >= MaxSteps || time >= MaxDuration)
            {
                truncated = true;
                end = time;
                break;
            }

            if ((updates & 1023) == 0)
                cancel.ThrowIfCancellationRequested();

            // The speed of the step is the target at its start, because a live take writes the speed of
            // one frame and the sim runs at it until the next frame.
            double speed = state.TargetSpeed > 1e-12 ? state.TargetSpeed : 0.0;
            long n = Ramping(take, runner, time) ? 1
                : StepsAhead(take, runner, predictions, sorted, next, k, time, recording, recordingDuration, speed, step);
            if (n <= 0)
            {
                // The sim stands still and nothing can start any more, so the plan ends when the shot
                // that shows is over, or after one more pass of a pre-roll loop.
                double settle = Settle(take, runner, state, time);
                if (settle > time)
                {
                    times.Add(settle);
                    table.Add(recording);
                    time = settle;
                }

                end = time;
                break;
            }

            n = Math.Min(n, Math.Max(1L, (long)Math.Ceiling((MaxDuration - time) / step)));
            double after = speed > 0.0 ? recording + speed * step * n : recording;
            while (next < sorted.Count && sorted[next].Time <= after)
            {
                ReplayEvent item = sorted[next++];
                Arrive(item, speed > 0.0 ? time + (item.Time - recording) / speed : time);
            }

            double stepped = (k + n) * step;
            if (after >= recordingDuration)
            {
                double last = speed > 0.0 ? time + (recordingDuration - recording) / speed : time;
                after = recordingDuration;
                if (last > time && last < stepped)
                {
                    times.Add(last);
                    table.Add(recordingDuration);
                }
            }

            k += n;
            time = stepped;
            recording = after;
            times.Add(time);
            table.Add(recording);
            predictions.Move(recording, speed);
            state = runner.Update(time, eventTimes, speed > 0.0 ? predictions.Active : null);
            updates++;
            MarkShown(first, state, time);
        }

        double[] timeTable = times.ToArray();
        return new ReplayTakePlan(recordingDuration, step, timeTable, table.ToArray())
        {
            Take = take,
            File = file,
            EventTimes = eventTimes,
            Starts = runner.Starts,
            FirstShown = first,
            Duration = Math.Min(end, timeTable[^1]),
            Truncated = truncated,
            Updates = updates,
            WantsSeparated = take.Shots.Any(s => s.Subject == ShotSubject.Separated && Shot.SupportsAim(s.Type)),
        };

        void Arrive(ReplayEvent item, double at)
        {
            eventTimes.TryAdd(item.Name, at);
            predictions.Arrive(item.Name);
        }
    }

    public double RecordingTime(double takeTime)
    {
        if (_times == null || _recording == null)
            return Math.Clamp(takeTime, 0.0, RecordingDuration);
        if (!(takeTime > 0.0))
            return 0.0;
        int index = Segment(takeTime);
        if (index >= _times.Length - 1)
            return _recording[^1];
        double share = (takeTime - _times[index]) / (_times[index + 1] - _times[index]);
        return _recording[index] + (_recording[index + 1] - _recording[index]) * Math.Clamp(share, 0.0, 1.0);
    }

    /// <summary>The first take time that reaches the recording time, because a still sim maps many take times to one.</summary>
    public double TakeTime(double recordingTime)
    {
        if (_times == null || _recording == null)
            return Math.Clamp(recordingTime, 0.0, Duration);
        if (!(recordingTime > 0.0))
            return 0.0;
        int lo = 0, hi = _recording.Length - 1;
        if (_recording[hi] < recordingTime)
            return Duration;
        while (hi - lo > 1)
        {
            int mid = lo + (hi - lo) / 2;
            if (_recording[mid] < recordingTime) lo = mid;
            else hi = mid;
        }

        double span = _recording[hi] - _recording[lo];
        double share = span > 0.0 ? (recordingTime - _recording[lo]) / span : 1.0;
        return Math.Min(_times[lo] + (_times[hi] - _times[lo]) * share, Duration);
    }

    /// <summary>Recording seconds per take second, the sim speed that the take asks for.</summary>
    public double RecordingRate(double takeTime)
    {
        if (_times == null || _recording == null)
            return 1.0;
        if (_times.Length < 2)
            return 0.0;
        int index = Math.Min(Segment(Math.Max(takeTime, 0.0)), _times.Length - 2);
        return (_recording[index + 1] - _recording[index]) / (_times[index + 1] - _times[index]);
    }

    public TakeDirector? CreateDirector() =>
        Take != null && File != null && Starts != null ? new TakeDirector(Take, File, Starts) : null;

    // The last update at or before a take time.
    private int Segment(double takeTime)
    {
        double[] times = _times!;
        int lo = 0, hi = times.Length - 1;
        if (takeTime >= times[hi])
            return hi;
        while (hi - lo > 1)
        {
            int mid = lo + (hi - lo) / 2;
            if (times[mid] <= takeTime) lo = mid;
            else hi = mid;
        }

        return lo;
    }

    private static void MarkShown(double?[] first, TimelineState state, double time)
    {
        if (state.ShotIndex >= 0)
            first[state.ShotIndex] ??= time;
        if (state.PreviousShotIndex >= 0)
            first[state.PreviousShotIndex] ??= time;
    }

    private static bool Ramping(Take take, TimelineRunner runner, double time)
    {
        for (int i = 0; i < take.Speed.Count; i++)
        {
            if (runner.SpeedKeyStart(i) is double start && start <= time && take.Speed[i].Ramp > 1e-9 && time < start + take.Speed[i].Ramp)
                return true;
        }

        return false;
    }

    // 0 means that nothing can change any more. The later passes of a pre-roll loop only repeat shots
    // that showed already, so they are no reason to stop.
    private static long StepsAhead(Take take, TimelineRunner runner, Predictions predictions, List<ReplayEvent> sorted, int next,
        long k, double time, double recording, double recordingDuration, double speed, double step)
    {
        long best = long.MaxValue;
        for (int i = 0; i < take.Shots.Count; i++)
        {
            if (runner.ShotStart(i) is double start && start > time)
                best = Math.Min(best, GridStep(start, step) - k);
        }

        for (int i = 0; i < take.Speed.Count; i++)
        {
            if (runner.SpeedKeyStart(i) is double start && start > time)
                best = Math.Min(best, GridStep(start, step) - k);
        }

        if (speed > 0.0)
        {
            double perStep = speed * step;
            best = Math.Min(best, StepsTo(recordingDuration - recording, perStep));
            if (next < sorted.Count)
                best = Math.Min(best, StepsTo(sorted[next].Time - recording, perStep));
            best = Math.Min(best, predictions.StepsToDue(recording, speed, perStep));
        }

        return best == long.MaxValue ? 0 : Math.Max(best, 1L);
    }

    // Uses the same product index * step as the resolve, so rounding cannot put a start one step off.
    private static long GridStep(double start, double step)
    {
        double cell = Math.Ceiling(start / step);
        if (cell > 4e15)
            return long.MaxValue / 2;
        long index = (long)cell;
        while (index > 0 && (index - 1) * step >= start)
            index--;
        while (index * step < start)
            index++;
        return index;
    }

    private static long StepsTo(double distance, double perStep)
    {
        double steps = Math.Ceiling(distance / perStep);
        return steps >= 4e15 ? long.MaxValue / 2 : Math.Max((long)steps, 1L);
    }

    private static double Settle(Take take, TimelineRunner runner, TimelineState state, double time)
    {
        int loop = runner.LoopShots;
        if (loop > 0 && runner.ShotStart(loop) == null)
            return time + runner.LoopLength;
        double settle = time;
        if (state.ShotIndex >= 0 && runner.ShotStart(state.ShotIndex) is double start)
        {
            Shot shot = take.Shots[state.ShotIndex];
            settle = Math.Max(settle, start + shot.Duration);
            if (shot.Transition.Kind == TransitionKind.Blend)
                settle = Math.Max(settle, start + shot.Transition.Seconds);
        }

        return settle;
    }

    // Only altitude and speed events that a trigger waits for with a negative delay need a prediction.
    // The view gives the take seconds until each of them from the speed of the last step, as a live take
    // predicts them from the rate of the value, and it allocates nothing per step.
    private sealed class Predictions : IReadOnlyDictionary<string, double>
    {
        private readonly Dictionary<string, List<double>> _delays = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _pending = new(StringComparer.Ordinal);
        private double _recording;
        private double _speed = 1.0;

        public Predictions(Take take, ShotFile file)
        {
            foreach (Trigger trigger in take.Shots.Select(s => s.At).Concat(take.Speed.Select(k => k.At)).Concat(take.Actions.Select(a => a.At)))
            {
                if (trigger.Kind != TriggerKind.Event || trigger.Delay >= 0.0
                    || !EventName.TryResolve(trigger.Event, file.Events, out EventName name, out _) || !name.IsPredictable)
                    continue;
                if (!_delays.TryGetValue(name.Canonical, out List<double>? delays))
                    _delays.Add(name.Canonical, delays = new List<double>());
                delays.Add(trigger.Delay);
            }
        }

        // Null without a predicted event, as the timeline expects.
        public IReadOnlyDictionary<string, double>? Active => _pending.Count > 0 ? this : null;

        public void Expect(string name, double recordingTime)
        {
            if (_delays.ContainsKey(name))
                _pending.TryAdd(name, recordingTime);
        }

        public void Arrive(string name) => _pending.Remove(name);

        public void Move(double recording, double speed)
        {
            _recording = recording;
            _speed = speed;
        }

        // The timeline starts such a trigger once the seconds until the event are no more than its
        // negative delay. The delay is in take seconds, so the speed turns it into recording seconds.
        public long StepsToDue(double recording, double speed, double perStep)
        {
            long best = long.MaxValue;
            foreach ((string name, double eventTime) in _pending)
            {
                foreach (double delay in _delays[name])
                {
                    double distance = eventTime + delay * speed - recording;
                    if (distance > 0.0)
                        best = Math.Min(best, StepsTo(distance, perStep));
                }
            }

            return best;
        }

        public bool TryGetValue(string key, out double value)
        {
            if (_pending.TryGetValue(key, out double eventTime) && _speed > 0.0)
            {
                value = (eventTime - _recording) / _speed;
                return true;
            }

            value = 0.0;
            return false;
        }

        public double this[string key] => TryGetValue(key, out double value) ? value : throw new KeyNotFoundException(key);

        public IEnumerable<string> Keys => _pending.Keys;

        public IEnumerable<double> Values => _pending.Keys.Select(key => this[key]);

        public int Count => _pending.Count;

        public bool ContainsKey(string key) => _pending.ContainsKey(key);

        public IEnumerator<KeyValuePair<string, double>> GetEnumerator() =>
            _pending.Keys.Select(key => new KeyValuePair<string, double>(key, this[key])).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
