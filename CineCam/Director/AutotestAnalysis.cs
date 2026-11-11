using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CineCam.Director;

/// <summary>One line of the event log of a take.</summary>
public sealed record CsvRow(double RealSeconds, double GameSeconds, double MissionSeconds, double SimSpeed,
    long Frame, string Kind, string Name, string Detail);

/// <summary>What the test reads in one frame while a take runs.</summary>
public sealed record AutotestSample(double TakeSeconds, double GameSeconds, double SimSpeed, double TargetSpeed, bool SpeedActive);

/// <summary>The camera and game values that a take must give back.</summary>
public sealed record ViewState(string Mode, string Following, double FovDegrees, double SimSpeed, bool DrawUi);

public sealed record AutotestCheck(string Name, bool Pass, string Reason);

/// <summary>Reads the event log and judges a take. The code uses no game type.</summary>
public static class AutotestAnalysis
{
    public static List<CsvRow> ParseCsv(string text)
    {
        var rows = new List<CsvRow>();
        List<List<string>> records = SplitRecords(text);
        for (int i = 1; i < records.Count; i++)
        {
            List<string> f = records[i];
            if (f.Count < 8)
                continue;
            rows.Add(new CsvRow(D(f[0]), D(f[1]), D(f[2]), D(f[3]), f[4].Length == 0 ? -1 : long.Parse(f[4], CultureInfo.InvariantCulture),
                f[5], f[6], f[7]));
        }

        return rows;
    }

    private static double D(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    private static List<List<string>> SplitRecords(string text)
    {
        var records = new List<List<string>>();
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                fields.Add(field.ToString());
                field.Clear();
                if (fields.Count > 1 || fields[0].Length > 0)
                    records.Add(fields);
                fields = new List<string>();
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(fields);
        }

        return records;
    }

    public static double? EventTime(IEnumerable<CsvRow> rows, string name) =>
        rows.Where(r => r.Kind == "event" && r.Name == name).Select(r => (double?)r.RealSeconds).FirstOrDefault();

    public static string EndReason(IEnumerable<CsvRow> rows) =>
        rows.LastOrDefault(r => r.Kind == "take" && r.Name == "end")?.Detail ?? "";

    /// <summary>Every named event is in the log, and an event that depends on another comes after it.</summary>
    public static AutotestCheck EventsSeen(IReadOnlyList<CsvRow> rows, IEnumerable<string> expected)
    {
        var missing = new List<string>();
        var parts = new List<string>();
        foreach (string name in expected)
        {
            double? t = EventTime(rows, name);
            if (t == null)
                missing.Add(name);
            else
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{name} at {t:F2} s"));
        }

        double? ignition = EventTime(rows, "ignition");
        double? liftoff = EventTime(rows, "liftoff");
        if (ignition != null && liftoff != null && liftoff < ignition)
            missing.Add("order (liftoff before ignition)");
        return missing.Count == 0
            ? new AutotestCheck("events seen", true, string.Join(", ", parts))
            : new AutotestCheck("events seen", false, "missing: " + string.Join(", ", missing) + ". Seen: " + string.Join(", ", parts));
    }

    /// <summary>Each shot started once, near the time that the test expects.</summary>
    public static AutotestCheck ShotsStarted(IReadOnlyList<CsvRow> rows, IReadOnlyList<double?> expectedStart, double tolerance)
    {
        var problems = new List<string>();
        var parts = new List<string>();
        for (int i = 0; i < expectedStart.Count; i++)
        {
            CsvRow[] found = rows.Where(r => r.Kind == "shot" && r.Name == $"camera[{i}]").ToArray();
            if (found.Length != 1)
            {
                problems.Add($"camera[{i}] started {found.Length} times");
                continue;
            }

            parts.Add(string.Create(CultureInfo.InvariantCulture, $"camera[{i}] at {found[0].RealSeconds:F2} s"));
            if (expectedStart[i] is double expected && Math.Abs(found[0].RealSeconds - expected) > tolerance)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"camera[{i}] at {found[0].RealSeconds:F2} s, expected {expected:F2} s"));
        }

        return problems.Count == 0
            ? new AutotestCheck("shots started", true, string.Join(", ", parts))
            : new AutotestCheck("shots started", false, string.Join("; ", problems));
    }

    /// <summary>
    /// The sim speed that the game reports is the target of the speed track in nearly every frame
    /// and reaches the value of each key. A key is judged in the window after its ramp.
    /// </summary>
    public static AutotestCheck SimSpeedFollowsTarget(IReadOnlyList<AutotestSample> samples, IReadOnlyList<CsvRow> rows)
    {
        AutotestSample[] active = samples.Where(s => s.SpeedActive).ToArray();
        if (active.Length == 0)
            return new AutotestCheck("sim speed", false, "the speed track never became active");
        int off = active.Count(s => Math.Abs(s.SimSpeed - s.TargetSpeed) > 0.02);
        var plateaus = new List<string>();
        var problems = new List<string>();
        CsvRow[] keys = rows.Where(r => r.Kind == "speed").OrderBy(r => r.RealSeconds).ToArray();
        for (int k = 0; k < keys.Length; k++)
        {
            double to = Detail(keys[k].Detail, "to");
            double ramp = Detail(keys[k].Detail, "ramp");
            double from = keys[k].RealSeconds + ramp + 0.3;
            double until = k + 1 < keys.Length ? keys[k + 1].RealSeconds : double.MaxValue;
            AutotestSample[] window = active.Where(s => s.TakeSeconds >= from && s.TakeSeconds < until).ToArray();
            if (window.Length == 0)
            {
                plateaus.Add(string.Create(CultureInfo.InvariantCulture, $"{keys[k].Name} has no plateau"));
                continue;
            }

            AutotestSample mid = window[window.Length / 2];
            plateaus.Add(string.Create(CultureInfo.InvariantCulture, $"{keys[k].Name}: target {to:0.###}, observed {mid.SimSpeed:0.###} at {mid.TakeSeconds:F1} s"));
            if (Math.Abs(mid.SimSpeed - to) > 0.03)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{keys[k].Name} reached {mid.SimSpeed:0.###}, not {to:0.###}"));
        }

        if (off > active.Length * 0.02)
            problems.Add($"{off} of {active.Length} frames differ from the target by more than 0.02");
        string reason = $"{active.Length - off} of {active.Length} frames match; " + string.Join("; ", plateaus);
        return problems.Count == 0
            ? new AutotestCheck("sim speed", true, reason)
            : new AutotestCheck("sim speed", false, string.Join("; ", problems) + ". " + reason);
    }

    private static double Detail(string detail, string key)
    {
        foreach (string part in detail.Split(';'))
        {
            string p = part.Trim();
            if (p.StartsWith(key + "=", StringComparison.Ordinal)
                && double.TryParse(p.AsSpan(key.Length + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                return value;
        }

        return 0.0;
    }

    // The number of frames after a repeated frame that are not judged, because the game time runs
    // behind until the sim step has grown back.
    private const int RecoveryFrames = 12;

    /// <summary>
    /// In the offline mode the take time moves by exactly one picture step per picture, and the
    /// game time moves by that step times the sim speed while the speed holds still.
    /// </summary>
    public static AutotestCheck FrameSteps(IReadOnlyList<AutotestSample> samples, double fps)
    {
        double step = 1.0 / fps;
        int pictures = 0, retries = 0, badTake = 0, steady = 0, steadyBad = 0;
        var ratios = new List<double>();
        long lastRetry = long.MinValue / 2;
        for (int i = 1; i < samples.Count; i++)
        {
            double takeStep = samples[i].TakeSeconds - samples[i - 1].TakeSeconds;
            if (Math.Abs(takeStep) < 1e-6)
            {
                retries++;
                lastRetry = i;
                continue;
            }

            if (Math.Abs(takeStep - step) > 1e-6)
            {
                badTake++;
                continue;
            }

            pictures++;
            // A repeated frame has a tiny sim step, and the game lets the step only double from frame to
            // frame, so the frames after it run behind.
            if (i - lastRetry <= RecoveryFrames || i < 2 || !samples[i - 1].SpeedActive || !samples[i - 2].SpeedActive)
                continue;
            double speed = samples[i - 1].SimSpeed;
            if (Math.Abs(speed - samples[i - 2].SimSpeed) > 1e-6 || speed <= 0.0)
                continue;
            double ratio = (samples[i].GameSeconds - samples[i - 1].GameSeconds) / (takeStep * speed);
            ratios.Add(ratio);
            steady++;
            if (Math.Abs(ratio - 1.0) > 0.25)
                steadyBad++;
        }

        ratios.Sort();
        double median = ratios.Count == 0 ? double.NaN : ratios[ratios.Count / 2];
        string reason = string.Create(CultureInfo.InvariantCulture,
            $"{pictures} picture steps of {step:F4} s, {retries} repeated frames, {badTake} wrong take steps; game time per take time at a steady speed: median {median:F3}, {steadyBad} of {steady} frames off by more than 25 percent");
        bool pass = pictures > 0 && badTake == 0 && steadyBad <= steady * 0.05;
        return new AutotestCheck("frame time steps", pass, reason);
    }

    /// <summary>The numbered pictures exist and match the log.</summary>
    public static AutotestCheck FrameCount(int files, long lastFrameInLog, double endSeconds, double fps, int minimum)
    {
        long expected = (long)Math.Round(endSeconds * fps);
        bool pass = files >= minimum && files == lastFrameInLog && Math.Abs(files - expected) <= 1;
        return new AutotestCheck("frame count", pass,
            string.Create(CultureInfo.InvariantCulture, $"{files} picture files, the log ends at picture {lastFrameInLog}, the end time {endSeconds:F2} s gives {expected}, at least {minimum} are needed"));
    }

    /// <summary>The take gave the camera, the speed and the interface back.</summary>
    public static AutotestCheck Restored(ViewState before, ViewState after)
    {
        var problems = new List<string>();
        if (before.Mode != after.Mode)
            problems.Add($"camera mode {before.Mode} became {after.Mode}");
        if (before.Following != after.Following)
            problems.Add($"follow target {before.Following} became {after.Following}");
        if (Math.Abs(before.FovDegrees - after.FovDegrees) > 0.05)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"field of view {before.FovDegrees:F2} became {after.FovDegrees:F2}"));
        if (Math.Abs(before.SimSpeed - after.SimSpeed) > 1e-3)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"sim speed {before.SimSpeed:0.###} became {after.SimSpeed:0.###}"));
        if (before.DrawUi != after.DrawUi)
            problems.Add($"DrawUI {before.DrawUi} became {after.DrawUi}");
        string state = string.Create(CultureInfo.InvariantCulture,
            $"mode {after.Mode}, follow {after.Following}, fov {after.FovDegrees:F2}, speed {after.SimSpeed:0.###}, DrawUI {after.DrawUi}");
        return problems.Count == 0
            ? new AutotestCheck("values restored", true, state)
            : new AutotestCheck("values restored", false, string.Join("; ", problems));
    }

    public static string ToJson(object report) =>
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}
