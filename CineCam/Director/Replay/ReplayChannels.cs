namespace CineCam.Director.Replay;

/// <summary>
/// Keys are stored only when the value changes. Between two keys the value is linear, holds the earlier
/// key for a step kind, and moves along the shorter arc for a kind that wraps at half a turn.
/// </summary>
public sealed record ReplayChannel(string Kind, List<double> Times, List<float> Values)
{
    /// <summary>Takes recording time and holds the first and the last value outside the keys.</summary>
    public float ValueAt(double time)
    {
        int count = Times.Count;
        if (count == 0)
            return 0f;
        if (!(time > Times[0]))
            return Values[0];
        if (time >= Times[count - 1])
            return Values[count - 1];
        int lo = 0, hi = count - 1;
        while (hi - lo > 1)
        {
            int mid = lo + (hi - lo) / 2;
            if (Times[mid] <= time) lo = mid;
            else hi = mid;
        }
        if (ReplayChannelKind.IsStep(Kind))
            return Values[lo];
        double t = (time - Times[lo]) / (Times[hi] - Times[lo]);
        double change = Values[hi] - Values[lo];
        if (ReplayChannelKind.Wraps(Kind))
            return (float)Math.IEEERemainder(Values[lo] + Math.IEEERemainder(change, Math.Tau) * t, Math.Tau);
        return (float)(Values[lo] + change * t);
    }
}

public static class ReplayChannelKind
{
    /// <summary>Animation time in seconds.</summary>
    public const string Animation = "a";

    /// <summary>The sun tracking angle that a keyframe animation adds, in radians.</summary>
    public const string AnimationTracking = "r";

    /// <summary>The angle of a sun tracker module in radians.</summary>
    public const string Tracker = "s";

    /// <summary>Radians.</summary>
    public const string GimbalY = "y";

    /// <summary>Radians.</summary>
    public const string GimbalZ = "z";

    /// <summary>A fraction from 0 to 1.</summary>
    public const string Heat = "h";

    /// <summary>A fraction from 0 to 1.</summary>
    public const string ThinFilm = "f";

    /// <summary>A fraction from 0 to 1.</summary>
    public const string Wetness = "w";

    /// <summary>1 for on and 0 for off.</summary>
    public const string Light = "l";

    public static bool IsKnown(string? kind) => kind is Animation or AnimationTracking or Tracker or GimbalY or GimbalZ
        or Heat or ThinFilm or Wetness or Light;

    public static bool IsStep(string kind) => kind == Light;

    // SolarTrackingExtension.Update keeps its angle in the range of half a turn with
    // Math.IEEERemainder, so the recorded value jumps by a full turn where the panel turns past it.
    public static bool Wraps(string kind) => kind == AnimationTracking;

    // The stored resolution of a kind. A smaller change than the tolerance writes no key.
    private static double Quantum(string kind) => kind switch
    {
        Animation => 1e-3,
        AnimationTracking or Tracker or GimbalY or GimbalZ => 1e-4,
        Light => 1.0,
        _ => 1e-3,
    };

    private static double Tolerance(string kind) => kind switch
    {
        Heat or ThinFilm or Wetness => 4e-3,
        _ => Quantum(kind) * 0.5,
    };

    public static float Quantize(string kind, float value)
    {
        if (!float.IsFinite(value))
            return 0f;
        if (kind == Light)
            return value > 0.5f ? 1f : 0f;
        double quantum = Quantum(kind);
        return (float)(Math.Round(value / quantum) * quantum);
    }

    public static bool Changed(string kind, float stored, float value) => Math.Abs(value - stored) > Tolerance(kind);
}

/// <summary>
/// The recorder first prepares a tick, which gives the bytes that the tick adds to the file, and commits
/// it only when the tick fits in the size limit.
/// </summary>
public sealed class ReplayChannelWriter
{
    private readonly float[] _lastTick;
    private readonly float[] _pendingTick;
    private readonly List<(int Channel, double Time, float Value, bool Replace)> _pending = new();
    private bool _prepared;
    private double _pendingTime;
    private double _lastTickTime = double.NaN;

    public ReplayChannelWriter(IReadOnlyList<string> kinds)
    {
        foreach (string kind in kinds)
        {
            if (!ReplayChannelKind.IsKnown(kind))
                throw new ArgumentException($"Unknown channel kind {kind}.", nameof(kinds));
        }
        Channels = kinds.Select(kind => new ReplayChannel(kind, new List<double>(), new List<float>())).ToList();
        _lastTick = new float[kinds.Count];
        _pendingTick = new float[kinds.Count];
    }

    public List<ReplayChannel> Channels { get; }

    /// <summary>
    /// When a value starts to change after it was flat, the flat value is also stored at the time of the
    /// previous tick, so the replay does not ramp over the flat time.
    /// </summary>
    public long Prepare(double time, ReadOnlySpan<float> values)
    {
        if (values.Length != Channels.Count)
            throw new ArgumentException("The number of values differs from the number of channels.", nameof(values));
        _pending.Clear();
        double stored = Math.Round(time, 4);
        float[] tick = _pendingTick;
        bool first = Channels.Count > 0 && Channels[0].Times.Count == 0;
        long bytes = 0;
        for (int i = 0; i < values.Length; i++)
        {
            ReplayChannel channel = Channels[i];
            float value = ReplayChannelKind.Quantize(channel.Kind, values[i]);
            tick[i] = value;
            int count = channel.Times.Count;
            if (count == 0)
            {
                _pending.Add((i, stored, value, false));
                continue;
            }
            float last = channel.Values[count - 1];
            if (!ReplayChannelKind.Changed(channel.Kind, last, value))
                continue;
            double previous = Math.Round(_lastTickTime, 4);
            if (!ReplayChannelKind.IsStep(channel.Kind) && _lastTick[i] == last && previous > channel.Times[count - 1] && previous < stored)
            {
                _pending.Add((i, previous, last, false));
                bytes += ReplayFile.MeasureNumber(previous) + ReplayFile.MeasureNumber(last);
            }
            bool replace = !(stored > channel.Times[count - 1]);
            _pending.Add((i, stored, value, replace));
            if (!replace)
                bytes += ReplayFile.MeasureNumber(stored) + ReplayFile.MeasureNumber(value);
        }

        if (first)
        {
            var preview = Channels.Select((channel, i) => channel with { Times = [stored], Values = [tick[i]] }).ToList();
            bytes = ReplayFile.MeasureChannels(preview);
        }
        _prepared = true;
        _pendingTime = time;
        return bytes;
    }

    public void Commit()
    {
        if (!_prepared)
            return;
        foreach ((int index, double time, float value, bool replace) in _pending)
        {
            ReplayChannel channel = Channels[index];
            if (replace)
            {
                channel.Values[^1] = value;
            }
            else
            {
                channel.Times.Add(time);
                channel.Values.Add(value);
            }
        }
        _pendingTick.CopyTo(_lastTick, 0);
        _lastTickTime = _pendingTime;
        _pending.Clear();
        _prepared = false;
    }
}

/// <summary>
/// The replay compares this key with the key of the decoded part tree, so the values of one part never go
/// to another part that has the same kinds of channels.
/// </summary>
public static class ReplayPartKey
{
    /// <summary>The FNV-1a hash of the part ids and the channel kinds, in channel order.</summary>
    public static uint Of(IEnumerable<(string Part, string Kind)> channels)
    {
        uint hash = 2166136261;
        foreach ((string part, string kind) in channels)
        {
            Add(part);
            Add(kind);
        }
        return hash;

        void Add(string text)
        {
            foreach (char c in text)
            {
                hash = (hash ^ c) * 16777619;
            }
            hash = (hash ^ 0xFFFF) * 16777619;
        }
    }
}
