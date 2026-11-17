using Brutal.ImGuiApi;
using Brutal.Logging;
using Brutal.Numerics;
using CineCam.Core;
using CineCam.Editing;
using CineCam.Features.Takes;
using CineCam.Features.Replay;
using KSA;

namespace CineCam.Features.Ui;

// The edit session is static, so the edits stay when the window is closed and opened again.
internal sealed class CineCamWindow
{
    private const string WindowId = "cinecam-window";
    private static readonly string WindowTitle = UiText.MessageText("text.d5c16cf7386a", "CAMERA DIRECTOR");
    private const string WindowSignature = "CINECAM";

    private static CineCamWindow? _instance;
    private static bool _toggleRequested;
    private static bool _wasShown;
    private static bool _openedByPause;
    private static bool _pauseSeen;
    private static bool _focused;
    private static bool _lastDrawUi = true;
    private static bool _lastPlayback;
    private static (float2 Position, float2 Size)? _drawnBounds;

    private static readonly EditSession Session = new();
    private static readonly LiveEvents Live = new();

    private bool _show;

    public static CineCamWindow Instance => _instance ??= new CineCamWindow();

    public static bool IsOpen => _instance != null && _instance.IsShown;

    public static EditSession EditorSession
    {
        get
        {
            _ = Instance;
            return Session;
        }
    }

    public bool IsShown => _show;

    private CineCamWindow()
    {
        ShotFileStore.EditorCopyProvider = CopyOfEdits;
        ShotFileStore.CheckpointRecorder = (take, save) => (Session.RecordCheckpoint(take, save), Session.Message);
        LoadFirstFile();
    }

    // The key asks for a toggle and the draw hook does it, because that hook runs in an ImGui frame.
    public static void RequestToggle() => _toggleRequested = true;

    public void Open() => _show = true;

    public void Close() => _show = false;

    public static void DrawActive(IGameViewport viewport)
    {
        if (_toggleRequested)
        {
            _toggleRequested = false;
            if (IsOpen)
                Instance.Close();
            else
                Instance.Open();
        }

        // Only the start of a pause opens the window, so a window closed during the pause stays closed.
        bool pausedNow = TakeRunner.Current is { IsPaused: true };
        if (pausedNow && !_pauseSeen && !IsOpen)
        {
            Instance.Open();
            _openedByPause = true;
        }
        else if (!pausedNow && _openedByPause)
        {
            _openedByPause = false;
            if (IsOpen)
                Instance.Close();
        }

        _pauseSeen = pausedNow;

        CineCamWindow? window = _instance;
        _drawnBounds = null;
        if (window == null || !window.IsShown)
        {
            NotifyClosed(window);
            if (ReplayVisible)
                ReplayOverlay.Draw();
            LiveTimeline.Draw(Session, null);
            return;
        }

        _wasShown = true;
        ActiveTake? take = TakeRunner.Current;
        bool hidden = take is { IsRecording: true, IsPaused: false } && !CineCamSettings.ShowWindowDuringTake;
        if (!hidden)
            window.DrawWindow();
        else
            _focused = false;

        // The close button of the title strip hides the window without a call to Close.
        if (!window.IsShown)
            NotifyClosed(window);
        if (ReplayVisible)
            ReplayOverlay.Draw();
        LiveTimeline.Draw(Session, window.IsShown ? _drawnBounds : null);
    }

    private static void NotifyClosed(CineCamWindow? window)
    {
        if (!_wasShown)
            return;
        _wasShown = false;
        _focused = false;
        FreeCamera.RestoreFov();
        Widgets.ClearBuffers();
        if (TakeRunner.Current is { Preview: not null })
            TakeRunner.RequestStop();
    }

    // Must not touch ImGui. It runs when the mod unloads.
    public static void Shutdown()
    {
        try
        {
            if (TakeRunner.Current is { Preview: not null })
                TakeRunner.RequestStop();
            FreeCamera.RestoreFov();
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Warning($"[CineCam] The window could not give the camera back: {ex.Message}");
        }

        QuickPanel.Shutdown();
        LiveTimeline.Shutdown();
        ReplayPanel.Reset();
        TakePanel.Reset();
        TrackPanel.Reset();
        ShotFileStore.EditorCopyProvider = null;
        ShotFileStore.CheckpointRecorder = null;
        ShotFileStore.PathOverride = null;
        ShotFileStore.PreviewSiteId = null;
        ShotFileStore.PreviewAnchor = null;
        Widgets.ClearBuffers();
        _instance = null;
        _toggleRequested = false;
        _wasShown = false;
        _openedByPause = false;
        _pauseSeen = false;
        _focused = false;
        _drawnBounds = null;
        _page = Page.Quick;
        _liveTab = LiveTab.Shots;
        _source = Source.Live;
        _wasPaused = false;
        _wasReplaying = false;
        _problems = [];
        _problemTips = [];
        _problemsOf = null;
        _problemsTake = -1;
    }

    private static EditorCopy? CopyOfEdits() =>
        Session.Dirty && Session.Document != null ? new EditorCopy(Session.CopyForRun(), Session.Path) : null;

    private static void LoadFirstFile()
    {
        if (Session.Document != null)
            return;
        string path = ShotFileStore.CurrentPath;
        if (File.Exists(path))
            Session.Load(path);
        else
            Session.NewFromDefault(path);
    }

    #region Frame

    // ConsoleStyle.BeginWindow ends the ImGui window itself when it returns false, so EndWindow
    // follows only a true result. Every scope below closes in a finally, so a throw from a page
    // cannot leave Begin, a body or the widget style unmatched for the rest of the frame.
    private void DrawWindow()
    {
        // IsWindowFocused is read at the end of the window, so the game flag follows one frame later, as for the stock windows.
        if (_focused)
            Program.IsWindowOpen = true;

        ImGuiViewportPtr main = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new float2(main.Pos.X + 40f, main.Pos.Y + 60f), ImGuiCond.Once, (float2?)null);
        // Stock panels draw over the window once the interface comes back or a replay closes, so the window takes the focus for one frame then.
        bool drawUi = Program.DrawUI;
        bool playback = ReplaySession.IsPlayingBack;
        if ((drawUi && !_lastDrawUi) || (_lastPlayback && !playback))
            ImGui.SetNextWindowFocus();
        _lastDrawUi = drawUi;
        _lastPlayback = playback;
        bool open = true;
        if (!ConsoleStyle.BeginWindow(WindowId.AsSpan(), UiText.Render(WindowTitle).AsSpan(), WindowSignature.AsSpan(), ref open,
                new float2(640f, 760f) * ImGuiHelper.InterfaceScale, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            _focused = false;
            return;
        }

        try
        {
            _drawnBounds = (ImGui.GetWindowPos(), ImGui.GetWindowSize());
            if (!open)
                Close();
            DrawFrame();
            _focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("window-" + ex.GetType().Name, $"[CineCam] Window draw failed: {ex}");
        }
        finally
        {
            ConsoleStyle.EndWindow();
            Widgets.EndWindow();
        }
    }

    // ConsoleStyle bodies must not nest, and the footer must come after the last body, as in the stock console windows.
    private static void DrawFrame()
    {
        ConsoleLayout layout = ConsoleStyle.CurrentLayout;
        float pad = ConsoleStyle.ContentPaddingPx;
        float x = layout.BodyPos.X + pad;
        float w = Math.Max(1f, layout.BodySize.X - pad * 2f);
        float y = layout.BodyPos.Y + pad;

        ActiveTake? running = TakeRunner.Current;
        ConsoleStyle.PushWidgetStyle();
        try
        {
            float tabsH = ConsoleStyle.FrameHeightPx + Math.Max(1f, 2f * Widgets.Scale);
            ConsoleStyle.BeginBody("cinecam-tabs".AsSpan(), new float2(x, y), new float2(w, tabsH),
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            try
            {
                int picked = Widgets.Tabs("cinecam-pages", PageNames, (int)_page);
                if (picked >= 0)
                    _page = (Page)picked;
            }
            finally
            {
                ConsoleStyle.EndBody();
            }

            y += tabsH + pad * 0.5f;
            float bottom = layout.FooterPos.Y - pad * 0.5f;
            ConsoleStyle.BeginBody("cinecam-page".AsSpan(), new float2(x, y), new float2(w, Math.Max(1f, bottom - y)));
            try
            {
                DrawPage(running);
            }
            catch (Exception ex)
            {
                LogHelper.ErrorOnce("window-page-" + ex.GetType().Name, $"[CineCam] Window page failed: {ex}");
            }
            finally
            {
                ConsoleStyle.EndBody();
            }
        }
        finally
        {
            ConsoleStyle.PopWidgetStyle();
        }

        ConsoleStyle.BeginFooter();
        try
        {
            DrawFooter();
        }
        finally
        {
            ConsoleStyle.EndFooter();
        }
    }

    private static void DrawFooter()
    {
        FooterLine footer = StatusHeader.Footer(Session);
        ConsoleLayout layout = ConsoleStyle.CurrentLayout;
        float pad = ConsoleStyle.ContentPaddingPx;
        float room = Math.Max(1f, layout.FooterSize.X - pad * 2f - (footer.Pending ? ConsoleStyle.FrameHeightPx * 0.6f : 0f));
        string shown;
        ConsoleStyle.PushValueFont();
        try { shown = Widgets.FitWords(footer.Shown, room); }
        finally { ConsoleStyle.PopFont(); }
        ConsoleStyle.FooterStatus(Widgets.Escape(shown).AsSpan(), footer.Pending);
        string tip = footer.Tip.Length > 0 ? footer.Tip : shown != footer.Shown ? footer.Shown : "";
        if (tip.Length > 0)
            Widgets.TipOver(layout.FooterPos, layout.FooterPos + layout.FooterSize, tip);
    }

    #endregion

    #region Pages

    private enum Page { Quick, Editor, Settings }

    private enum Source { Live, Replay }

    private enum LiveTab { Shots, Speed, Actions, PauseEdit, TakeSetup }

    private static readonly string[] PageNames = [UiText.MessageText("text.9f49fadb6a86", "Quick"), UiText.MessageText("text.936a161c3931", "Editor"), UiText.MessageText("text.74a883a037bc", "Settings")];
    private static readonly string[] LiveTabNames = [UiText.MessageText("text.2bce1f4a654f", "Shots"), UiText.MessageText("text.c372fee9b456", "Speed"), UiText.MessageText("text.ff8059dc6752", "Actions"), UiText.MessageText("text.dff466d6f51d", "Pause edit"), UiText.MessageText("text.b82721c4c8da", "Take setup")];

    // A replay has no pause edit tab, because the replay itself pauses and the take is always open for edits.
    private static readonly LiveTab[] ReplayTabs = [LiveTab.Shots, LiveTab.Speed, LiveTab.Actions, LiveTab.TakeSetup];
    private static readonly string[] ReplayTabNames = [UiText.MessageText("text.2bce1f4a654f", "Shots"), UiText.MessageText("text.c372fee9b456", "Speed"), UiText.MessageText("text.ff8059dc6752", "Actions"), UiText.MessageText("text.b82721c4c8da", "Take setup")];
    private static readonly string[] SourceNames = [UiText.MessageText("text.56f76fe047b2", "DIRECT LIVE"), UiText.MessageText("text.0fd82eb78aba", "RECORD AND REPLAY")];

    private const int MaxProblemLines = 5;

    private static Page _page = Page.Quick;
    private static LiveTab _liveTab = LiveTab.Shots;
    private static Source _source = Source.Live;
    private static bool _wasPaused;
    private static bool _wasReplaying;

    private static List<ProblemLine> _problems = [];
    private static string[] _problemTips = [];
    private static readonly string[] ProblemIds = Enumerable.Range(0, MaxProblemLines).Select(i => UiText.Concat("problem-", i)).ToArray();
    private static ValidationResult? _problemsOf;
    private static int _problemsTake = -1;

    // The replay also shows while it records or plays, so a replay that a local program opened can still be closed.
    private static bool ReplayVisible => CineCamSettings.ExperimentalReplay || ReplaySession.IsBusy;

    public static bool ShowsEditor => IsOpen && _page == Page.Editor;

    private static void DrawPage(ActiveTake? running)
    {
        if (running == null || running.Preview != null)
            Live.Update(Session.Take, Session.Document, Session.Version);
        UpdateSelection(running);

        // The replay source of the Editor page shows the same state in its own block.
        if (ReplaySession.IsBusy && !(_page == Page.Editor && _source == Source.Replay && ReplayVisible))
            ReplayPanel.DrawHeader();
        switch (_page)
        {
            case Page.Quick:
                QuickPanel.Draw();
                break;
            case Page.Editor:
                DrawEditor(running);
                break;
            default:
                GlobalPanel.Draw(Session);
                break;
        }
    }

    private static void UpdateSelection(ActiveTake? running)
    {
        bool paused = running is { IsPaused: true };
        if (paused != _wasPaused && !ReplaySession.IsPlayingBack)
        {
            _page = Page.Editor;
            _source = Source.Live;
            _liveTab = paused ? LiveTab.PauseEdit : LiveTab.Shots;
        }

        _wasPaused = paused;
        if (ReplaySession.IsPlayingBack && !_wasReplaying)
            ShowReplay();
        _wasReplaying = ReplaySession.IsPlayingBack;
        if (!ReplayVisible)
            _source = Source.Live;
    }

    private static void ShowReplay()
    {
        _page = Page.Editor;
        _source = Source.Replay;
    }

    private static void DrawEditor(ActiveTake? running)
    {
        if (ReplayVisible)
        {
            string?[] tips =
            [
                ReplaySession.IsPlayingBack
                    ? UiText.MessageText("text.eef3fcc3270b", "Exit the replay first. The take of the editor plays in the replay now.")
                    : UiText.MessageText("text.234d9b6f3f49", "The take runs in the live flight, while you fly."),
                UiText.MessageText("text.c2fc849b80a9", "Record a flight first, then play the take on the recording as often as you like, and render it to pictures. Experimental."),
            ];
            int picked = Widgets.Segmented("cinecam-source", SourceNames, (int)_source, tips);
            if (picked == (int)Source.Live)
                _source = Source.Live;
            else if (picked == (int)Source.Replay)
                _source = Source.Replay;
            ImGui.Dummy(new float2(0f, 4f * Widgets.Scale));
        }

        bool replay = _source == Source.Replay && ReplayVisible;
        if (replay)
            ReplayPanel.DrawSource(_drawnBounds);
        DrawTake(running, replay);
    }

    // The tab content scrolls in its own child, so the run buttons of the take bar stay on the screen on every tab.
    private static void DrawTake(ActiveTake? running, bool replay)
    {
        if (!replay)
            StatusHeader.Draw(Session);
        if (!Session.Validation.IsValid || Session.Validation.Warnings.Count > 0)
            DrawProblems();
        TakePanel.DrawBar(Session, replay);
        if (replay)
        {
            int shown = Math.Max(0, Array.IndexOf(ReplayTabs, _liveTab));
            int picked = Widgets.Tabs("cinecam-replay-take-tabs", ReplayTabNames, shown);
            _liveTab = ReplayTabs[picked >= 0 ? picked : shown];
        }
        else
        {
            int picked = Widgets.Tabs("cinecam-live-tabs", LiveTabNames, (int)_liveTab);
            if (picked >= 0)
                _liveTab = (LiveTab)picked;
        }

        Scroll("live", () =>
        {
            switch (_liveTab)
            {
                case LiveTab.Shots:
                    WithTake(TrackPanel.DrawShots);
                    break;
                case LiveTab.Speed:
                    WithTake(TrackPanel.DrawSpeed);
                    break;
                case LiveTab.Actions:
                    WithTake(TrackPanel.DrawActions);
                    break;
                case LiveTab.PauseEdit:
                    DrawEdit(running);
                    break;
                default:
                    TakePanel.DrawSetup(Session, Live, replay);
                    break;
            }
        });
    }

    // The child has no background, so the console panel shows through.
    private static void Scroll(string id, Action body)
    {
        ImGui.BeginChild((UiText.Concat("##cinecam-scroll-", id)).AsSpan(), new float2?(new float2(0f, 0f)), ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground);
        try
        {
            body();
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private static void WithTake(Action<EditSession> draw)
    {
        if (Session.Document == null)
            Widgets.Muted(UiText.MessageText("text.be781f7e10c1", "There is no take yet. A take is a shot list: the cameras of a flight, with the game speed and the stage actions. Pick New take in the Take list above."));
        else
            draw(Session);
    }

    private static void DrawEdit(ActiveTake? running)
    {
        if (running is { IsPaused: true })
        {
            RehearsalPanel.Draw(Session, running);
            return;
        }

        Widgets.Muted(UiText.Concat(UiText.MessageText("text.cb32d3b29076", "This tab works while a take is paused. Start a take, then press the edit key (see Settings) or Pause and edit. "), UiText.MessageText("text.94685cde7595", "The sim stops, the camera is free, and you can move the camera into a shot, add keys, and set when a shot, a speed key or a stage action starts.")));
        bool canPause = running is { Preview: null, Offline: null };
        if (Widgets.Button(UiText.MessageText("text.dcc0670ed57d", "PAUSE AND EDIT"), canPause, running == null ? UiText.MessageText("text.0a87acfbf4db", "Needs a running take.")
                : UiText.MessageText("text.dc511c8cb8d4", "An offline capture or a preview cannot be paused.")))
            TakeRunner.RequestPause();
        Widgets.Reason(canPause ? "" : running == null ? UiText.MessageText("text.580cc7c207c2", "Start a take first.") : UiText.MessageText("text.8f04df3052be", "This run cannot be paused."));
    }

    private static void DrawProblems()
    {
        ValidationResult result = Session.Validation;
        if (!ReferenceEquals(result, _problemsOf) || _problemsTake != Session.TakeIndex)
        {
            List<string> names = Session.Document?.Takes.Select(t => t.Name).ToList() ?? [];
            _problems = ProblemText.DescribeAll(result, names, Session.TakeIndex);
            _problemTips = _problems.Select(line => UiText.Concat(UiText.Concat((line.Item is ProblemItem.None ? "" : UiText.MessageText("text.e04bc1aef236", "Click to select it in the editor.\n\n")), UiText.MessageText("text.94afb96c07bd", "File reader: ")), line.Raw)).ToArray();
            _problemsOf = result;
            _problemsTake = Session.TakeIndex;
        }

        for (int i = 0; i < _problems.Count && i < MaxProblemLines; i++)
        {
            ProblemLine line = _problems[i];
            if (Widgets.LinkLine(ProblemIds[i], line.Sentence, line.IsError ? Widgets.ErrorColor : Widgets.WarnColor, _problemTips[i], warning: true))
                SelectProblem(line);
        }

        if (_problems.Count > MaxProblemLines)
        {
            int more = _problems.Count - MaxProblemLines;
            Widgets.Muted(more == 1 ? UiText.MessageText("text.e36fd38886e8", "1 more problem. Fix the ones above first.") : UiText.Format("text.db75275558d6", $"{more} more problems. Fix the ones above first."));
        }
    }

    private static void SelectProblem(ProblemLine line)
    {
        if (Session.Document is not { } file)
            return;
        if (line.TakeIndex >= 0 && line.TakeIndex < file.Takes.Count && line.TakeIndex != Session.TakeIndex)
            Session.SelectTake(line.TakeIndex);
        if (Session.Take is not { } take)
            return;
        int i = line.ItemIndex;
        switch (line.Item)
        {
            case ProblemItem.Shot when i >= 0 && i < take.Shots.Count:
                Session.ShotIndex = i;
                _liveTab = LiveTab.Shots;
                break;
            case ProblemItem.SpeedKey when i >= 0 && i < take.Speed.Count:
                Session.KeyIndex = i;
                _liveTab = LiveTab.Speed;
                break;
            case ProblemItem.Action when i >= 0 && i < take.Actions.Count:
                Session.ActionIndex = i;
                _liveTab = LiveTab.Actions;
                break;
            case ProblemItem.Take:
                _liveTab = line.Raw.Contains("preRoll", StringComparison.Ordinal) ? LiveTab.Shots : LiveTab.TakeSetup;
                break;
        }
    }

    #endregion
}
