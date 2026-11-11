using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using CineCam.Core;
using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Editing;
using CineCam.Features.Replay;
using CineCam.Features.Takes;
using KSA;

namespace CineCam.Features.Ui;

// Every change goes through the edit session, so the file counts as changed and is validated again.
// While a replay is open the dock is its transport, and the replay resolves the take again after
// each edit, so an edit acts on the replay too.
internal static class LiveTimeline
{
    private const ImGuiWindowFlags Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollWithMouse;
    private const string MenuId = "##cinecam-timeline-menu";

    // The search for an event log reads at most this many of the newest take folders.
    private const int MaxRunsToRead = 20;

    private enum ItemKind { None, Shot, ShotEdge, Key, Action }

    private static bool _hidden;

    private static double _viewStart;
    private static double _viewSpan = 30.0;
    private static bool _fitPending = true;
    private static ShotFile? _viewDocument;
    private static string? _viewTake;

    private static string? _runTake;
    private static ActiveTake? _runSeen;
    private static Dictionary<string, double>? _runEvents;
    private static string _runFolder = "";

    private static ItemKind _dragKind;
    private static int _dragIndex = -1;
    private static double _dragOrigin;
    private static ItemKind _menuKind;
    private static int _menuIndex = -1;
    private static bool _openMenu;

    private static bool _wasReplay;

    private static bool _hintSeen;
    private static double _lastSeek = double.NaN;
    private static ReplayTakePlan? _replayPlan;
    private static Dictionary<string, double> _replayEvents = new(StringComparer.Ordinal);
    private static List<TimelineMarker> _replayMarkers = new();

    private static readonly TimelineLayoutCache ReplayLayout = new();
    private static readonly string[] TrackNames = { "CAMERA", "SPEED", "ACTIONS", "EVENTS" };
    private static readonly Take NoTake = new();
    private static readonly Dictionary<string, string> NoAliases = new();

    #region Colors

    // The inks are read each frame, so that they follow the alpha of the window.
    private static uint ShotFill => DockStyle.Ink(ConsoleStyle.FrameU32);
    private static uint ShotSelected => DockStyle.Ink(ConsoleStyle.ActiveToolU32);
    private static uint LoopFill => DockStyle.Ink(ConsoleStyle.PendingU32, 0.35f);
    private static uint BlockBorder => DockStyle.Ink(ConsoleStyle.HairlineU32);
    private static uint OnLight => DockStyle.Ink(ConsoleStyle.PanelU32);
    private static uint KeyColor => DockStyle.Ink(ConsoleStyle.PendingU32);
    private static uint ActionColor => DockStyle.Ink(ConsoleStyle.TextLabelU32);
    private static uint EventColor => DockStyle.Ink(ConsoleStyle.TextLabelU32);
    private static uint EstimateColor => DockStyle.Ink(ConsoleStyle.TextMutedU32);
    private static uint PlayHead => DockStyle.Ink(ConsoleStyle.DangerU32);
    private static uint ActiveOutline => DockStyle.Ink(ConsoleStyle.PositiveU32);
    private static uint Selection => DockStyle.Ink(ConsoleStyle.TextPrimaryU32);
    private static uint TextColor => DockStyle.Ink(ConsoleStyle.TextPrimaryU32);
    private static uint MutedText => DockStyle.Ink(ConsoleStyle.TextMutedU32);
    private static uint TickColor => DockStyle.Ink(ConsoleStyle.TextMutedU32);
    private static uint RuleColor => DockStyle.Ink(ConsoleStyle.HairlineU32);
    private static uint TrackWell => DockStyle.Ink(ConsoleStyle.InsetU32);
    private static uint TrackBorder => DockStyle.Ink(ConsoleStyle.FrameU32);
    private static uint RowWash => DockStyle.Ink(ConsoleStyle.RowHoverWashU32);
    private static uint EventGuide => DockStyle.Ink(ConsoleStyle.HairlineU32, 0.6f);
    private static uint MarkerColor => DockStyle.Ink(ConsoleStyle.TextMutedU32);
    private static uint PastEnd => DockStyle.Ink(ConsoleStyle.PanelU32, 0.7f);

    #endregion

    // The window bounds are null when the window is closed.
    public static void Draw(EditSession session, (float2 Position, float2 Size)? window)
    {
        ActiveTake? running = TakeRunner.Current;
        bool replay = ReplaySession.IsPlayingBack;
        if (replay != _wasReplay)
        {
            _wasReplay = replay;
            _fitPending = true;
            _lastSeek = double.NaN;
        }

        // A replay shows the dock also while the window is closed, because the dock is its transport.
        Take? take = session.Document != null ? session.Take : null;
        bool shown = replay
            ? !TakeRunner.HidesInterface
            : window != null && CineCamWindow.ShowsEditor && take != null
                && !TakeRunner.HidesInterface && running is not { IsRecording: true, IsPaused: false };
        if (!shown)
        {
            _dragKind = ItemKind.None;
            session.ReleaseValidation();
            return;
        }

        float scale = ImGuiHelper.InterfaceScale;
        if (Bounds(window, _hidden ? 44f * scale : DockHeight(window, replay)) is not { } bounds)
        {
            _dragKind = ItemKind.None;
            session.ReleaseValidation();
            return;
        }
        if (_hidden)
        {
            _dragKind = ItemKind.None;
            session.ReleaseValidation();
            DrawCollapsed(bounds.Position, bounds.Size, replay);
            return;
        }

        take ??= NoTake;
        IReadOnlyDictionary<string, string> aliases = session.Document?.Events ?? NoAliases;
        ActiveTake? live = !replay && running is { Preview: null } && string.Equals(running.Take.Name, take.Name, StringComparison.OrdinalIgnoreCase)
            ? running : null;
        double now;
        TimelineLayout layout;
        if (replay)
        {
            RefreshReplay();
            now = ReplaySession.Time;
            IReadOnlyDictionary<string, string> replayAliases = aliases;
            Take replayTake = take;
            layout = ReplayLayout.Get(session.Version, _replayPlan, take, aliases,
                () => TimelineLayout.Build(replayTake, replayAliases, recorded: _replayEvents, markers: _replayMarkers));
        }
        else
        {
            RefreshLastRun(take.Name, running);
            now = live?.Seconds ?? 0.0;
            layout = TimelineLayout.Build(take, aliases, _runEvents, live?.Director.Events.Seen, now);
        }

        if (!ReferenceEquals(_viewDocument, session.Document) || _viewTake != take.Name)
        {
            _viewDocument = session.Document;
            _viewTake = take.Name;
            _fitPending = true;
        }

        if (_fitPending)
        {
            Fit(layout, replay);
            _fitPending = false;
        }

        ImGui.SetNextWindowPos(bounds.Position);
        ImGui.SetNextWindowSize(bounds.Size);
        // Stock flight panels draw over the dock once they get the focus, so the dock takes the
        // focus back while the mouse is over it and no other item is held.
        if (ImGui.IsMouseHoveringRect(bounds.Position, bounds.Position + bounds.Size, false) && !ImGui.IsAnyItemActive())
            ImGui.SetNextWindowFocus();
        DockStyle.BeginPanel("##cinecam-live-timeline", Flags);
        try
        {
            if (replay)
                DrawReplayHeader(layout);
            else
                DrawHeader(layout, live, running);
            DrawCanvas(session, take, layout, live, now, replay);
            if (_openMenu)
            {
                _openMenu = false;
                ImGui.OpenPopup(MenuId);
            }

            DrawMenu(session, running);
        }
        finally { DockStyle.EndPanel(); }
    }

    // Must not touch ImGui. It runs when the mod unloads.
    public static void Shutdown()
    {
        _dragKind = ItemKind.None;
        _dragIndex = -1;
        _menuKind = ItemKind.None;
        _menuIndex = -1;
        _openMenu = false;
        _hidden = false;
        _hintSeen = false;
        _viewStart = 0.0;
        _viewSpan = 30.0;
        _viewDocument = null;
        _viewTake = null;
        _fitPending = true;
        _runTake = null;
        _runSeen = null;
        _runEvents = null;
        _runFolder = "";
        _wasReplay = false;
        _lastSeek = double.NaN;
        _replayPlan = null;
        _replayEvents = new Dictionary<string, double>(StringComparer.Ordinal);
        _replayMarkers = new List<TimelineMarker>();
        ReplayLayout.Clear();
    }

    public static bool FitsBeside((float2 Position, float2 Size) window) =>
        Bounds(window, DockHeight(window, true)) != null;

    #region Placement

    // When the window covers part of the bottom band, the dock takes the wider side beside the window,
    // and it returns null when that side is too narrow.
    private static (float2 Position, float2 Size)? Bounds((float2 Position, float2 Size)? console, float wantedHeight)
    {
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        float scale = ImGuiHelper.InterfaceScale;
        float margin = 8f * scale;
        float height = Math.Min(wantedHeight, viewport.WorkSize.Y);
        float bottom = viewport.WorkPos.Y + viewport.WorkSize.Y;
        float top = bottom - height;
        float left = viewport.WorkPos.X + margin;
        float right = viewport.WorkPos.X + viewport.WorkSize.X - margin;
        if (console is { } window && window.Position.Y + window.Size.Y > top && window.Position.Y < bottom)
        {
            float leftEnd = Math.Clamp(window.Position.X - margin, left, Math.Max(left, right));
            float rightStart = Math.Clamp(window.Position.X + window.Size.X + margin, left, Math.Max(left, right));
            if (leftEnd - left > right - rightStart)
                right = leftEnd;
            else
                left = rightStart;
            if (right - left < 375f * scale)
                return null;
        }

        float width = Math.Max(1f, Math.Min(right - left, 1200f * scale));
        return (new float2(left + (right - left - width) * 0.5f, top), new float2(width, height));
    }

    private static float DockHeight((float2 Position, float2 Size)? window, bool replay)
    {
        float scale = ImGuiHelper.InterfaceScale;
        float height = 230f * scale;
        if (!replay)
            return height;
        float row = ConsoleWidgets.ButtonHeight + ImGui.GetStyle().ItemSpacing.Y;
        int rows = 1;
        if (Bounds(window, height + row) is { } bounds && TransportWidth() > bounds.Size.X - ConsoleStyle.ContentPaddingPx * 2f)
            rows++;
        if (ReplaySession.TakeProblem.Length > 0)
            rows++;
        return height + row * rows;
    }

    private static void DrawCollapsed(float2 position, float2 size, bool replay)
    {
        float scale = ImGuiHelper.InterfaceScale;
        ImGui.SetNextWindowPos(new float2(position.X, position.Y + size.Y - 4f * scale), ImGuiCond.None, new float2(0f, 1f));
        DockStyle.BeginPanel("##cinecam-live-timeline-tab", Flags | ImGuiWindowFlags.AlwaysAutoResize);
        try
        {
            if (replay)
            {
                PlayButton();
                ImGui.SameLine();
            }

            if (DockStyle.Button("TIMELINE", tip: replay ? "Shows the timeline and the controls of the replay." : "Shows the timeline of the take."))
                _hidden = false;
        }
        finally { DockStyle.EndPanel(); }
    }

    private static void Fit(TimelineLayout layout, bool replay)
    {
        _viewStart = 0.0;
        double duration = replay ? Math.Max(layout.Duration, ReplaySession.Duration) : layout.Duration;
        _viewSpan = Math.Max(1.0, duration * 1.05);
    }

    #endregion

    #region Event log of the last run

    // The folders are read again when the take changes and when a take starts or ends, because a run
    // that ends writes a new log.
    private static void RefreshLastRun(string takeName, ActiveTake? running)
    {
        if (_runTake == takeName && ReferenceEquals(_runSeen, running))
            return;
        _runTake = takeName;
        _runSeen = running;
        _runEvents = null;
        _runFolder = "";
        try
        {
            string root = CineCamPaths.TakesFolder;
            if (!Directory.Exists(root))
                return;
            IEnumerable<string> names = Directory.GetDirectories(root).Select(d => Path.GetFileName(d));
            foreach (string folder in TimelineLayout.RunFoldersNewestFirst(names, takeName).Take(MaxRunsToRead))
            {
                string path = Path.Combine(root, folder, "events.csv");
                if (!File.Exists(path))
                    continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                Dictionary<string, double> times = TimelineLayout.ReadEventTimes(reader, out bool finished);
                if (!finished)
                    continue;
                _runEvents = times;
                _runFolder = folder;
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _runEvents = null;
            _runFolder = "";
        }
    }

    // An event after the end of the take has no take time and stays out.
    private static void RefreshReplay()
    {
        ReplayTakePlan? plan = ReplaySession.Plan;
        if (ReferenceEquals(plan, _replayPlan))
            return;
        _replayPlan = plan;
        _replayEvents = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (ReplayEventStatus e in ReplaySession.Events())
        {
            if (e.TakeTime is double time)
                _replayEvents.TryAdd(e.Name, time);
        }

        double recordingEnd = plan == null ? 0.0 : plan.RecordingTime(plan.Duration);
        _replayMarkers = TimelineLayout.MarkersBefore(
            ReplaySession.Markers().Select(m => (m.Label, m.RecordingTime, m.TakeTime)), recordingEnd);
    }

    #endregion

    #region Header

    private static void DrawHeader(TimelineLayout layout, ActiveTake? live, ActiveTake? running)
    {
        float textEnd = DockStyle.Titlebar("TIMELINE", out float barHeight);
        float2 pos = ImGui.GetWindowPos();
        float right = pos.X + ImGui.GetWindowSize().X - ConsoleStyle.TitlebarHorizontalPaddingPx;
        float gap = ImGui.GetStyle().ItemSpacing.X;
        float buttonHeight = ConsoleWidgets.ButtonHeight;
        float buttonY = pos.Y + (barHeight - buttonHeight) * 0.5f;
        float hideX = right - ConsoleWidgets.ButtonWidth("HIDE".AsSpan());
        float fitX = hideX - gap - ConsoleWidgets.ButtonWidth("FIT".AsSpan());

        ImGui.SetCursorScreenPos(new float2(fitX, buttonY));
        if (DockStyle.Button("FIT", tip: "Shows the whole take. The mouse wheel zooms, Shift and the wheel or a drag on the ruler scrolls."))
            Fit(layout, false);
        ImGui.SetCursorScreenPos(new float2(hideX, buttonY));
        if (DockStyle.Button("HIDE", tip: "Hides the timeline. The Timeline button at the bottom of the screen shows it again."))
            _hidden = true;

        var sources = new List<string>();
        if (layout.Events.Any(e => e.Source == EventTimeSource.Live))
            sources.Add("THIS RUN");
        if (layout.Events.Any(e => e.Source == EventTimeSource.LastRun))
            sources.Add("LAST RUN");
        if (layout.HasEstimates)
            sources.Add("ESTIMATES");
        string events = sources.Count == 0 ? "NO EVENTS" : "EVENT TIMES: " + string.Join(", ", sources);
        string? tip = layout.HasEstimates
            ? "Hatched items wait for an event that no run has seen yet, so their time is a guess at an even spacing. Run the take once to measure the events."
            : _runFolder.Length > 0 ? $"The times of the events come from the run in the take folder {_runFolder}." : null;

        float x = textEnd + ConsoleStyle.TitlebarSignatureGapPx * 2f;
        float limit = fitX - gap;
        BarParts.Clear();
        if (live != null)
            BarParts.Add(new BarPart(live.IsPaused ? "PAUSED " + Seconds(live.Seconds) : "RUNNING " + Seconds(live.Seconds),
                live.IsPaused ? ConsoleStyle.PendingU32 : ConsoleStyle.PositiveU32, false, 0, null, null));
        else if (running != null)
            BarParts.Add(new BarPart("ANOTHER RUN IS ACTIVE", ConsoleStyle.TextMutedU32, true, 1, null, null));
        BarParts.Add(new BarPart(events, layout.HasEstimates ? ConsoleStyle.PendingU32 : ConsoleStyle.TextMutedU32, true, 2, tip, "##event-source"));
        DrawBarParts(x, limit, pos.Y, barHeight);

        ImGui.SetCursorScreenPos(new float2(pos.X + ConsoleStyle.ContentPaddingPx, pos.Y + barHeight + ConsoleStyle.ContentPaddingPx));
    }

    private static void DrawReplayHeader(TimelineLayout layout)
    {
        float textEnd = DockStyle.Titlebar("REPLAY", out float barHeight);
        float2 pos = ImGui.GetWindowPos();
        float right = pos.X + ImGui.GetWindowSize().X - ConsoleStyle.TitlebarHorizontalPaddingPx;
        float gap = ImGui.GetStyle().ItemSpacing.X;
        float buttonY = pos.Y + (barHeight - ConsoleWidgets.ButtonHeight) * 0.5f;
        float hideX = right - ConsoleWidgets.ButtonWidth("HIDE".AsSpan());
        float fitX = hideX - gap - ConsoleWidgets.ButtonWidth("FIT".AsSpan());

        ImGui.SetCursorScreenPos(new float2(fitX, buttonY));
        if (DockStyle.Button("FIT", tip: "Shows the whole replay. The mouse wheel zooms, Shift and the wheel scrolls."))
            Fit(layout, true);
        ImGui.SetCursorScreenPos(new float2(hideX, buttonY));
        if (DockStyle.Button("HIDE", tip: "Hides the timeline. The Timeline button at the bottom of the screen shows it again."))
            _hidden = true;

        float limit = fitX - gap;
        float x = textEnd + ConsoleStyle.TitlebarSignatureGapPx * 2f;
        BarParts.Clear();
        if (ReplaySession.ReplayId != null)
            BarParts.Add(new BarPart(StatusHeader.RecordingName(), ConsoleStyle.TextMutedU32, true, 3, "Recording: " + ReplaySession.ReplayId, "##recording-name"));
        if (!(ReplaySession.IsRendering && ReplaySession.RenderStatus is not null))
            BarParts.Add(new BarPart(ReplaySession.IsPlaying ? "PLAYING" : "PAUSED",
                ReplaySession.IsPlaying ? ConsoleStyle.PositiveU32 : ConsoleStyle.PendingU32, false, 0, null, null));
        if (ReplaySession.IsRendering && ReplaySession.RenderStatus is { } render)
            BarParts.Add(new BarPart(FormattableString.Invariant($"RENDERING {Math.Floor(render.Percent):0}%"), ConsoleStyle.PendingU32, false, 0,
                ReplaySession.RenderStatusText + " Escape or the start and stop key stops it.", "##render-progress"));
        if (layout.Unreached.Count > 0)
            BarParts.Add(new BarPart(StatusText.NotReached(layout.Unreached.Count), ConsoleStyle.PendingU32, true, 1,
                UnreachedTip(layout.Unreached), "##unreached"));
        DrawBarParts(x, limit, pos.Y, barHeight);

        ImGui.SetCursorScreenPos(new float2(pos.X + ConsoleStyle.ContentPaddingPx, pos.Y + barHeight + ConsoleStyle.ContentPaddingPx));
        DrawTransport();
    }

    // A part with an id gets a hover area for its tip.
    private readonly record struct BarPart(string Text, uint Color, bool Small, int Priority, string? Tip, string? Id);

    private static readonly List<BarPart> BarParts = new(4);
    private static readonly List<double> BarWidths = new(4);
    private static readonly List<int> BarPriorities = new(4);

    // The parts of the lowest priority are left out first, so no text is cut in a word.
    private static bool[] _barKeep = [];

    private static void DrawBarParts(float x, float limit, float top, float barHeight)
    {
        float gap = ConsoleStyle.TitlebarSignatureGapPx * 2f;
        BarWidths.Clear();
        BarPriorities.Clear();
        foreach (BarPart part in BarParts)
        {
            BarWidths.Add(DockStyle.TextWidth(part.Text, part.Small) + gap);
            BarPriorities.Add(part.Priority);
        }

        if (_barKeep.Length < BarParts.Count)
            _barKeep = new bool[BarParts.Count];
        bool[] keep = _barKeep;
        StatusText.FitParts(BarWidths, BarPriorities, limit - x + gap, keep);
        for (int i = 0; i < BarParts.Count; i++)
        {
            if (!keep[i])
                continue;
            BarPart part = BarParts[i];
            float start = x;
            float end = DockStyle.BarText(part.Text, x, limit, part.Color, part.Small);
            if (part.Id != null && end - start >= 1f)
            {
                ImGui.SetCursorScreenPos(new float2(start, top));
                ImGui.InvisibleButton(part.Id.AsSpan(), new float2(end - start, barHeight));
                Tip(part.Tip);
            }

            x = end + gap;
        }
    }

    private static string UnreachedTip(IReadOnlyList<TimelineUnreached> items)
    {
        var lines = new List<string> { "The recording has no event for these items, so the replay never reaches them:" };
        foreach (TimelineUnreached item in items)
        {
            string what = item.Kind switch
            {
                TimelineItemKind.Shot => "Shot",
                TimelineItemKind.Key => "Speed key",
                _ => "Action",
            };
            lines.Add($"{what} {item.Index + 1} waits for {EventDisplay.Name(item.EventKey)}");
        }

        lines.Add("An item after such a shot or speed key never starts either.");
        return string.Join("\n", lines);
    }

    #endregion

    #region Transport

    private const string PlayTip = "Plays or pauses the replay. The play and pause key of the Settings page does the same.";
    private const string StartTip = "Goes back to the start of the replay.";
    private const string SpeedTip = "Plays the take slower or faster than its video. The speed track of the take sets the speed of the flight on top. Ctrl and a click types a value.";
    private const string ExitLabel = "EXIT REPLAY";
    private const string OpenLabel = "OPEN CINECAM";

    private static string ClockText() =>
        FormattableString.Invariant($"{ReplaySession.Time:F1} / {ReplaySession.Duration:F1} s");

    private static string FlightText() => FormattableString.Invariant($"FLIGHT {ReplaySession.RecordingTime:F1} s");

    private static void PlayButton()
    {
        string play = ReplaySession.IsPlaying ? "PAUSE" : "PLAY";
        if (DockStyle.Button(play, ReplaySession.Duration > 0, ReplaySession.Duration > 0 ? PlayTip : "The replay has no time to play.",
                DockStyle.Kind.Primary, id: "replay-play"))
            ReplaySession.SetPlaying(!ReplaySession.IsPlaying);
    }

    // The times are measured at their longest values, so the height of the dock does not change while the replay plays.
    private static float TransportWidth()
    {
        float gap = ImGui.GetStyle().ItemSpacing.X;
        float scale = ImGuiHelper.InterfaceScale;
        string clock = FormattableString.Invariant($"{ReplaySession.Duration:F1} / {ReplaySession.Duration:F1} s");
        string flight = FormattableString.Invariant($"FLIGHT {ReplaySession.RecordingDuration:F1} s");
        float width = ConsoleWidgets.ButtonWidth("PAUSE".AsSpan()) + ConsoleWidgets.ButtonWidth("START".AsSpan())
            + DockStyle.TextWidth(clock, false) + DockStyle.TextWidth(flight, true)
            + DockStyle.TextWidth("SPEED", false) + 80f * scale
            + ConsoleWidgets.ButtonWidth("TAKE CAMERA".AsSpan()) + ConsoleWidgets.ButtonWidth("FREE CAMERA".AsSpan())
            + ConsoleWidgets.ButtonWidth(ExitLabel.AsSpan()) + gap * 10f;
        if (!CineCamWindow.IsOpen)
            width += ConsoleWidgets.ButtonWidth(OpenLabel.AsSpan()) + gap;
        return width;
    }

    private static void DrawTransport()
    {
        float scale = ImGuiHelper.InterfaceScale;
        PlayButton();
        DockStyle.Flow(ConsoleWidgets.ButtonWidth("START".AsSpan()));
        if (DockStyle.Button("START", tip: StartTip, id: "replay-start"))
            ReplaySession.Seek(0);

        string clock = ClockText();
        DockStyle.Flow(DockStyle.TextWidth(clock, false));
        ImGui.AlignTextToFramePadding();
        DockStyle.PushFont(false);
        try { DockStyle.Text(clock, in ConsoleStyle.TextPrimary); }
        finally { ConsoleStyle.PopFont(); }
        string flight = FlightText();
        DockStyle.Flow(DockStyle.TextWidth(flight, true));
        ImGui.AlignTextToFramePadding();
        DockStyle.PushFont(true);
        try { DockStyle.Text(flight, in ConsoleStyle.TextMuted); }
        finally { ConsoleStyle.PopFont(); }
        DockStyle.Tip("The time of the recording that shows now. The speed track of the take sets how fast it runs.");

        float fieldWidth = 80f * scale;
        DockStyle.Flow(DockStyle.TextWidth("SPEED", false) + fieldWidth);
        ImGui.AlignTextToFramePadding();
        DockStyle.Text("SPEED", in ConsoleStyle.TextLabel);
        ImGui.SameLine();
        float speed = (float)ReplaySession.Speed;
        ImGui.SetNextItemWidth(fieldWidth);
        if (ImGui.DragFloat("##replay-speed".AsSpan(), ref speed, 0.01f, (float)ReplayTakePlayer.MinPreviewSpeed,
                (float)ReplayTakePlayer.MaxPreviewSpeed, "%.2fx".AsSpan(), ImGuiSliderFlags.AlwaysClamp))
            ReplaySession.SetSpeed(speed);
        DockStyle.Tip(SpeedTip);

        // The camera in use is a primary button, because the kit draws a positive button plain until the mouse is over it.
        bool takeCamera = ReplaySession.CameraMode == ReplayCameraMode.Take;
        DockStyle.Flow(ConsoleWidgets.ButtonWidth("TAKE CAMERA".AsSpan()) + ConsoleWidgets.ButtonWidth("FREE CAMERA".AsSpan()) + ImGui.GetStyle().ItemSpacing.X);
        if (DockStyle.Button("TAKE CAMERA", tip: "The camera shows the take, as the video will show it.",
                kind: takeCamera ? DockStyle.Kind.Primary : DockStyle.Kind.Plain, id: "replay-take-camera") && !takeCamera)
            ReplaySession.SetCamera(ReplayCameraMode.Take);
        ImGui.SameLine();
        if (DockStyle.Button("FREE CAMERA", tip: "Look around with the game camera. Add key here makes a shot from its view at the replay time.",
                kind: takeCamera ? DockStyle.Kind.Plain : DockStyle.Kind.Primary, id: "replay-free-camera") && takeCamera)
            ReplaySession.SetCamera(ReplayCameraMode.Free);

        DockStyle.Flow(ConsoleWidgets.ButtonWidth(ExitLabel.AsSpan()));
        if (DockStyle.Button(ExitLabel, tip: "Closes the replay and goes back to the live flight. The start and stop key does the same."))
            ReplaySession.Close();
        if (!CineCamWindow.IsOpen)
        {
            DockStyle.Flow(ConsoleWidgets.ButtonWidth(OpenLabel.AsSpan()));
            if (DockStyle.Button(OpenLabel, tip: "Opens the CineCam window to edit the take. The replay goes on."))
                CineCamWindow.Instance.Open();
        }

        string problem = StatusHeader.ReplayProblem(CineCamWindow.EditorSession);
        if (problem.Length > 0)
        {
            float2 at = ImGui.GetCursorScreenPos();
            float width = Math.Max(1f, ImGui.GetContentRegionAvail().X);
            float height = ImGui.GetFrameHeight();
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            string shown;
            DockStyle.PushFont(false);
            try
            {
                shown = Widgets.FitWords(problem, width);
                draw.AddText(new float2(at.X, at.Y + (height - ImGui.GetFontSize()) * 0.5f), DockStyle.Ink(ConsoleStyle.PendingU32), shown);
            }
            finally { ConsoleStyle.PopFont(); }
            ImGui.InvisibleButton("##take-problem".AsSpan(), new float2(width, height));
            Tip(shown != problem ? problem : null);
        }
    }

    #endregion

    #region Canvas

    private static void DrawCanvas(EditSession session, Take take, TimelineLayout layout, ActiveTake? live, double now, bool replay)
    {
        // A playing replay keeps its play head in the view, unless the player holds an item or the ruler.
        if (replay && ReplaySession.IsPlaying && _dragKind == ItemKind.None && double.IsNaN(_lastSeek))
            _viewStart = TimelineLayout.FollowStart(_viewStart, _viewSpan, now);

        float scale = ImGuiHelper.InterfaceScale;
        float2 origin = ImGui.GetCursorScreenPos();
        float2 avail = ImGui.GetContentRegionAvail();
        string[] names = TrackNames;
        float widest = 0f;
        foreach (string name in names)
            widest = Math.Max(widest, DockStyle.TextWidth(name, true));
        float labelWidth = Math.Max(64f * scale, widest + 10f * scale);
        float rulerHeight = 18f * scale;
        float x0 = origin.X + labelWidth;
        float x1 = origin.X + Math.Max(labelWidth + 40f * scale, avail.X - 4f * scale);
        float trackHeight = Math.Clamp((avail.Y - rulerHeight - 4f * scale) / 4f, 16f * scale, 40f * scale);
        float rulerTop = origin.Y;
        float tracksTop = rulerTop + rulerHeight;
        float bottom = tracksTop + trackHeight * 4f;
        var canvas = new Canvas(x0, x1, _viewStart, (x1 - x0) / (float)Math.Max(_viewSpan, 1e-6));

        HandleWheel(canvas, new float2(origin.X, rulerTop), new float2(x1, bottom), layout);

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        float rounding = ConsoleStyle.FrameRoundingPx;
        float hairline = ConsoleStyle.WindowBorderThicknessPx;
        draw.AddRectFilled(new float2(x0, tracksTop), new float2(x1, bottom), TrackWell, rounding);
        ConsoleStyle.PushSignatureFont();
        try
        {
            for (int row = 0; row < 4; row++)
            {
                float top = tracksTop + trackHeight * row;
                if (row % 2 == 1)
                    draw.AddRectFilled(new float2(x0, top), new float2(x1, top + trackHeight), RowWash);
                if (row > 0)
                    draw.AddLine(new float2(x0, top), new float2(x1, top), RuleColor, hairline);
                draw.AddText(new float2(origin.X + ConsoleWidgets.RowLabelInsetPx, top + (trackHeight - ImGui.GetFontSize()) * 0.5f), MutedText, names[row]);
            }
        }
        finally { ConsoleStyle.PopFont(); }
        draw.AddRect(new float2(x0, tracksTop), new float2(x1, bottom), TrackBorder, rounding, ImDrawFlags.None, hairline);

        DrawRuler(draw, canvas, rulerTop, rulerHeight, scale, replay);

        draw.PushClipRect(new float2(x0, rulerTop), new float2(x1, bottom), true);
        try
        {
            if (replay)
            {
                float end = Math.Max(canvas.X(ReplaySession.Duration), x0);
                if (end < x1)
                {
                    draw.AddRectFilled(new float2(end, tracksTop), new float2(x1, bottom), PastEnd);
                    draw.AddLine(new float2(end, rulerTop), new float2(end, bottom), RuleColor, hairline);
                }
            }

            foreach (TimelineEvent e in layout.Events)
            {
                float x = canvas.X(e.Time);
                if (x >= x0 && x <= x1)
                    draw.AddLine(new float2(x, tracksTop), new float2(x, bottom - trackHeight), EventGuide, 1f);
            }

            int activeShot = replay ? ReplaySession.Frame?.ShotIndex ?? -1 : live?.LastFrame?.ShotIndex ?? -1;
            DrawShots(draw, session, take, layout, canvas, tracksTop, trackHeight, activeShot, scale);
            if (!_hintSeen || take.Shots.Count == 0)
                DrawHint(draw, layout, canvas, tracksTop, trackHeight, scale);
            DrawKeys(draw, session, take, layout, canvas, tracksTop + trackHeight, trackHeight, scale);
            DrawActions(draw, session, take, layout, canvas, tracksTop + trackHeight * 2f, trackHeight, scale);
            DrawEvents(draw, layout, canvas, tracksTop + trackHeight * 3f, trackHeight, scale, replay);

            if (live != null || replay)
            {
                float x = canvas.X(now);
                if (x >= x0 && x <= x1)
                {
                    draw.AddLine(new float2(x, rulerTop), new float2(x, bottom), PlayHead, 2f * scale);
                    draw.AddTriangleFilled(new float2(x - 5f * scale, rulerTop), new float2(x + 5f * scale, rulerTop),
                        new float2(x, rulerTop + 7f * scale), PlayHead);
                }
            }
        }
        finally { draw.PopClipRect(); }

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            _dragKind = ItemKind.None;
        // A drag changes the take in steps of a tenth of a second, so the file is validated once at its end.
        if (_dragKind == ItemKind.None)
            session.ReleaseValidation();

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new float2(Math.Max(1f, avail.X), bottom - origin.Y));
    }

    // Maps take time to screen x and back.
    private readonly record struct Canvas(float Left, float Right, double Start, float PixelsPerSecond)
    {
        public float X(double time) => Left + (float)((time - Start) * PixelsPerSecond);

        public double Time(float x) => Start + (x - Left) / PixelsPerSecond;

        public double Seconds(float pixels) => pixels / PixelsPerSecond;
    }

    private static void HandleWheel(Canvas canvas, float2 min, float2 max, TimelineLayout layout)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        float wheel = io.MouseWheel;
        if (wheel == 0f || !ImGui.IsWindowHovered() || !ImGui.IsMouseHoveringRect(min, max))
            return;
        if (io.KeyShift)
        {
            _viewStart = Math.Max(0.0, _viewStart - wheel * _viewSpan * 0.1);
            return;
        }

        double maxSpan = Math.Max(60.0, layout.Duration * 4.0);
        (_viewStart, _viewSpan) = TimelineLayout.ZoomAround(_viewStart, _viewSpan, canvas.Time(ImGui.GetMousePos().X),
            Math.Pow(0.8, wheel), 0.5, maxSpan);
    }

    private static void DrawRuler(ImDrawListPtr draw, Canvas canvas, float top, float height, float scale, bool replay)
    {
        double step = TimelineLayout.TickStep(canvas.PixelsPerSecond, 70f * scale);
        double first = Math.Ceiling(canvas.Start / step) * step;
        double end = canvas.Time(canvas.Right);
        float hairline = ConsoleStyle.WindowBorderThicknessPx;
        ConsoleStyle.PushSignatureFont();
        try
        {
            float textTop = top + 1f * scale;
            for (double t = first; t <= end + 1e-9; t += step)
            {
                float x = canvas.X(t);
                draw.AddLine(new float2(x, top + height * 0.6f), new float2(x, top + height), TickColor, hairline);
                draw.AddText(new float2(x + 3f * scale, textTop), TickColor, Clock(t, step));
            }
        }
        finally { ConsoleStyle.PopFont(); }

        ImGui.SetCursorScreenPos(new float2(canvas.Left, top));
        ImGui.InvisibleButton("##ruler".AsSpan(), new float2(Math.Max(1f, canvas.Right - canvas.Left), height));
        if (replay)
        {
            // A drag scrubs roughly when the time under the mouse changes, and the release seeks exactly.
            if (ImGui.IsItemActive())
            {
                double time = TimelineLayout.ScrubTime(canvas.Time(ImGui.GetMousePos().X), ReplaySession.Duration);
                if (double.IsNaN(_lastSeek) || Math.Abs(time - _lastSeek) > 1e-4)
                {
                    _lastSeek = time;
                    ReplaySession.Scrub(time);
                }
            }
            else
            {
                if (!double.IsNaN(_lastSeek))
                    ReplaySession.Seek(_lastSeek);
                _lastSeek = double.NaN;
                Tip("Click or drag to move the replay. The mouse wheel zooms, Shift and the wheel scrolls.");
            }

            return;
        }

        if (ImGui.IsItemActivated())
            _dragOrigin = _viewStart;
        if (ImGui.IsItemActive())
            _viewStart = Math.Max(0.0, _dragOrigin - canvas.Seconds(ImGui.GetMouseDragDelta(ImGuiMouseButton.Left, 0f).X));
        else
            Tip("Drag to scroll. The mouse wheel zooms, Shift and the wheel scrolls.");
    }

    #endregion

    #region Tracks

    private static void DrawShots(ImDrawListPtr draw, EditSession session, Take take, TimelineLayout layout, Canvas canvas,
        float top, float height, int activeShot, float scale)
    {
        float inset = 3f * scale;
        float y0 = top + inset;
        float y1 = top + height - inset;
        ImGui.PushID("camera".AsSpan());
        try
        {
            for (int b = 0; b < layout.Shots.Count; b++)
            {
                TimelineShot block = layout.Shots[b];
                int i = block.Index;
                if (i >= take.Shots.Count)
                    continue;
                Shot shot = take.Shots[i];
                float xs = canvas.X(block.Start);
                float xe = Math.Max(canvas.X(block.End), xs + 2f);
                if (xe < canvas.Left || xs > canvas.Right)
                    continue;
                float vx0 = Math.Max(xs, canvas.Left);
                float vx1 = Math.Min(xe, canvas.Right);
                bool selected = i == session.ShotIndex;
                float rounding = ConsoleStyle.FrameRoundingPx;
                uint ink = selected ? OnLight : TextColor;
                draw.AddRectFilled(new float2(xs, y0), new float2(xe, y1), selected ? ShotSelected : block.Loop ? LoopFill : ShotFill, rounding);
                if (block.Estimated)
                    Hatch(draw, vx0, y0, vx1, y1, scale, DockStyle.Ink(selected ? ConsoleStyle.PanelU32 : ConsoleStyle.TextPrimaryU32, 0.3f));
                if (block.BlendSeconds > 0.0)
                {
                    float xb = Math.Min(canvas.X(block.Start + block.BlendSeconds), xe);
                    draw.AddTriangleFilled(new float2(xs, y1), new float2(xb, y0), new float2(xb, y1),
                        DockStyle.Ink(selected ? ConsoleStyle.PanelU32 : ConsoleStyle.TextPrimaryU32, 0.2f));
                    draw.AddLine(new float2(xs, y1), new float2(xb, y0), ink, 1.5f * scale);
                }

                draw.AddRect(new float2(xs, y0), new float2(xe, y1), i == activeShot ? ActiveOutline : selected ? Selection : BlockBorder,
                    rounding, ImDrawFlags.None, i == activeShot ? 2f * scale : ConsoleStyle.WindowBorderThicknessPx);
                // A later block that starts inside this one is drawn over it, so the label room ends where that block starts.
                float visibleEnd = vx1;
                for (int later = b + 1; later < layout.Shots.Count; later++)
                {
                    float laterStart = canvas.X(layout.Shots[later].Start);
                    if (laterStart < visibleEnd && layout.Shots[later].End > block.Start)
                        visibleEnd = Math.Max(vx0, laterStart);
                }

                float room = visibleEnd - Math.Max(xs, canvas.Left) - 8f * scale;
                string label = $"{shot.Type} {Number(shot.Duration)} s" + (block.Loop ? " loop" : "");
                if (ImGui.CalcTextSize(label).X > room)
                    label = shot.Type.ToString();
                if (ImGui.CalcTextSize(label).X <= room)
                {
                    draw.PushClipRect(new float2(vx0 + 3f * scale, y0), new float2(Math.Max(vx0 + 3f * scale, visibleEnd - 3f * scale), y1), true);
                    draw.AddText(new float2(Math.Max(xs, canvas.Left) + 5f * scale, y0 + (y1 - y0 - ImGui.GetFontSize()) * 0.5f), ink, label);
                    draw.PopClipRect();
                }

                ImGui.PushID(i);
                try
                {
                    // The edge is submitted before the body, because the first item under the mouse takes the hover.
                    if (xe >= canvas.Left && xe <= canvas.Right)
                    {
                        float half = 4f * scale;
                        ImGui.SetCursorScreenPos(new float2(xe - half, y0));
                        ImGui.InvisibleButton("##edge".AsSpan(), new float2(half * 2f, y1 - y0));
                        if (ImGui.IsItemHovered() || ImGui.IsItemActive())
                            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
                        if (ImGui.IsItemActivated())
                            StartDrag(ItemKind.ShotEdge, i, shot.Duration, () => session.ShotIndex = i);
                        if (ImGui.IsItemActive() && IsDragging(ItemKind.ShotEdge, i))
                        {
                            float dx = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left).X;
                            session.HoldValidation();
                            if (dx != 0f)
                                session.SetShotDuration(i, _dragOrigin + canvas.Seconds(dx));
                        }
                        else
                            Tip($"Shot {i + 1}: drag to change its length ({Number(shot.Duration)} s).");
                        ContextMenu(ItemKind.Shot, i, () => session.ShotIndex = i);
                    }

                    ImGui.SetCursorScreenPos(new float2(vx0, y0));
                    ImGui.InvisibleButton("##body".AsSpan(), new float2(Math.Max(2f, vx1 - vx0), y1 - y0));
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                    {
                        session.ShotIndex = i;
                        _hintSeen = true;
                    }

                    ContextMenu(ItemKind.Shot, i, () => session.ShotIndex = i);
                    Tip(ShotTip(i, shot, block));
                }
                finally { ImGui.PopID(); }
            }
        }
        finally { ImGui.PopID(); }
    }

    private const string HintText = "Click a shot to edit it. Drag its right edge for the length. Right-click for more.";

    private static void DrawHint(ImDrawListPtr draw, TimelineLayout layout, Canvas canvas, float top, float height, float scale)
    {
        float from = canvas.Left;
        foreach (TimelineShot block in layout.Shots)
            from = Math.Max(from, canvas.X(block.End));
        from += 8f * scale;
        float width = ImGui.CalcTextSize(HintText).X;
        if (from + width > canvas.Right - 4f * scale)
            return;
        draw.AddText(new float2(from, top + (height - ImGui.GetFontSize()) * 0.5f), MutedText, HintText);
    }

    private static string ShotTip(int i, Shot shot, TimelineShot block)
    {
        string text = $"Shot {ShotEdit.Describe(i, shot)}\nFrom {Seconds(block.Start)} to {Seconds(block.End)}";
        if (block.BlendSeconds > 0.0)
            text += $", blends in over {Number(block.BlendSeconds)} s";
        if (block.Loop)
            text += "\nPart of the pre-roll loop, which repeats until its event comes.";
        if (block.Estimated)
            text += "\nThe start is estimated, because no run has seen its event yet.";
        return text + "\nClick to select, drag the right edge to change the length, right click for more.";
    }

    private static void DrawKeys(ImDrawListPtr draw, EditSession session, Take take, TimelineLayout layout, Canvas canvas,
        float top, float height, float scale)
    {
        float r = 6f * scale;
        float cy = top + height * 0.5f;
        float labelEnd = float.NegativeInfinity;
        ImGui.PushID("speed".AsSpan());
        try
        {
            foreach (TimelineKey mark in layout.Keys)
            {
                int i = mark.Index;
                if (i >= take.Speed.Count)
                    continue;
                SpeedKey key = take.Speed[i];
                float x = canvas.X(mark.Time);
                float xr = canvas.X(mark.RampEnd);
                if (xr > x)
                    draw.AddLine(new float2(x, cy), new float2(xr, cy), KeyColor, 2f * scale);
                if (x < canvas.Left - r || x > canvas.Right + r)
                    continue;
                bool selected = i == session.KeyIndex;
                float size = selected ? r * 1.3f : r;
                Diamond(draw, x, cy, size, KeyColor, filled: !mark.Estimated);
                if (selected)
                    Diamond(draw, x, cy, size + 2f * scale, Selection, filled: false);
                string label = $"{Number(key.To)}x";
                if (x + size + 2f > labelEnd)
                {
                    float labelY = xr > x ? cy - ImGui.GetFontSize() - 1f * scale : cy - ImGui.GetFontSize() * 0.5f;
                    draw.AddText(new float2(x + size + 3f * scale, labelY), mark.Estimated ? EstimateColor : TextColor, label);
                    labelEnd = x + size + 3f * scale + ImGui.CalcTextSize(label).X + 4f * scale;
                }

                bool movable = key.At.Kind == TriggerKind.Time;
                ImGui.PushID(i);
                try
                {
                    ImGui.SetCursorScreenPos(new float2(x - r, top));
                    ImGui.InvisibleButton("##key".AsSpan(), new float2(r * 2f, height));
                    if (ImGui.IsItemActivated())
                        StartDrag(movable ? ItemKind.Key : ItemKind.None, i, key.At.Time, () => session.KeyIndex = i);
                    if (movable && ImGui.IsItemActive() && IsDragging(ItemKind.Key, i))
                    {
                        float dx = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left).X;
                        session.HoldValidation();
                        if (dx != 0f)
                            session.SetKeyTime(i, _dragOrigin + canvas.Seconds(dx));
                    }

                    ContextMenu(ItemKind.Key, i, () => session.KeyIndex = i);
                    Tip($"Speed key {ShotEdit.Describe(i, key)}\nStarts at {Seconds(mark.Time)}"
                        + (mark.Estimated ? " (estimated, no run has seen its event yet)" : "")
                        + (movable ? "\nClick to select, drag to move, right click for more."
                            : "\nClick to select, right click for more. Only a key that starts at a time can be dragged."));
                }
                finally { ImGui.PopID(); }
            }
        }
        finally { ImGui.PopID(); }
    }

    private static void DrawActions(ImDrawListPtr draw, EditSession session, Take take, TimelineLayout layout, Canvas canvas,
        float top, float height, float scale)
    {
        float r = 6f * scale;
        float cy = top + height * 0.5f;
        float labelEnd = float.NegativeInfinity;
        ImGui.PushID("actions".AsSpan());
        try
        {
            foreach (TimelineAction mark in layout.Actions)
            {
                int i = mark.Index;
                if (i >= take.Actions.Count)
                    continue;
                TakeAction action = take.Actions[i];
                float x = canvas.X(mark.Time);
                if (x < canvas.Left - r || x > canvas.Right + r)
                    continue;
                bool selected = i == session.ActionIndex;
                float2 a = new(x - r, cy - r);
                float2 b = new(x + r, cy - r);
                float2 c = new(x, cy + r);
                if (mark.Estimated)
                    draw.AddTriangle(a, b, c, ActionColor, 2f * scale);
                else
                    draw.AddTriangleFilled(a, b, c, ActionColor);
                if (selected)
                    draw.AddTriangle(new float2(a.X - 2f * scale, a.Y - 2f * scale), new float2(b.X + 2f * scale, b.Y - 2f * scale),
                        new float2(c.X, c.Y + 3f * scale), Selection, 1.5f * scale);
                string label = action.Sequence is int sequence ? $"Stage {sequence}" : "Stage";
                if (x + r + 2f > labelEnd)
                {
                    draw.AddText(new float2(x + r + 3f * scale, cy - ImGui.GetFontSize() * 0.5f), mark.Estimated ? EstimateColor : TextColor, label);
                    labelEnd = x + r + 3f * scale + ImGui.CalcTextSize(label).X + 4f * scale;
                }

                bool movable = action.At.Kind == TriggerKind.Time;
                ImGui.PushID(i);
                try
                {
                    ImGui.SetCursorScreenPos(new float2(x - r, top));
                    ImGui.InvisibleButton("##action".AsSpan(), new float2(r * 2f, height));
                    if (ImGui.IsItemActivated())
                        StartDrag(movable ? ItemKind.Action : ItemKind.None, i, action.At.Time, () => session.ActionIndex = i);
                    if (movable && ImGui.IsItemActive() && IsDragging(ItemKind.Action, i))
                    {
                        float dx = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left).X;
                        session.HoldValidation();
                        if (dx != 0f)
                            session.SetActionTime(i, _dragOrigin + canvas.Seconds(dx));
                    }

                    ContextMenu(ItemKind.Action, i, () => session.ActionIndex = i);
                    Tip($"Action {ShotEdit.Describe(i, action)}\nFires at {Seconds(mark.Time)}"
                        + (mark.Estimated ? " (estimated, no run has seen its event yet)" : "")
                        + (movable ? "\nClick to select, drag to move, right click for more."
                            : "\nClick to select, right click for more. Only an action that fires at a time can be dragged."));
                }
                finally { ImGui.PopID(); }
            }
        }
        finally { ImGui.PopID(); }
    }

    private static void DrawEvents(ImDrawListPtr draw, TimelineLayout layout, Canvas canvas, float top, float height, float scale, bool replay)
    {
        float labelEnd = float.NegativeInfinity;
        // The labels come last, each on a plate in the well color, so no guide or event line shows through the text.
        var labels = new List<(float X, float Gap, uint Color, string Text)>();
        string jump = replay ? "\nClick to move the replay here." : "";
        ImGui.PushID("events".AsSpan());
        try
        {
            for (int n = 0; n < layout.Markers.Count; n++)
            {
                TimelineMarker mark = layout.Markers[n];
                float x = canvas.X(mark.Time);
                if (x < canvas.Left - 4f || x > canvas.Right + 4f)
                    continue;
                draw.AddLine(new float2(x, top + 4f * scale), new float2(x, top + height - 4f * scale), MarkerColor, 1f * scale);
                labels.Add((x + 3f * scale, 3f * scale, MarkerColor, mark.Label));
                // The marks use ids from 1,000,000 on, so they do not collide with the event ids below.
                ImGui.PushID(1_000_000 + n);
                try
                {
                    ImGui.SetCursorScreenPos(new float2(x - 3f * scale, top));
                    ImGui.InvisibleButton("##mark".AsSpan(), new float2(6f * scale, height));
                    if (replay && ImGui.IsItemClicked(ImGuiMouseButton.Left))
                        ReplaySession.Seek(TimelineLayout.ScrubTime(mark.Time, ReplaySession.Duration));
                    Tip($"{mark.Label} at {Seconds(mark.Time)} (from the recording){jump}");
                }
                finally { ImGui.PopID(); }
            }

            for (int n = 0; n < layout.Events.Count; n++)
            {
                TimelineEvent e = layout.Events[n];
                float x = canvas.X(e.Time);
                if (x < canvas.Left - 4f || x > canvas.Right + 4f)
                    continue;
                uint color = e.Estimated ? EstimateColor : EventColor;
                if (e.Estimated)
                {
                    for (float y = top + 2f * scale; y < top + height - 2f * scale; y += 6f * scale)
                        draw.AddLine(new float2(x, y), new float2(x, Math.Min(y + 3f * scale, top + height - 2f * scale)), color, 2f * scale);
                }
                else
                    draw.AddLine(new float2(x, top + 2f * scale), new float2(x, top + height - 2f * scale), color, 2f * scale);

                string name = EventDisplay.Name(e.Key);
                labels.Add((x + 4f * scale, 4f * scale, color, e.Estimated ? name + " (estimated)" : name));

                ImGui.PushID(n);
                try
                {
                    ImGui.SetCursorScreenPos(new float2(x - 4f * scale, top));
                    ImGui.InvisibleButton("##event".AsSpan(), new float2(8f * scale, height));
                    if (replay && ImGui.IsItemClicked(ImGuiMouseButton.Left))
                        ReplaySession.Seek(TimelineLayout.ScrubTime(e.Time, ReplaySession.Duration));
                    string source = e.Source switch
                    {
                        EventTimeSource.Live => "seen in this run",
                        EventTimeSource.LastRun => "from the last finished run",
                        EventTimeSource.Recording => "from the recording",
                        _ => "estimated, because no run has seen it yet",
                    };
                    Tip($"{name} at {Seconds(e.Time)} ({source}){jump}");
                }
                finally { ImGui.PopID(); }
            }
        }
        finally { ImGui.PopID(); }

        // A label that would overlap the label before it is left out.
        labels.Sort((a, b) => a.X.CompareTo(b.X));
        float textTop = top + (height - ImGui.GetFontSize()) * 0.5f;
        foreach ((float lineRight, float gap, uint lcolor, string text) in labels)
        {
            float w = ImGui.CalcTextSize(text).X;
            float lx = lineRight;
            if (lx + w + 2f * scale > canvas.Right)
                lx = Math.Max(canvas.Left, lineRight - gap - gap - w);
            if (lx <= labelEnd)
                continue;
            labelEnd = lx + w + 6f * scale;
            draw.AddRectFilled(new float2(lx - 2f * scale, textTop), new float2(lx + w + 2f * scale, textTop + ImGui.GetFontSize()), TrackWell);
            draw.AddText(new float2(lx, textTop), lcolor, text);
        }
    }

    #endregion

    #region Interaction

    private static void StartDrag(ItemKind kind, int index, double origin, Action select)
    {
        _hintSeen = true;
        select();
        _dragKind = kind;
        _dragIndex = index;
        _dragOrigin = origin;
    }

    private static bool IsDragging(ItemKind kind, int index) => _dragKind == kind && _dragIndex == index;

    private static void ContextMenu(ItemKind kind, int index, Action select)
    {
        if (!ImGui.IsItemClicked(ImGuiMouseButton.Right))
            return;
        _hintSeen = true;
        select();
        _menuKind = kind;
        _menuIndex = index;
        _openMenu = true;
    }

    // Delete and Duplicate change the order of a list, so they wait until no take runs, as the
    // buttons of the track lists do. Cut and Blend change a field and work at any time.
    private static void DrawMenu(EditSession session, ActiveTake? running)
    {
        if (!DockStyle.BeginMenu(MenuId))
            return;
        try
        {
            string block = ViewText.FileEditBlock(running != null);
            bool free = block.Length == 0;
            Take? take = session.Take;
            int i = _menuIndex;
            switch (_menuKind)
            {
                case ItemKind.Shot when take != null && i >= 0 && i < take.Shots.Count:
                    ImGui.TextDisabled($"SHOT {i + 1}".AsSpan());
                    if (ImGui.MenuItem("Duplicate", default, false, free))
                    {
                        session.ShotIndex = i;
                        session.DuplicateShot();
                    }

                    if (ImGui.MenuItem("Delete", default, false, free))
                    {
                        session.ShotIndex = i;
                        session.RemoveShot();
                    }

                    ImGui.Separator();
                    TransitionKind kind = take.Shots[i].Transition.Kind;
                    if (ImGui.MenuItem("Cut", default, kind == TransitionKind.Cut))
                        session.SetShotTransition(i, TransitionKind.Cut);
                    if (ImGui.MenuItem("Blend", default, kind == TransitionKind.Blend))
                        session.SetShotTransition(i, TransitionKind.Blend);
                    break;
                case ItemKind.Key when take != null && i >= 0 && i < take.Speed.Count:
                    ImGui.TextDisabled($"SPEED KEY {i + 1}".AsSpan());
                    if (ImGui.MenuItem("Duplicate", default, false, free))
                    {
                        session.KeyIndex = i;
                        session.DuplicateKey();
                    }

                    if (ImGui.MenuItem("Delete", default, false, free))
                    {
                        session.KeyIndex = i;
                        session.RemoveKey();
                    }

                    break;
                case ItemKind.Action when take != null && i >= 0 && i < take.Actions.Count:
                    ImGui.TextDisabled($"ACTION {i + 1}".AsSpan());
                    if (ImGui.MenuItem("Duplicate", default, false, free))
                    {
                        session.ActionIndex = i;
                        session.DuplicateAction();
                    }

                    if (ImGui.MenuItem("Delete", default, false, free))
                    {
                        session.ActionIndex = i;
                        session.RemoveAction();
                    }

                    break;
                default:
                    ImGui.CloseCurrentPopup();
                    return;
            }

            if (!free)
                ImGui.TextDisabled(Widgets.Escape(block));
        }
        finally { ImGui.EndPopup(); }
    }

    #endregion

    #region Drawing and text

    private static void Diamond(ImDrawListPtr draw, float x, float y, float r, uint color, bool filled)
    {
        float2 top = new(x, y - r);
        float2 right = new(x + r, y);
        float2 bottom = new(x, y + r);
        float2 left = new(x - r, y);
        if (filled)
            draw.AddQuadFilled(top, right, bottom, left, color);
        else
            draw.AddQuad(top, right, bottom, left, color, 2f);
    }

    // Diagonal lines over a block whose start is estimated.
    private static void Hatch(ImDrawListPtr draw, float x0, float y0, float x1, float y1, float scale, uint color)
    {
        if (x1 <= x0)
            return;
        float h = y1 - y0;
        float gap = 7f * scale;
        draw.PushClipRect(new float2(x0, y0), new float2(x1, y1), true);
        for (float x = x0 - h; x < x1; x += gap)
            draw.AddLine(new float2(x, y1), new float2(x + h, y0), color, 1f);
        draw.PopClipRect();
    }

    private static void Tip(string? text) => DockStyle.Tip(text);

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Seconds(double value) => value.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    // A ruler label: seconds for a short take, minutes and seconds from one minute on.
    private static string Clock(double t, double step)
    {
        if (t < 60.0)
            return t.ToString(step < 1.0 ? "0.0" : "0", CultureInfo.InvariantCulture) + " s";
        int minutes = (int)Math.Floor(t / 60.0);
        double rest = t - minutes * 60.0;
        return minutes.ToString(CultureInfo.InvariantCulture) + ":" + rest.ToString(step < 1.0 ? "00.0" : "00", CultureInfo.InvariantCulture);
    }

    #endregion
}
