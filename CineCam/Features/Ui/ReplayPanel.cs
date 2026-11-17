using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using CineCam.Core;
using CineCam.Director.Replay;
using CineCam.Features.Look;
using CineCam.Features.Replay;
using KSA;

namespace CineCam.Features.Ui;

internal static class ReplayPanel
{
    private const int ListRowsShown = 6;
    private static readonly string[] StepsHint = { UiText.MessageText("text.52001e35c686", "1 Record a flight."), UiText.MessageText("text.c9143aeb6a0f", "2 Open it."), UiText.MessageText("text.74ca6f31cb75", "3 Direct it with the timeline."), UiText.MessageText("text.672e9434a7fc", "4 Render.") };

    private static readonly string[] ListHeads = [UiText.MessageText("text.99c40ab40592", "Date"), UiText.MessageText("text.a62394ba4acc", "Vehicle"), UiText.MessageText("text.bdd254b65baa", "Build"), UiText.MessageText("text.adc95605a1b3", "Length"), UiText.MessageText("text.1af851907331", "Size")];

    private static string? _selectedFile;
    private static string? _confirmDelete;
    private static string? _confirmOlder;
    private static string _deleteMessage = "";

    // The cells are made again only when the list changes.
    private static int _cellsVersion = -1;
    private static string[][] _cells = [];

    // A delete confirm holds only while the library shows in every frame, so a replay, another page
    // or a closed window clears it.
    private static int _libraryFrame = int.MinValue;

    public static void Reset()
    {
        _selectedFile = null;
        _confirmDelete = null;
        _confirmOlder = null;
        _deleteMessage = "";
        _cellsVersion = -1;
        _cells = [];
        _libraryFrame = int.MinValue;
    }

    public static void DrawHeader()
    {
        Widgets.Label(StateText(), ReplaySession.IsBusy ? Widgets.GoodColor : Widgets.MutedColor);
        if (ReplaySession.IsRecording)
        {
            Widgets.SameLine();
            if (Widgets.Button(UiText.MessageText("text.3a93055fe99f", "STOP RECORDING"), true, null, Widgets.Tone.Danger))
                ReplaySession.StopRecording();
        }
        else if (ReplaySession.IsPlayingBack)
        {
            Widgets.SameLine();
            if (Widgets.Button(UiText.MessageText("text.8bd891fd01cb", "EXIT REPLAY"), true, UiText.MessageText("text.55466996d856", "Closes the replay and goes back to the live flight.")))
                ReplaySession.Close();
        }

        ConsoleWidgets.Rule();
    }

    // The window bounds tell whether the dock has room beside the window.
    public static void DrawSource((float2 Position, float2 Size)? window)
    {
        if (ReplaySession.IsPlayingBack)
            DrawOpen(window);
        else
            DrawLibrary();
        ConsoleWidgets.Rule();
    }

    private static string StateText()
    {
        if (ReplaySession.IsRecording)
            return UiText.Format("text.0e0bb60ce279", $"RECORDING  {ReplaySession.RecordingSeconds:F1} s");
        if (!ReplaySession.IsPlayingBack)
            return UiText.MessageText("text.c2e3ac47f4a3", "READY");
        string state = ReplaySession.IsPlaying ? UiText.MessageText("text.86042b89c731", "PLAYING") : UiText.MessageText("text.5b5f8d4f6743", "PAUSED");
        return UiText.Format("text.55df75f64af8", $"REPLAY {state}  {ReplaySession.Time:F1} / {ReplaySession.Duration:F1} s");
    }

    #region No replay open

    private static void DrawLibrary()
    {
        int frame = ImGui.GetFrameCount();
        if (_libraryFrame == int.MinValue || frame - _libraryFrame > 1)
        {
            _confirmDelete = null;
            _confirmOlder = null;
            _deleteMessage = "";
        }

        _libraryFrame = frame;
        Widgets.MutedSteps(StepsHint);
        DrawRecorder();
        IReadOnlyList<RecordingEntry> entries = ReplaySession.Recordings();
        if (_selectedFile == null || !entries.Any(e => e.Path == _selectedFile))
            _selectedFile = entries.Count > 0 ? entries[0].Path : null;
        if (_confirmDelete != null && _confirmDelete != _selectedFile)
            _confirmDelete = null;
        if (_confirmOlder != null && _confirmOlder != _selectedFile)
            _confirmOlder = null;

        if (entries.Count == 0)
        {
            Widgets.Muted(UiText.MessageText("text.632d3214fddc", "No recordings yet. Press Record flight while you fly, and Stop recording when the part that you want to film is over."));
        }
        else
        {
            DrawList(entries);
            DrawListButtons(entries);
        }

        if (!string.IsNullOrEmpty(ReplaySession.Status))
            Widgets.Muted(ReplaySession.Status);
    }

    private static void DrawRecorder()
    {
        if (ReplaySession.IsRecording)
        {
            Widgets.Label(StateText(), Widgets.GoodColor);
            Widgets.SameLine();
            if (Widgets.Button(UiText.MessageText("text.3a93055fe99f", "STOP RECORDING"), true, UiText.MessageText("text.33fd56c88683", "Stops the recording and saves it as a new recording."), Widgets.Tone.Danger))
                ReplaySession.StopRecording();
        }
        else if (ReplaySession.HasUnsavedFlight)
        {
            if (Widgets.Button(UiText.MessageText("text.4737fde6ff55", "RETRY SAVE"), true, UiText.MessageText("text.9afd4a592a63", "The last flight is still in memory, because it could not be saved.")))
                ReplaySession.RetrySave();
        }
        else
        {
            string? reason = ReplaySession.StartBlockReason;
            if (Widgets.Button(UiText.MessageText("text.ec8bb18f4996", "RECORD FLIGHT"), string.IsNullOrEmpty(reason), reason ?? RecordTip(), Widgets.Tone.Primary))
                ReplaySession.StartRecording();
            Widgets.Reason(reason ?? "");
        }
    }

    private static void DrawList(IReadOnlyList<RecordingEntry> entries)
    {
        if (_cellsVersion != ReplaySession.RecordingsVersion || _cells.Length != entries.Count)
        {
            _cells = entries.Select(RecordingList.Cells).ToArray();
            _cellsVersion = ReplaySession.RecordingsVersion;
        }

        float size = ImGui.GetFontSize();
        float width = ConsoleStyle.ContentAvailWidth();
        float inset = ConsoleWidgets.RowLabelInsetPx;
        float[] columns = [inset, size * 9.5f, Math.Max(size * 15f, width - size * 13f), Math.Max(size * 19f, width - size * 9f),
            Math.Max(size * 23f, width - size * 4.5f)];
        Widgets.ListHeader(ListHeads, columns);
        float rowH = ConsoleStyle.FrameHeightPx + ImGui.GetStyle().ItemSpacing.Y;
        float height = rowH * Math.Min(entries.Count, ListRowsShown);
        ImGui.BeginChild("##cinecam-recordings".AsSpan(), new float2?(new float2(0f, height)), ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground);
        try
        {
            for (int i = 0; i < entries.Count; i++)
            {
                RecordingEntry entry = entries[i];
                if (Widgets.ListRow(UiText.Concat("recording-", i), _cells[i], columns, entry.Path == _selectedFile, false, entry.Error != null ? 1 : -1))
                    _selectedFile = entry.Path;
                if (ImGui.IsItemHovered())
                    Widgets.Tip(RowTip(entry));
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private static string RowTip(RecordingEntry entry)
    {
        string text = entry.Id;
        if (entry.Header is { } header)
        {
            text = UiText.Concat(text, UiText.Format("text.a9a34768f794", $"\nKSA {header.GameVersion}, {header.SystemId}, {header.BodyId}"));
            string current = VersionInfo.Current.VersionString;
            text = UiText.Concat(text, ReplayBuild.Compare(header.GameVersion, current) switch
            {
                ReplayBuildAge.Same => UiText.MessageText("text.62323cc960ad", "\nRecorded on the build of this game."),
                ReplayBuildAge.Older => UiText.Format("text.c05156f81af4", $"\nRecorded on an older build than this game ({current}). It opens after a confirm."),
                _ => UiText.Format("text.6aa727adec41", $"\nThis game ({current}) cannot open it."),
            });
        }
        else if (entry.Error != null)
            text = UiText.Concat(text, UiText.Concat(UiText.MessageText("text.48c1315cd966", "\nThe file cannot be read: "), entry.Error));
        return text;
    }

    // A recording of an older game build opens with a second click on a confirm, as a delete does.
    private static void DrawListButtons(IReadOnlyList<RecordingEntry> entries)
    {
        RecordingEntry? selected = entries.FirstOrDefault(e => e.Path == _selectedFile);
        string current = VersionInfo.Current.VersionString;
        ReplayBuildAge age = selected?.Header is { } header ? ReplayBuild.Compare(header.GameVersion, current) : ReplayBuildAge.Same;
        string? openReason = ReplaySession.IsRecording ? UiText.MessageText("text.bceedc4c4eda", "Stop the recording first.")
            : selected == null ? UiText.MessageText("text.5365173884d9", "Select a recording in the list.")
            : age is ReplayBuildAge.Newer or ReplayBuildAge.Unknown ? ReplayBuild.OpenBlock(selected.Header!.GameVersion, current, true)?.Message
            : ReplaySession.OpenBlockReason;
        bool older = age == ReplayBuildAge.Older && selected != null;
        if (!older)
            _confirmOlder = null;
        if (_confirmOlder != null && string.IsNullOrEmpty(openReason))
        {
            if (Widgets.Button(UiText.MessageText("text.a4d3916b21ae", "CONFIRM OPEN"), true, UiText.MessageText("text.4a902ffe503e", "Opens the recording of the older build."), Widgets.Tone.Primary))
            {
                ReplaySession.Open(_confirmOlder, true);
                _confirmOlder = null;
            }

            Widgets.SameLine();
            if (Widgets.Button(UiText.MessageText("text.646608e6b907", "CANCEL##open"), true, UiText.MessageText("text.c08d773e64fa", "Keeps the replay closed.")))
                _confirmOlder = null;
        }
        else if (older)
        {
            if (Widgets.Button(UiText.MessageText("text.b27beaccb83e", "OPEN OLDER RECORDING"), string.IsNullOrEmpty(openReason), openReason
                    ?? UiText.MessageText("text.e7cbd4a6e985", "This recording is from an older game build. You confirm the open with a second click."), Widgets.Tone.Primary))
                _confirmOlder = selected!.Path;
        }
        else if (Widgets.Button(UiText.MessageText("text.80e21fe9dab5", "OPEN REPLAY"), string.IsNullOrEmpty(openReason), openReason
                ?? UiText.MessageText("text.81aa533731a2", "Opens the recording and plays the selected take of the editor on it."), Widgets.Tone.Primary))
        {
            ReplaySession.Open(selected!.Path);
        }

        Widgets.SameLine();
        string? deleteReason = ReplaySession.IsRecording ? UiText.MessageText("text.bceedc4c4eda", "Stop the recording first.") : selected == null ? UiText.MessageText("text.5365173884d9", "Select a recording in the list.") : null;
        if (_confirmDelete == null || selected == null)
        {
            if (Widgets.Button(UiText.MessageText("text.65daeb378bde", "DELETE"), deleteReason == null, deleteReason ?? UiText.MessageText("text.7546bc2ff5f0", "Deletes the selected recording file. You confirm it with a second click.")))
            {
                _confirmDelete = selected!.Path;
                _deleteMessage = "";
            }
        }
        else
        {
            if (Widgets.Button(UiText.MessageText("text.1907dc9aff8b", "CONFIRM DELETE"), deleteReason == null, UiText.MessageText("text.29ba3ea67777", "Deletes the file of this recording for good."), Widgets.Tone.Danger))
            {
                _deleteMessage = ReplaySession.DeleteRecording(_confirmDelete) ?? "";
                _confirmDelete = null;
            }

            Widgets.SameLine();
            if (Widgets.Button(UiText.MessageText("text.389bd79c0dc2", "CANCEL##delete"), true, UiText.MessageText("text.2a91fee14264", "Keeps the recording.")))
                _confirmDelete = null;
        }

        Widgets.Reason(openReason ?? "");
        if (_confirmOlder != null && selected?.Header is { } olderHeader)
            Widgets.Colored(ReplayBuild.OlderWarning(olderHeader.GameVersion, current), Widgets.WarnColor);
        if (_confirmDelete != null && selected != null)
            Widgets.Colored(UiText.Format("text.2f57935aeb2b", $"Delete {RecordingList.DisplayName(selected)} ({RecordingList.SizeText(selected.Bytes)})?"), Widgets.WarnColor);
        if (_deleteMessage.Length > 0)
            Widgets.Colored(_deleteMessage, Widgets.ErrorColor);
    }

    private static string RecordTip() => ReplaySession.SelectedVehicleName is { Length: > 0 } vehicle
        ? UiText.Format("text.ddce142998f4", $"Records the flight of {vehicle} until you stop it.")
        : UiText.MessageText("text.d9345bb81cc9", "Records the flight of the controlled vehicle until you stop it.");

    #endregion

    #region Replay open

    private static void DrawOpen((float2 Position, float2 Size)? window)
    {
        DrawStatus();
        if (Widgets.Button(UiText.MessageText("text.8bd891fd01cb", "EXIT REPLAY"), true, UiText.MessageText("text.55466996d856", "Closes the replay and goes back to the live flight.")))
            ReplaySession.Close();
        if (window is { } bounds && !LiveTimeline.FitsBeside(bounds))
        {
            Widgets.SameLine();
            if (Widgets.Button(UiText.MessageText("text.3f3e212178b0", "CLOSE WINDOW, SHOW TIMELINE"), true,
                    UiText.MessageText("text.4a847001bd9c", "The timeline with the replay controls has no room beside this window. This closes the window, and the window key opens it again.")))
                CineCamWindow.Instance.Close();
        }

        Widgets.SameLine();
        if (Widgets.Button(UiText.MessageText("text.3bba856e1544", "HIDE INTERFACE"), ReplaySession.CanHideInterface,
                ReplaySession.CanHideInterface ? UiText.Concat(UiText.MessageText("text.19fb0cfa10f3", "Hides the game interface and this window, for a clean picture. "), KeyHelp())
                : UiText.MessageText("text.f32da6cda0e3", "Set a key for \"Open or close this window\" on the Settings page first. That key shows the interface again.")))
            ReplaySession.ToggleInterface();

        DrawRender();
        if (ReplaySession.CameraMode == ReplayCameraMode.Free)
            DrawFreeCamera();
    }

    // The editor lists the errors of the take, so this names only the other problems.
    private static void DrawStatus()
    {
        var session = CineCamWindow.EditorSession;
        StatusHeader.DrawReplay(session);
        string warning = ReplaySession.OpenWarning;
        if (warning.Length > 0)
            Widgets.Colored(warning, Widgets.WarnColor);
        else
            Widgets.Muted(ReplayBuild.BuildLine(ReplaySession.RecordingBuild, VersionInfo.Current.VersionString));
        if (session.Validation.IsValid && ReplaySession.TakeProblem.Length > 0)
            Widgets.Colored(ReplaySession.TakeProblem, Widgets.WarnColor);
    }

    private static void DrawRender()
    {
        string? renderBlock = ReplaySession.RenderBlockReason;
        if (Widgets.Button(UiText.MessageText("text.19ea3a1259c0", "RENDER VIDEO"), renderBlock == null, renderBlock ?? UiText.MessageText("text.5a17121dee1b", "Saves one picture per frame of the whole take, at the pictures per second and into the output folder of the Video settings."), Widgets.Tone.Primary))
            ReplaySession.StartRender(ReplayRenderRange.Whole);
        Widgets.SameLine();
        if (Widgets.Button(UiText.MessageText("text.9484a2e0950e", "RENDER FROM THIS TIME"), renderBlock == null, renderBlock ?? UiText.MessageText("text.508c87db7819", "Renders from the replay time to the end of the take.")))
            ReplaySession.StartRender(ReplayRenderRange.FromCurrent);
        Widgets.SameLine();
        string? stillBlock = StillCapture.BlockReason;
        if (Widgets.Button(UiText.MessageText("text.e4626f6cb291", "TAKE STILL"), stillBlock == null, stillBlock ?? UiText.Format("text.abd0da2a417d", $"Pauses the replay and saves one picture of this view at {CineCamSettings.StillScale} times the window size into the stills folder. The size is on the Settings page.")))
            StillCapture.Request(null);
        Widgets.Reason(renderBlock ?? "");
        if (StillCapture.Status.Length > 0)
            Widgets.Muted(StillCapture.Status);

        string text = ReplaySession.RenderStatusText;
        if (text.Length == 0)
            return;
        Widgets.Muted(text);
        if (!ReplaySession.IsRendering && ReplaySession.RenderStatus is { Folder.Length: > 0 } last)
        {
            if (Widgets.Button(UiText.MessageText("text.e823a2075509", "COPY FOLDER PATH"), true, UiText.Concat(UiText.MessageText("text.5f61cb750369", "Copies the path of the render folder, to paste it into the file explorer:\n"), last.Folder)))
                ImGui.SetClipboardText(last.Folder);
        }
    }

    private static string KeyHelp()
    {
        var parts = new List<string>();
        if (CineCamSettings.PlayPauseKey.HasValue)
            parts.Add(UiText.Format("text.bb69017adb44", $"{KeyName(CineCamSettings.PlayPauseKey)} plays and pauses"));
        if (CineCamSettings.StartStopKey.HasValue)
            parts.Add(UiText.Format("text.504f02cfe369", $"{KeyName(CineCamSettings.StartStopKey)} closes the replay"));
        if (CineCamSettings.WindowKey.HasValue)
            parts.Add(UiText.Format("text.2a05b9ad7080", $"{KeyName(CineCamSettings.WindowKey)} shows the interface again"));
        return parts.Count == 0 ? UiText.MessageText("text.e44b33a7183f", "No keys are set for the replay. Set them on the Settings page.") : UiText.Concat(UiText.Concat(UiText.MessageText("text.8ed6153fc892", "Keys: "), UiText.Join(", ", parts)), ".");
    }

    private static string KeyName(GlfwKey? key) => key.HasValue ? key.Value.ToString() : "";

    private static void DrawFreeCamera()
    {
        var flight = ReplaySession.Flight;
        string selected = flight?.Actors.FirstOrDefault(actor => ReplaySession.FollowActorId == actor.Id)?.Name ?? UiText.MessageText("text.67e1bca0889b", "Nothing");
        if (Widgets.BeginComboRow(UiText.MessageText("text.f9d6a0ff7cfd", "Free camera follows"), selected))
        {
            try
            {
                if (ImGui.Selectable(UiText.Control(UiText.MessageText("text.67e1bca0889b", "Nothing")).AsSpan(), ReplaySession.FollowActorId == null))
                    ReplaySession.Follow(null);
                if (flight != null)
                {
                    foreach (var actor in flight.Actors)
                    {
                        bool available = ReplaySession.CanFollow(actor.Id);
                        ImGui.BeginDisabled(!available);
                        try
                        {
                            if (Widgets.SelectableText(actor.Id, actor.Name, ReplaySession.FollowActorId == actor.Id))
                                ReplaySession.Follow(actor.Id);
                        }
                        finally { ImGui.EndDisabled(); }
                        Widgets.Tip(available ? null : UiText.MessageText("text.4450dfce1484", "This stage is not present at the current time."));
                    }
                }
            }
            finally
            {
                Widgets.EndComboRow();
            }
        }

        double fov = ReplaySession.FieldOfView;
        if (Widgets.Num(UiText.MessageText("text.1fab91bbb0d3", "Field of view"), ref fov, 0.1f, 1, ReplaySession.MaximumFieldOfView, "%.1f deg"))
            ReplaySession.SetFov(fov);
    }

    #endregion
}
