using System.Globalization;

namespace CineCam.Director.Replay;

public enum ReplayRenderRange
{
    Whole,

    FromCurrent,
}

/// <summary>
/// Times are take seconds. Picture k (from 0) shows <c>Start + k / Fps</c>, computed from k and not
/// added up, so no error builds up over a long render.
/// </summary>
public sealed class ReplayRenderJob
{
    // A take time that is less than this before a picture time still counts as that picture.
    private const double Tolerance = 1e-9;

    public ReplayRenderJob(double start, double end, double fps)
    {
        if (!double.IsFinite(fps) || fps <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(fps), "The pictures per second must be above 0.");
        if (!double.IsFinite(start) || !double.IsFinite(end) || end < start)
            throw new ArgumentOutOfRangeException(nameof(end), "The end of a render must not be before its start.");
        Start = start;
        End = end;
        Fps = fps;
        Total = Count(end - start, fps);
    }

    public double Start { get; }

    /// <summary>No picture shows this time or a later one.</summary>
    public double End { get; }

    public double Fps { get; }

    /// <summary>At least one.</summary>
    public int Total { get; }

    public double VideoDuration => Total / Fps;

    public static bool TryCreate(ReplayRenderRange range, double current, double duration, double fps,
        out ReplayRenderJob? job, out string reason)
    {
        job = null;
        reason = "";
        if (!double.IsFinite(fps) || fps <= 0.0)
        {
            reason = "The pictures per second must be above 0.";
            return false;
        }

        if (!double.IsFinite(duration) || duration <= 0.0)
        {
            reason = "The take has no time on this recording.";
            return false;
        }

        double start = range == ReplayRenderRange.FromCurrent && double.IsFinite(current) ? Math.Clamp(current, 0.0, duration) : 0.0;
        if (duration - start < 0.5 / fps)
        {
            reason = "The replay is at the end of the take. Move it back or render the whole take.";
            return false;
        }

        job = new ReplayRenderJob(start, duration, fps);
        return true;
    }

    /// <summary>One picture for each started picture step.</summary>
    public static int Count(double seconds, double fps)
    {
        double steps = Math.Ceiling(seconds * fps - Tolerance);
        return (int)Math.Clamp(steps, 1.0, int.MaxValue);
    }

    public double TakeTime(int index) => Math.Min(Start + index / Fps, End);

    public double VideoSeconds(int index) => index / Fps;

    /// <summary>The first picture that shows the video time or a later one, counted from 1 as in the file names.</summary>
    public static int PictureAt(double videoSeconds, double fps)
    {
        if (!(videoSeconds > 0.0))
            return 1;
        return (int)Math.Min(Math.Ceiling(videoSeconds * fps - Tolerance), int.MaxValue - 1) + 1;
    }
}

public readonly record struct ReplayRenderProgress(int Done, int Total, double ElapsedSeconds)
{
    public double Fraction => Total <= 0 ? 0.0 : Math.Clamp((double)Done / Total, 0.0, 1.0);

    public double Percent => 100.0 * Fraction;

    /// <summary>At the mean rate so far, or null before the first picture.</summary>
    public double? SecondsLeft => Done <= 0 || !(ElapsedSeconds >= 0.0) ? null
        : ElapsedSeconds / Done * Math.Max(Total - Done, 0);

    public string Text()
    {
        string left = SecondsLeft is double seconds ? $", about {Span(seconds)} left" : "";
        return string.Create(CultureInfo.InvariantCulture, $"picture {Done} of {Total} ({Math.Floor(Percent):0}%){left}");
    }

    public static bool PassedTenth(int before, int after, int total) =>
        total > 0 && after > before && (long)after * 10 / total != (long)before * 10 / total;

    public static string Span(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0.0)
            return "?";
        long total = (long)Math.Round(seconds);
        if (total < 60)
            return string.Create(CultureInfo.InvariantCulture, $"{total} s");
        if (total < 3600)
            return string.Create(CultureInfo.InvariantCulture, $"{total / 60} min {total % 60:00} s");
        return string.Create(CultureInfo.InvariantCulture, $"{total / 3600} h {total / 60 % 60:00} min");
    }
}

/// <summary>The video seconds go to the column realSeconds.</summary>
public sealed record ReplayRenderRow(double VideoSeconds, double TakeSeconds, int Picture, string Kind, string Name, string Detail);

/// <summary>
/// Writes the format of the log of a live take, so the marker tool works on it. The event rows come from
/// the exact event times of the plan, the shot and speed rows from the frames of the director.
/// </summary>
public static class ReplayRenderLog
{
    /// <summary>Free disk space that a render needs to start.</summary>
    public const long MinFreeBytes = 1L << 30;

    /// <summary>Free disk space below which a running render stops.</summary>
    public const long StopFreeBytes = 256L << 20;

    // Two bytes per pixel is a high estimate for a PNG of the game window, so the warning comes before
    // the disk is full and not after.
    private const double BytesPerPixel = 2.0;

    public static long EstimateBytes(int pictures, int width, int height) =>
        (long)Math.Min(Math.Max(pictures, 0) * (double)Math.Max(width, 0) * Math.Max(height, 0) * BytesPerPixel, long.MaxValue);

    public static string FolderName(string recordingId, string takeName) =>
        TakeFolders.SafeName(recordingId) + "-" + TakeFolders.SafeName(takeName);

    /// <summary>An event at the end of the take has no picture of its own, so it goes on the last picture.</summary>
    public static List<ReplayRenderRow> EventRows(IReadOnlyDictionary<string, double> eventTimes, ReplayRenderJob job)
    {
        var rows = new List<ReplayRenderRow>();
        foreach ((string name, double time) in eventTimes.OrderBy(e => e.Value).ThenBy(e => e.Key, StringComparer.Ordinal))
        {
            if (time < job.Start - 1e-9 || time > job.End + 1e-9)
                continue;
            double video = Math.Max(time - job.Start, 0.0);
            int picture = Math.Min(ReplayRenderJob.PictureAt(video, job.Fps), job.Total);
            rows.Add(new ReplayRenderRow(video, time, picture, "event", name,
                string.Create(CultureInfo.InvariantCulture, $"takeSeconds={time:F3}")));
        }

        return rows;
    }

    /// <summary>
    /// The first picture also names the shot that runs already, because the render can start inside it.
    /// <paramref name="cut"/> is true when a shot starts, so the caller can let the game settle.
    /// </summary>
    public static List<ReplayRenderRow> FrameRows(Take take, DirectorFrame frame, ReplayRenderJob job, int index, out bool cut)
    {
        var rows = new List<ReplayRenderRow>();
        double video = job.VideoSeconds(index);
        double time = job.TakeTime(index);
        int picture = index + 1;
        cut = frame.StartedShots.Count > 0;
        if (index == 0 && frame.ShotIndex >= 0 && frame.ShotIndex < take.Shots.Count && !frame.StartedShots.Contains(frame.ShotIndex))
            rows.Add(ShotRow(take, frame.ShotIndex, video, time, picture, "; running at the render start"));
        foreach (int shot in frame.StartedShots)
        {
            if (shot >= 0 && shot < take.Shots.Count)
                rows.Add(ShotRow(take, shot, video, time, picture, ""));
        }

        foreach (int key in frame.StartedSpeedKeys)
        {
            if (key < 0 || key >= take.Speed.Count)
                continue;
            SpeedKey speed = take.Speed[key];
            rows.Add(new ReplayRenderRow(video, time, picture, "speed", $"speed[{key}]",
                string.Create(CultureInfo.InvariantCulture, $"to={speed.To}; ramp={speed.Ramp}")));
        }

        return rows;
    }

    // The detail has the same fields as the row of a live take, which the marker tool reads.
    private static ReplayRenderRow ShotRow(Take take, int index, double video, double time, int picture, string note)
    {
        Shot shot = take.Shots[index];
        return new ReplayRenderRow(video, time, picture, "shot", $"camera[{index}]",
            string.Create(CultureInfo.InvariantCulture, $"type={shot.Type}; duration={shot.Duration}; fov={shot.Fov}{note}"));
    }

    /// <summary>
    /// The game seconds are those of the recorded flight, the mission seconds count from the ignition when
    /// the recording has one, and the sim speed is the rate of the speed track.
    /// </summary>
    public static void Write(EventLogCsv log, ReplayRenderRow row, ReplayTakePlan plan, double flightStartSeconds)
    {
        double recording = plan.RecordingTime(row.TakeSeconds);
        double mission = plan.EventTimes.TryGetValue("ignition", out double ignition) && row.TakeSeconds >= ignition
            ? recording - plan.RecordingTime(ignition) : 0.0;
        log.Write(row.VideoSeconds, flightStartSeconds + recording, mission, plan.RecordingRate(row.TakeSeconds),
            row.Picture, row.Kind, row.Name, row.Detail);
    }
}
