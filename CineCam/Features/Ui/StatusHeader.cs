using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using CineCam.Director;
using CineCam.Director.Quick;
using CineCam.Director.Replay;
using CineCam.Editing;
using CineCam.Features.Quick;
using CineCam.Features.Replay;
using CineCam.Features.Takes;
using KSA;

namespace CineCam.Features.Ui;

internal static class StatusHeader
{
    private static readonly List<FooterItem> FooterParts = new(8);

    public static void Draw(EditSession session)
    {
        ActiveTake? running = TakeRunner.Current;
        ResetFlow flow = TakeRunner.Reset;
        RunState state = StateOf(running, flow);

        string text = StatusText.LiveLine(running?.Take.Name ?? session.Take?.Name, StateText(running, flow, state), NextText(session, running, state));
        float4 color = state == RunState.Resetting ? Widgets.GoodColor : Widgets.StateColor(state);
        if (state == RunState.Idle && session.Take != null)
            color = ConsoleStyle.TextPrimary;
        Widgets.StatusLine(text, color, FactsTip(session, running));
        if (running is { Preview: null })
            DrawShotLine(running);
    }

    private static RunState StateOf(ActiveTake? running, ResetFlow flow) => ViewText.StateOf(new RunStateInput(
        running != null, running?.DryRun ?? false, running?.Preview != null, running?.Offline != null,
        running?.IsPaused ?? false, flow.IsActive, TakeRunner.ReadyAfterReset));

    private static string StateText(ActiveTake? running, ResetFlow flow, RunState state)
    {
        double? seconds = running is { Preview: null } ? running.Seconds : null;
        string text = StatusText.TakeState(state, seconds);
        if (running?.Offline != null)
            text += string.Create(CultureInfo.InvariantCulture, $", picture {running.Offline.Count}");
        else if (state == RunState.Resetting && flow.SecondsToStart(TakeRunner.ClockSeconds) is double left)
            text += string.Create(CultureInfo.InvariantCulture, $", the take starts in {left:F1} s");
        return text;
    }

    private static string NextText(EditSession session, ActiveTake? running, RunState state)
    {
        if (running is not { Preview: null })
        {
            if (state is RunState.Idle or RunState.Ready)
                return session.Take == null ? "next: pick New take in the Take list"
                    : session.Validation.IsValid ? "next: press Start take"
                    : "the take has a problem, fix the problem below";
            return "";
        }

        IReadOnlyDictionary<string, string> aliases = session.Document?.Events ?? running.File.Events;
        NextTrigger? next = ViewText.FindNext(running.Take, aliases, running.Director.Events.Seen,
            running.Director.Events.Predictions, running.Seconds, running.LastFrame?.ShotIndex ?? -1);
        return next is { } n
            ? "next: " + (n with { Starts = Widgets.StartsText(n.Starts, aliases) }).Text
            : "the take ends with its last shot";
    }

    private static string FactsTip(EditSession session, ActiveTake? running)
    {
        double current = Universe.GetSimulationSpeed();
        double? target = running is { IsRecording: true, Preview: null } ? running.LastFrame?.TargetSpeed : null;
        Vehicle? vehicle = Program.ControlledVehicle;
        bool hasVehicle = vehicle != null && !vehicle.IsDisposed && Program.Editor == null;
        string checkpoint = session.Take?.Checkpoint is { } save ? save : "none (see Take setup)";
        return $"Game speed: {ViewText.SimSpeedText(current, target)}\nVehicle: {(hasVehicle ? vehicle!.Id : "no controlled vehicle")}\nCheckpoint: {checkpoint}";
    }

    private static void DrawShotLine(ActiveTake running)
    {
        DirectorFrame? frame = running.LastFrame;
        int index = frame?.ShotIndex ?? -1;
        DrawProgress(ViewText.ShotText(running.Take, index, frame?.Progress ?? 0.0), frame?.Progress ?? 0.0);
    }

    private static void DrawProgress(string text, double progress)
    {
        float fraction = (float)Math.Clamp(progress, 0.0, 1.0);
        float width = ConsoleStyle.ContentAvailWidth();
        float h = ConsoleStyle.FrameHeightPx;
        float2 at = ImGui.GetCursorScreenPos();
        ConsoleWidgets.InputWell(at, new float2(width, h));
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (fraction > 0f)
            dl.AddRectFilled(at, at + new float2(width * fraction, h), ConsoleStyle.ApplyAlpha(ConsoleStyle.FrameU32), ConsoleStyle.FrameRoundingPx);
        ConsoleStyle.PushValueFont();
        try
        {
            dl.AddText(at + new float2(ConsoleWidgets.ComboTextPaddingPx, (h - ImGui.GetFontSize()) * 0.5f),
                ConsoleStyle.ApplyAlpha(ConsoleStyle.TextPrimaryU32), text);
        }
        finally { ConsoleStyle.PopFont(); }
        ImGui.Dummy(new float2(width, h));
    }

    #region Replay

    public static void DrawReplay(EditSession session)
    {
        string takeName = ReplaySession.TakeName ?? session.Take?.Name ?? "no take";
        bool playing = ReplaySession.IsPlaying;
        string state = string.Create(CultureInfo.InvariantCulture, $"{(playing ? "Playing" : "Paused")} at {ReplaySession.Time:F1} s");
        string shot = ReplaySession.Plan?.Take is { } take && ReplaySession.Frame is { } frame
            ? ViewText.ShotText(take, frame.ShotIndex, frame.Progress)
            : "";
        string text = $"\"{takeName}\" on {RecordingName()}  |  {state}" + (shot.Length > 0 ? "  |  " + shot : "");
        double rate = (ReplaySession.Plan?.RecordingRate(ReplaySession.Time) ?? 0.0) * ReplaySession.Speed;
        string tip = string.Create(CultureInfo.InvariantCulture,
            $"Recording: {ReplaySession.ReplayId}\nFlight time: {ReplaySession.RecordingTime:F1} of {ReplaySession.RecordingDuration:F1} s\nFlight speed now: {rate:0.###}x, preview speed {ReplaySession.Speed:0.##}x\nPlay, pause and move the replay in the timeline at the bottom of the screen.");
        Widgets.StatusLine(text, playing ? Widgets.GoodColor : Widgets.WarnColor, tip);
    }

    // The vehicle and the date of the open recording, else its id.
    public static string RecordingName()
    {
        string? id = ReplaySession.ReplayId;
        if (id == null)
            return "no recording";
        foreach (RecordingEntry entry in ReplaySession.Recordings())
        {
            if (entry.Id == id)
                return RecordingList.DisplayName(entry);
        }

        return RecordingList.DateFromId(id) is { } date ? RecordingList.DateText(date) : id;
    }

    // Returns an empty text when the take plays on the replay as it is. The window lists the errors
    // of the take, so here they are named only by their count.
    public static string ReplayProblem(EditSession session)
    {
        string problem = ReplaySession.TakeProblem;
        if (problem.Length == 0)
            return "";
        int errors = session.Validation.Errors.Count;
        if (errors > 0)
            return errors == 1
                ? "The take has 1 error, so the replay plays it as it was before. The window lists it."
                : $"The take has {errors} errors, so the replay plays it as it was before. The window lists them.";
        return problem;
    }

    #endregion

    #region Footer

    public static FooterLine Footer(EditSession session)
    {
        ActiveTake? running = TakeRunner.Current;
        RunState state = StateOf(running, TakeRunner.Reset);
        FooterParts.Clear();
        if (ReplaySession.IsRendering)
            FooterParts.Add(new FooterItem(StatusText.Priority.Render, ReplaySession.RenderStatusText, true));
        if (ReplaySession.IsRecording)
            FooterParts.Add(new FooterItem(StatusText.Priority.Recording,
                string.Create(CultureInfo.InvariantCulture, $"Flight recording {ReplaySession.RecordingSeconds:F1} s"), true));
        int errors = session.Validation.Errors.Count;
        if (errors > 0)
            FooterParts.Add(new FooterItem(StatusText.Priority.Problem, errors == 1 ? "The take has 1 error" : $"The take has {errors} errors", true));
        else if (ReplayProblem(session) is { Length: > 0 } problem)
            FooterParts.Add(new FooterItem(StatusText.Priority.Problem, problem, true));
        bool replay = ReplaySession.IsPlayingBack;
        if (!replay || running != null)
            FooterParts.Add(new FooterItem(StatusText.Priority.Take,
                StatusText.TakeState(state, running is { Preview: null } ? running.Seconds : null), running is { IsRecording: true }));
        if (replay)
            FooterParts.Add(new FooterItem(StatusText.Priority.Replay, string.Create(CultureInfo.InvariantCulture,
                $"Replay {(ReplaySession.IsPlaying ? "playing" : "paused")} at {ReplaySession.Time:F1} s"), false));
        if (QuickCamera.Mode is QuickMode mode)
            FooterParts.Add(new FooterItem(StatusText.Priority.Quick, "Quick camera: " + QuickCamera.Describe(mode), false));
        if (!ReplaySession.IsRendering && ReplaySession.RenderStatusText is { Length: > 0 } render)
            FooterParts.Add(new FooterItem(StatusText.Priority.Note, render, false));
        if (ReplaySession.Status.Length > 0)
            FooterParts.Add(new FooterItem(StatusText.Priority.Note, ReplaySession.Status, false));
        if (session.Message.Length > 0 && !session.MessageIsError)
            FooterParts.Add(new FooterItem(StatusText.Priority.Note, session.Message, false));
        if (_footer == null || !FooterParts.SequenceEqual(LastFooterParts))
        {
            LastFooterParts.Clear();
            LastFooterParts.AddRange(FooterParts);
            _footer = StatusText.Footer(FooterParts);
        }

        return _footer.Value;
    }

    // The footer is made again only when a part changed.
    private static readonly List<FooterItem> LastFooterParts = new();
    private static FooterLine? _footer;

    #endregion
}
