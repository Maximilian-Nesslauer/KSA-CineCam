using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using CineCam.Core;
using CineCam.Director.Replay;
using CineCam.Features.Replay;
using KSA;

namespace CineCam.Features.Ui;

internal static class ReplayPanel
{
    private const int ListRowsShown = 6;
    private static readonly string[] StepsHint = { "1 Record a flight.", "2 Open it.", "3 Direct it with the timeline.", "4 Render." };

    private static readonly string[] ListHeads = ["Date", "Vehicle", "Length", "Size"];

    private static string? _selectedFile;
    private static string? _confirmDelete;
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
            if (Widgets.Button("STOP RECORDING", true, null, Widgets.Tone.Danger))
                ReplaySession.StopRecording();
        }
        else if (ReplaySession.IsPlayingBack)
        {
            Widgets.SameLine();
            if (Widgets.Button("EXIT REPLAY", true, "Closes the replay and goes back to the live flight."))
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
            return FormattableString.Invariant($"RECORDING  {ReplaySession.RecordingSeconds:F1} s");
        if (!ReplaySession.IsPlayingBack)
            return "READY";
        string state = ReplaySession.IsPlaying ? "PLAYING" : "PAUSED";
        return FormattableString.Invariant($"REPLAY {state}  {ReplaySession.Time:F1} / {ReplaySession.Duration:F1} s");
    }

    #region No replay open

    private static void DrawLibrary()
    {
        int frame = ImGui.GetFrameCount();
        if (_libraryFrame == int.MinValue || frame - _libraryFrame > 1)
        {
            _confirmDelete = null;
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

        if (entries.Count == 0)
        {
            Widgets.Muted("No recordings yet. Press Record flight while you fly, and Stop recording when the part that you want to film is over.");
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
            if (Widgets.Button("STOP RECORDING", true, "Stops the recording and saves it as a new recording.", Widgets.Tone.Danger))
                ReplaySession.StopRecording();
        }
        else if (ReplaySession.HasUnsavedFlight)
        {
            if (Widgets.Button("RETRY SAVE", true, "The last flight is still in memory, because it could not be saved."))
                ReplaySession.RetrySave();
        }
        else
        {
            string? reason = ReplaySession.StartBlockReason;
            if (Widgets.Button("RECORD FLIGHT", string.IsNullOrEmpty(reason), reason ?? RecordTip(), Widgets.Tone.Primary))
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
        float[] columns = [inset, size * 9.5f, Math.Max(size * 15f, width - size * 9f), Math.Max(size * 19f, width - size * 4.5f)];
        Widgets.ListHeader(ListHeads, columns);
        float rowH = ConsoleStyle.FrameHeightPx + ImGui.GetStyle().ItemSpacing.Y;
        float height = rowH * Math.Min(entries.Count, ListRowsShown);
        ImGui.BeginChild("##cinecam-recordings".AsSpan(), new float2?(new float2(0f, height)), ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground);
        try
        {
            for (int i = 0; i < entries.Count; i++)
            {
                RecordingEntry entry = entries[i];
                if (Widgets.ListRow("recording-" + i, _cells[i], columns, entry.Path == _selectedFile, false, entry.Error != null ? 1 : -1))
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
            text += $"\nKSA {header.GameVersion}, {header.SystemId}, {header.BodyId}";
        else if (entry.Error != null)
            text += "\nThe file cannot be read: " + entry.Error;
        return text;
    }

    private static void DrawListButtons(IReadOnlyList<RecordingEntry> entries)
    {
        RecordingEntry? selected = entries.FirstOrDefault(e => e.Path == _selectedFile);
        string? openReason = ReplaySession.IsRecording ? "Stop the recording first."
            : selected == null ? "Select a recording in the list."
            : selected.Header is { } header && header.GameVersion.Length > 0 && header.GameVersion != VersionInfo.Current.VersionString
                ? $"This recording needs KSA {header.GameVersion}."
            : ReplaySession.OpenBlockReason;
        if (Widgets.Button("OPEN REPLAY", string.IsNullOrEmpty(openReason), openReason
                ?? "Opens the recording and plays the selected take of the editor on it.", Widgets.Tone.Primary))
            ReplaySession.Open(selected!.Path);

        Widgets.SameLine();
        string? deleteReason = ReplaySession.IsRecording ? "Stop the recording first." : selected == null ? "Select a recording in the list." : null;
        if (_confirmDelete == null || selected == null)
        {
            if (Widgets.Button("DELETE", deleteReason == null, deleteReason ?? "Deletes the selected recording file. You confirm it with a second click."))
            {
                _confirmDelete = selected!.Path;
                _deleteMessage = "";
            }
        }
        else
        {
            if (Widgets.Button("CONFIRM DELETE", deleteReason == null, "Deletes the file of this recording for good.", Widgets.Tone.Danger))
            {
                _deleteMessage = ReplaySession.DeleteRecording(_confirmDelete) ?? "";
                _confirmDelete = null;
            }

            Widgets.SameLine();
            if (Widgets.Button("CANCEL##delete", true, "Keeps the recording."))
                _confirmDelete = null;
        }

        Widgets.Reason(openReason ?? "");
        if (_confirmDelete != null && selected != null)
            Widgets.Colored($"Delete {RecordingList.DisplayName(selected)} ({RecordingList.SizeText(selected.Bytes)})?", Widgets.WarnColor);
        if (_deleteMessage.Length > 0)
            Widgets.Colored(_deleteMessage, Widgets.ErrorColor);
    }

    private static string RecordTip() => ReplaySession.SelectedVehicleName is { Length: > 0 } vehicle
        ? $"Records the flight of {vehicle} until you stop it."
        : "Records the flight of the controlled vehicle until you stop it.";

    #endregion

    #region Replay open

    private static void DrawOpen((float2 Position, float2 Size)? window)
    {
        DrawStatus();
        if (Widgets.Button("EXIT REPLAY", true, "Closes the replay and goes back to the live flight."))
            ReplaySession.Close();
        if (window is { } bounds && !LiveTimeline.FitsBeside(bounds))
        {
            Widgets.SameLine();
            if (Widgets.Button("CLOSE WINDOW, SHOW TIMELINE", true,
                    "The timeline with the replay controls has no room beside this window. This closes the window, and the window key opens it again."))
                CineCamWindow.Instance.Close();
        }

        Widgets.SameLine();
        if (Widgets.Button("HIDE INTERFACE", ReplaySession.CanHideInterface,
                ReplaySession.CanHideInterface ? "Hides the game interface and this window, for a clean picture. " + KeyHelp()
                : "Set a key for \"Open or close this window\" on the Settings page first. That key shows the interface again."))
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
        if (session.Validation.IsValid && ReplaySession.TakeProblem.Length > 0)
            Widgets.Colored(ReplaySession.TakeProblem, Widgets.WarnColor);
    }

    private static void DrawRender()
    {
        string? renderBlock = ReplaySession.RenderBlockReason;
        if (Widgets.Button("RENDER VIDEO", renderBlock == null, renderBlock ?? "Saves one picture per frame of the whole take, at the pictures per second and into the output folder of the Video settings.", Widgets.Tone.Primary))
            ReplaySession.StartRender(ReplayRenderRange.Whole);
        Widgets.SameLine();
        if (Widgets.Button("RENDER FROM THIS TIME", renderBlock == null, renderBlock ?? "Renders from the replay time to the end of the take."))
            ReplaySession.StartRender(ReplayRenderRange.FromCurrent);
        Widgets.Reason(renderBlock ?? "");

        string text = ReplaySession.RenderStatusText;
        if (text.Length == 0)
            return;
        Widgets.Muted(text);
        if (!ReplaySession.IsRendering && ReplaySession.RenderStatus is { Folder.Length: > 0 } last)
        {
            if (Widgets.Button("COPY FOLDER PATH", true, "Copies the path of the render folder, to paste it into the file explorer:\n" + last.Folder))
                ImGui.SetClipboardText(last.Folder);
        }
    }

    private static string KeyHelp()
    {
        var parts = new List<string>();
        if (CineCamSettings.PlayPauseKey.HasValue)
            parts.Add($"{KeyName(CineCamSettings.PlayPauseKey)} plays and pauses");
        if (CineCamSettings.StartStopKey.HasValue)
            parts.Add($"{KeyName(CineCamSettings.StartStopKey)} closes the replay");
        if (CineCamSettings.WindowKey.HasValue)
            parts.Add($"{KeyName(CineCamSettings.WindowKey)} shows the interface again");
        return parts.Count == 0 ? "No keys are set for the replay. Set them on the Settings page." : "Keys: " + string.Join(", ", parts) + ".";
    }

    private static string KeyName(GlfwKey? key) => key.HasValue ? key.Value.ToString() : "";

    private static void DrawFreeCamera()
    {
        var flight = ReplaySession.Flight;
        string selected = flight?.Actors.FirstOrDefault(actor => ReplaySession.FollowActorId == actor.Id)?.Name ?? "Nothing";
        if (Widgets.BeginComboRow("Free camera follows", selected))
        {
            try
            {
                if (ImGui.Selectable("Nothing".AsSpan(), ReplaySession.FollowActorId == null))
                    ReplaySession.Follow(null);
                if (flight != null)
                {
                    foreach (var actor in flight.Actors)
                    {
                        ImGui.PushID(actor.Id);
                        try
                        {
                            bool available = ReplaySession.CanFollow(actor.Id);
                            ImGui.BeginDisabled(!available);
                            try
                            {
                                if (ImGui.Selectable(actor.Name.AsSpan(), ReplaySession.FollowActorId == actor.Id))
                                    ReplaySession.Follow(actor.Id);
                            }
                            finally { ImGui.EndDisabled(); }
                            Widgets.Tip(available ? null : "This stage is not present at the current time.");
                        }
                        finally { ImGui.PopID(); }
                    }
                }
            }
            finally
            {
                Widgets.EndComboRow();
            }
        }

        double fov = ReplaySession.FieldOfView;
        if (Widgets.Num("Field of view", ref fov, 0.1f, 1, ReplaySession.MaximumFieldOfView, "%.1f deg"))
            ReplaySession.SetFov(fov);
    }

    #endregion
}
