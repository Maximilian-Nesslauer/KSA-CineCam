using Brutal.Logging;
using CineCam.Api;
using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Features.Replay;
using CineCam.Features.Takes;
using KSA;

namespace CineCam.Features;

// Feeds CineCamEvents from the live take, the replay and the render. All calls come from the main thread.
internal static class ApiFeed
{
    private enum Source { None, Live, Replay, Render }

    private sealed class ReplayRun(ReplayTakePlan plan, int jumps, double last, bool includeFrom)
    {
        public ReplayTakePlan Plan { get; } = plan;
        public int Jumps { get; } = jumps;
        public double Last { get; set; } = last;
        public bool IncludeFrom { get; set; } = includeFrom;
    }

    private static Source _source;
    private static ReplayRun? _replay;

    public static void Start() => CineCamEvents.ErrorLog = text => DefaultCategory.Log.Error(text);

    public static void Shutdown()
    {
        _source = Source.None;
        _replay = null;
        CineCamEvents.Reset();
        CineCamEvents.ErrorLog = null;
    }

    /// <summary>Raises <see cref="CineCamEvents.InterfaceChanged"/> when the hidden state of the game interface changed.</summary>
    public static void SyncInterface() =>
        CineCamEvents.SyncInterface(TakeRunner.HidesInterface || ReplaySession.IsRendering || ReplaySession.HidesInterface);

    #region Live

    public static void LiveStarted(ActiveTake take)
    {
        EndRun(Source.Replay, "a live take started");
        SyncInterface();
        _source = Source.Live;
        CineCamEvents.RaiseTakeStarted(take.Take.Name, take.DryRun ? "dryRun" : "live", take.HidUi, 0.0, []);
    }

    public static void LiveEnded(string reason)
    {
        SyncInterface();
        EndRun(Source.Live, reason);
    }

    public static void LiveEvent(string name, double takeSeconds) =>
        Raise(Source.Live, () => CineCamEvents.RaiseFlightEvent(name, takeSeconds, Universe.GetElapsedSeconds()));

    public static void LiveShot(Take take, int index) => Raise(Source.Live, () => Shot(take, index, true));

    public static void LiveCue(string name, double takeSeconds) =>
        Raise(Source.Live, () => CineCamEvents.RaiseCue(name, takeSeconds, Universe.GetElapsedSeconds()));

    public static void LiveTick(Take take, DirectorFrame frame, double takeSeconds)
    {
        if (_source != Source.Live)
            return;
        CineCamEvents.SetTakeSeconds(takeSeconds);
        if (frame.ShotIndex != CineCamEvents.ShotIndex)
            Shot(take, frame.ShotIndex, false);
    }

    #endregion

    #region Replay

    // Runs after the replay moved in a frame without a render. A run lasts while the replay plays without a
    // jump, so a seek, an edit of the take, a pause and the end each end it.
    public static void ReplayFrame(ReplayScene scene)
    {
        if (_source is Source.Live or Source.Render)
            return;
        ReplayTakePlayer player = scene.Player;
        bool playing = player.IsPlaying && player.Plan.Take != null && ReplaySession.TakeName != null;
        if (_replay is { } run)
        {
            bool jumped = player.Jumps != run.Jumps || !ReferenceEquals(player.Plan, run.Plan) || player.Time < run.Last;
            if (!jumped)
                Step(scene, run);
            if (jumped || !playing)
            {
                string reason = jumped ? "the replay moved to another time or the take changed"
                    : player.Time >= player.Duration ? "the end of the replay" : "the replay paused";
                EndRun(Source.Replay, reason);
            }
        }

        if (_replay == null && playing)
            StartReplayRun(scene);
    }

    public static void ReplayOpened(string recording)
    {
        SyncInterface();
        CineCamEvents.RaiseReplayOpened(recording);
    }

    public static void ReplayClosed()
    {
        EndRun(Source.Replay, "the replay closed");
        SyncInterface();
        CineCamEvents.RaiseReplayClosed();
    }

    private static void StartReplayRun(ReplayScene scene)
    {
        ReplayTakePlayer player = scene.Player;
        Take take = player.Plan.Take!;
        double time = player.Time;
        bool fromStart = time <= 1e-9;
        _replay = new ReplayRun(player.Plan, player.Jumps, time, fromStart);
        _source = Source.Replay;
        SyncInterface();
        IEnumerable<string> passed = ReplayFeed.CuesUntil(player.Plan, time)
            .Where(cue => !fromStart || cue.TakeSeconds < time).Select(cue => cue.Name);
        CineCamEvents.RaiseTakeStarted(take.Name, "replay", CineCamEvents.InterfaceHidden, time, passed);
        if (player.Frame is { ShotIndex: >= 0 } frame)
            Shot(take, frame.ShotIndex, true);
    }

    private static void Step(ReplayScene scene, ReplayRun run)
    {
        ReplayTakePlayer player = scene.Player;
        double time = player.Time;
        if (time == run.Last && !run.IncludeFrom)
            return;
        List<FeedItem> due = ReplayFeed.Due(run.Plan, run.Last, time, run.IncludeFrom);
        bool moved = time > run.Last;
        run.Last = time;
        run.IncludeFrom = false;
        CineCamEvents.SetTakeSeconds(time);
        double start = scene.Flight.StartTime;
        foreach (FeedItem item in due.Where(item => item.Kind == FeedKind.Event))
            CineCamEvents.RaiseFlightEvent(item.Name, item.TakeSeconds, start + run.Plan.RecordingTime(item.TakeSeconds));
        if (moved && player.Frame is { } frame && run.Plan.Take is { } take)
        {
            foreach (int index in frame.StartedShots)
                Shot(take, index, true);
            if (frame.ShotIndex != CineCamEvents.ShotIndex)
                Shot(take, frame.ShotIndex, false);
        }

        foreach (FeedItem item in due.Where(item => item.Kind == FeedKind.Cue))
            CineCamEvents.RaiseCue(item.Name, item.TakeSeconds, start + run.Plan.RecordingTime(item.TakeSeconds));
    }

    #endregion

    #region Render

    public static void RenderStarted(string take, string folder, ReplayTakePlan plan, double start)
    {
        EndRun(Source.Replay, "a render started");
        CineCamEvents.RaiseRenderStarted(take, folder);
        SyncInterface();
        _source = Source.Render;
        // ReplayRenderLog.TimedRows gives a cue at the first picture a row of its own, so only the cues before it count as passed.
        IEnumerable<string> passed = ReplayFeed.CuesUntil(plan, start).Where(cue => cue.TakeSeconds < start - 1e-9).Select(cue => cue.Name);
        CineCamEvents.RaiseTakeStarted(take, "render", true, start, passed);
    }

    /// <summary>The rows are the event and cue rows of one picture, the frame is the director frame of that picture.</summary>
    public static void RenderPicture(ReplayTakePlan plan, double flightStart, double takeSeconds, IEnumerable<ReplayRenderRow> rows,
        DirectorFrame? frame, bool first)
    {
        if (_source != Source.Render)
            return;
        CineCamEvents.SetTakeSeconds(takeSeconds);
        List<ReplayRenderRow> list = rows.ToList();
        foreach (ReplayRenderRow row in list.Where(row => row.Kind == "event"))
            CineCamEvents.RaiseFlightEvent(row.Name, row.TakeSeconds, flightStart + plan.RecordingTime(row.TakeSeconds));
        if (frame != null && plan.Take is { } take)
        {
            if (first && frame.ShotIndex >= 0 && !frame.StartedShots.Contains(frame.ShotIndex))
                Shot(take, frame.ShotIndex, true);
            foreach (int index in frame.StartedShots)
                Shot(take, index, true);
            if (frame.ShotIndex != CineCamEvents.ShotIndex)
                Shot(take, frame.ShotIndex, false);
        }

        foreach (ReplayRenderRow row in list.Where(row => row.Kind == "cue"))
            CineCamEvents.RaiseCue(row.Name, row.TakeSeconds, flightStart + plan.RecordingTime(row.TakeSeconds));
    }

    public static void RenderEnded(string take, string folder, string reason)
    {
        EndRun(Source.Render, reason);
        SyncInterface();
        CineCamEvents.RaiseRenderEnded(take, folder, reason);
    }

    #endregion

    private static void EndRun(Source source, string reason)
    {
        if (_source != source)
            return;
        _source = Source.None;
        _replay = null;
        CineCamEvents.RaiseTakeEnded(reason);
    }

    private static void Raise(Source source, Action raise)
    {
        if (_source == source)
            raise();
    }

    private static void Shot(Take take, int index, bool started)
    {
        if (index < 0 || index >= take.Shots.Count)
        {
            CineCamEvents.SetShot(-1, "", "");
            return;
        }

        Shot shot = take.Shots[index];
        string type = shot.Type.ToString().ToLowerInvariant();
        if (started)
            CineCamEvents.RaiseShotStarted(index, type, shot.Name);
        else
            CineCamEvents.SetShot(index, type, shot.Name);
    }
}
