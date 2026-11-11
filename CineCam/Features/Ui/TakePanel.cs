using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using CineCam.Core;
using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Editing;
using CineCam.Features.Replay;
using CineCam.Features.Takes;
using CineCam.Features.TimeOfDay;
using CineCam.Patches;
using KSA;

namespace CineCam.Features.Ui;

internal static class TakePanel
{
    // In a replay the transport is in the dock, so the bar has no run buttons.
    public static void DrawBar(EditSession session, bool replay)
    {
        DrawTakePicker(session);
        if (!replay)
            DrawRun(session);
        DrawSave(session, replay);
        DrawRunNotes(session, replay);
    }

    public static void DrawSetup(EditSession session, LiveEvents live, bool replay)
    {
        if (session.Document == null)
        {
            Widgets.Muted("There is no take yet. Press New take above.");
            if (CineCamSettings.DeveloperMode)
                DrawFileSection(session);
            return;
        }

        Widgets.Section("Take");
        DrawTakeManage(session);
        if (replay)
        {
            Widgets.Section("Events of the recording");
            DrawRecordingEvents();
        }
        else
        {
            Widgets.Section("Checkpoint");
            DrawCheckpoint(session);
            Widgets.Section("Events seen on the vehicle");
            DrawEvents(session, live);
        }
        if (CineCamSettings.DeveloperMode)
        {
            ImGui.Spacing();
            DrawFileSection(session);
        }
    }

    private static void DrawSave(EditSession session, bool replay)
    {
        if (!replay)
            Widgets.SameLine();
        if (Widgets.Button("SAVE", session.Document != null && session.Dirty, session.Dirty
                ? "Writes the takes to the file of the mod. The old file stays as a .bak copy."
                : "Nothing to save. The takes are as in the file."))
        {
            if (session.Save())
                ShotFileStore.PathOverride = session.Path;
        }

        Widgets.SameLine();
        string fileBlock = ViewText.FileEditBlock(TakeRunner.Current != null);
        bool canRevert = session.Dirty && fileBlock.Length == 0 && File.Exists(session.Path);
        if (Widgets.Button("REVERT", canRevert, fileBlock.Length > 0 ? fileBlock
                : session.Dirty ? "Drops the unsaved changes and reads the saved takes again." : "Nothing to revert."))
            LoadPath(session, session.Path);
        if (session.Dirty)
        {
            Widgets.SameLine();
            Widgets.Label("unsaved changes", Widgets.WarnColor);
        }
    }

    // A message that is no error goes to the footer.
    private static void DrawRunNotes(EditSession session, bool replay)
    {
        if (!replay)
        {
            Widgets.Reason(_startBlock);
            if (ReplaySession.IsRecording)
                Widgets.Muted("The flight records. A take can run during the recording, and the recording goes on after it.");
            else if (CineCamSettings.ExperimentalReplay && CineCamSettings.RecordWithTake)
                Widgets.Muted("The flight records with the take, see the Settings page.");
        }

        if (session.Message.Length > 0 && session.MessageIsError)
            Widgets.Colored(session.Message, Widgets.ErrorColor);
    }

    private static void DrawFileSection(EditSession session)
    {
        if (Widgets.Fold("File (developer)", "take-file"))
            DrawFile(session);
    }

    private static void DrawFile(EditSession session)
    {
        string path = session.Path;
        if (Widgets.Text("File", "file-path", path, out string edited))
            session.Path = edited;

        string fileBlock = ViewText.FileEditBlock(TakeRunner.Current != null);
        bool canLoad = fileBlock.Length == 0;
        if (Widgets.Button("LOAD", canLoad, canLoad ? "Reads the file at the path above." : fileBlock))
            LoadPath(session, session.Path);
        Widgets.SameLine();
        if (Widgets.Button("RELOAD", session.Document != null && canLoad, canLoad ? "Reads the file again and drops the changes of the window." : fileBlock))
            LoadPath(session, session.Path);
        Widgets.SameLine();
        if (Widgets.Button("SAVE", session.Document != null, "Writes the file. A file with problems is not written."))
        {
            if (session.Save())
                ShotFileStore.PathOverride = session.Path;
        }

        Widgets.SameLine();
        if (Widgets.Button("DEFAULT PATH", true, "Puts the path of the stock location into the field. Load reads it."))
            session.Path = CineCamPaths.ShotFile;

        if (session.Dirty)
        {
            Widgets.SameLine();
            Widgets.Colored("unsaved changes", Widgets.WarnColor);
        }
    }

    private static void LoadPath(EditSession session, string path)
    {
        if (!File.Exists(path))
        {
            session.SetMessage($"There is no file at {path}. Save to make it from the current shots.", true);
            return;
        }

        if (session.Load(path))
            ShotFileStore.PathOverride = path;
    }

    private const string NewTakeTip = "Adds a take with one chase shot that suits any vehicle, and selects it. " +
        "A take is a shot list: the cameras of a flight, with the game speed and the stage actions. Add more shots with Add key here or Add preset.";

    private static void DrawTakePicker(EditSession session)
    {
        string block = ViewText.FileEditBlock(TakeRunner.Current != null);
        if (session.Document is not { } file)
        {
            if (Widgets.Button("NEW TAKE", block.Length == 0, block.Length > 0 ? block : NewTakeTip, Widgets.Tone.Primary))
                session.AddTake();
            return;
        }

        Take? take = session.Take;
        string preview = take == null ? "" : take.Name + (take.IsDefault ? " (default)" : "");
        if (!Widgets.BeginComboRow("Take##picker", preview, "The take that the editor shows and that Start take runs. The last item of the list makes a new take."))
            return;
        try
        {
            for (int i = 0; i < file.Takes.Count; i++)
            {
                ImGui.PushID(i);
                try
                {
                    string label = file.Takes[i].Name + (file.Takes[i].IsDefault ? " (default)" : "");
                    if (ImGui.Selectable(label.AsSpan(), i == session.TakeIndex))
                        session.SelectTake(i);
                }
                finally { ImGui.PopID(); }
            }

            ImGui.Separator();
            ImGui.BeginDisabled(block.Length > 0);
            try
            {
                if (ImGui.Selectable("New take##cinecam-new-take".AsSpan(), false))
                    session.AddTake();
            }
            finally { ImGui.EndDisabled(); }
            Widgets.Tip(block.Length > 0 ? block : NewTakeTip);
        }
        finally
        {
            Widgets.EndComboRow();
        }
    }

    private static void DrawTakeManage(EditSession session)
    {
        ShotFile file = session.Document!;
        Take? take = session.Take;
        if (take == null)
            return;

        string block = ViewText.FileEditBlock(TakeRunner.Current != null);
        bool free = block.Length == 0;
        if (Widgets.Button("DUPLICATE", free, free ? "Copies the selected take. The copy has no checkpoint, because the save belongs to the original." : block))
            session.DuplicateTake();
        Widgets.SameLine();
        if (Widgets.Button("REMOVE", free && file.Takes.Count > 1, !free ? block : "The last take of a file cannot be removed.", Widgets.Tone.Danger))
            session.RemoveTake();
        Widgets.SameLine();
        if (Widgets.Button("MAKE DEFAULT", free && !take.IsDefault, !free ? block : "The default take starts when no name is given, for example from the start key."))
            session.MakeDefault();

        string key = "take-" + session.TakeIndex.ToString(CultureInfo.InvariantCulture);
        if (Widgets.Text("Name", key + "-name", take.Name, out string name) && name.Trim().Length > 0)
            session.RenameTake(name.Trim());
        if (CineCamSettings.DeveloperMode)
        {
            if (Widgets.Text("Site (Landmark id)", key + "-site", take.Site ?? "", out string site))
                session.SetTakeSite(site);
            Widgets.Tip("The id of a Landmark for the world point frame. Empty uses the site of the file, then the place that the mod finds.");
        }
    }

    private static void DrawCheckpoint(EditSession session)
    {
        Take? take = session.Take;
        if (take == null)
            return;

        ResetFlow flow = TakeRunner.Reset;
        Vehicle? vehicle = Program.ControlledVehicle;
        bool hasVehicle = vehicle != null && !vehicle.IsDisposed && Program.Editor == null;
        bool idle = TakeRunner.Current == null && !flow.IsActive && !SunWarp.IsRunning;
        string busy = CineCamPatches.FrameBlock ?? ViewText.CheckpointBlock(TakeRunner.Current != null, flow.IsActive, SunWarp.IsRunning);
        string saveBusy = busy.Length > 0 ? busy : ReplaySession.CheckpointBlock(reset: false);
        string resetBusy = busy.Length > 0 ? busy : ReplaySession.CheckpointBlock(reset: true);

        Widgets.Muted(take.Checkpoint == null
            ? "No checkpoint yet. Stand at the start of the flight and save one."
            : $"Game save: {take.Checkpoint}");

        if (Widgets.Button("SAVE CHECKPOINT", idle && saveBusy.Length == 0 && hasVehicle,
                saveBusy.Length > 0 ? saveBusy : !hasVehicle ? "No controlled vehicle in flight."
                : "Saves the game now as the checkpoint of this take. An earlier checkpoint of the take is replaced."))
            TakeRunner.RequestCheckpoint(take.Name);
        Widgets.SameLine();
        bool canReset = idle && resetBusy.Length == 0 && take.Checkpoint != null && session.Validation.IsValid;
        if (Widgets.Button("RESET TO CHECKPOINT", canReset,
                resetBusy.Length > 0 ? resetBusy : take.Checkpoint == null ? "Save a checkpoint first."
                : "Ends a running take, loads the checkpoint and makes the take ready."))
            TakeRunner.RequestReset(take.Name);
        if (flow.IsActive)
        {
            Widgets.SameLine();
            if (Widgets.Button("CANCEL"))
                TakeRunner.RequestStop();
        }

        bool autoStart = take.AutoStart;
        if (Widgets.Check("Start the take after a reset", ref autoStart))
            session.SetAutoStart(autoStart);
        Widgets.Tip("The game stays stopped after the load until the take starts. Without this, press Start take yourself.");
        if (take.AutoStart && CineCamPatches.FrameAvailable && CineCamPatches.CameraBlock is { } noStart)
            Widgets.Reason(noStart);
        if (take.AutoStart)
        {
            double delay = take.AutoStartDelay;
            if (Widgets.Num("Delay (s)##autostart", ref delay, 0.1f, 0.0, 600.0))
                session.SetAutoStartDelay(delay);
        }

        double? left = flow.SecondsToStart(TakeRunner.ClockSeconds);
        if (left.HasValue)
            Widgets.Colored(string.Create(CultureInfo.InvariantCulture, $"The take starts in {left.Value:F1} s."), Widgets.GoodColor);
        else if (flow.IsActive)
            Widgets.Colored("Reset: " + flow.Phase, Widgets.GoodColor);
        else if (flow.Outcome.Length > 0)
            Widgets.Colored(flow.Outcome, flow.Phase == ResetPhase.Failed ? Widgets.ErrorColor : Widgets.MutedColor);
        Widgets.Muted("A load clears the state of other mods. Arm them again before the flight.");
    }

    private static string _startBlock = "";

    public static void Reset() => _startBlock = "";

    private static readonly string[] LongLabels = ["START TAKE", "DRY RUN", "STOP", "PAUSE AND EDIT", "RESUME"];
    private static readonly string[] ShortLabels = ["START", "DRY RUN", "STOP", "PAUSE", "RESUME"];

    // The ### ids stay the same for the long and the short labels, so a held button keeps its state when the labels switch.
    private static void DrawRun(EditSession session)
    {
        ActiveTake? running = TakeRunner.Current;
        Vehicle? vehicle = Program.ControlledVehicle;
        bool hasVehicle = vehicle != null && !vehicle.IsDisposed && Program.Editor == null;
        string whyNot = CineCamPatches.CameraBlock ?? ViewText.StartBlock(running != null, TakeRunner.Reset.IsActive, hasVehicle, SunWarp.IsRunning,
            session.Take != null && session.Validation.IsValid);
        if (whyNot.Length == 0 && ReplaySession.IsPlayingBack)
            whyNot = "A replay is open. It plays the selected take.";
        _startBlock = whyNot;
        bool canStart = whyNot.Length == 0;
        string name = session.Take?.Name ?? "";
        bool paused = running is { IsPaused: true };
        string[] labels = RunRowWidth(LongLabels, paused) > ConsoleStyle.ContentAvailWidth() ? ShortLabels : LongLabels;

        string startTip = "Starts the take with its log, speed track and camera track."
            + (session.Dirty ? " The take uses the unsaved changes of this window." : "");
        if (Widgets.Button(labels[0] + "###run-start", canStart, canStart ? startTip : whyNot, Widgets.Tone.Primary))
            TakeRunner.RequestStart(name);
        Widgets.SameLine();
        if (Widgets.Button(labels[1] + "###run-dry", canStart, canStart ? "Runs the camera track only. The sim speed stays alone and no log is written." : whyNot))
            TakeRunner.RequestStart(name, dryRun: true);
        Widgets.SameLine();
        bool canStop = running != null || TakeRunner.Reset.IsActive;
        if (Widgets.Button(labels[2] + "###run-stop", canStop, canStop ? "Ends the take, the dry run or the preview." : "Nothing runs.", Widgets.Tone.Danger))
            TakeRunner.RequestStop();
        Widgets.SameLine();
        bool canPause = running is { Preview: null, Offline: null, IsPaused: false };
        if (paused)
        {
            if (Widgets.Button(labels[4] + "###run-resume", true, "Gives the camera back to the shot and the sim speed back to what it was."))
                TakeRunner.RequestResume();
        }
        else if (Widgets.Button(labels[3] + "###run-pause", canPause,
                 running == null ? "Needs a running take. The sim and the take clock stop and the camera is free."
                 : running.Offline != null ? "An offline capture cannot be paused."
                 : running.Preview != null ? "A preview has its own pause."
                 : "Stops the sim and the take clock and frees the camera. The same as the edit key."))
        {
            TakeRunner.RequestPause();
        }
    }

    private static float RunRowWidth(string[] labels, bool paused)
    {
        float gap = ImGui.GetStyle().ItemSpacing.X;
        return ConsoleWidgets.ButtonWidth(labels[0].AsSpan()) + ConsoleWidgets.ButtonWidth(labels[1].AsSpan())
            + ConsoleWidgets.ButtonWidth(labels[2].AsSpan()) + ConsoleWidgets.ButtonWidth(labels[paused ? 4 : 3].AsSpan()) + gap * 3f;
    }

    private static readonly string[] RecordingHeaders = { "Take (s)", "Event", "Flight (s)" };

    private sealed record RecordingRow(string Id, string[] Cells, double? Take, double Flight);

    private static IReadOnlyList<ReplayEventStatus>? _rowEvents;
    private static IReadOnlyList<ReplayMarkerStatus>? _rowMarkers;
    private static List<RecordingRow> _recordingRows = [];

    // The rows are made again only when the replay has new lists of events or marks, which it makes once per plan.
    private static List<RecordingRow> RecordingRows()
    {
        IReadOnlyList<ReplayEventStatus> events = ReplaySession.Events();
        IReadOnlyList<ReplayMarkerStatus> markers = ReplaySession.Markers();
        if (ReferenceEquals(events, _rowEvents) && ReferenceEquals(markers, _rowMarkers))
            return _recordingRows;

        var rows = new List<(double? Take, string Label, double Flight)>();
        foreach (var e in events)
            rows.Add((e.TakeTime, e.Label, e.RecordingTime));
        // The take time of a mark is clamped to the end of the take, so a mark after the end gets none.
        double recordingEnd = ReplaySession.Plan is { } plan ? plan.RecordingTime(plan.Duration) : 0.0;
        foreach (var m in markers)
            rows.Add((m.RecordingTime <= recordingEnd + 1e-9 ? m.TakeTime : null, m.Label, m.RecordingTime));
        rows.Sort((a, b) => a.Flight.CompareTo(b.Flight));

        _recordingRows = rows.Select((row, i) => new RecordingRow("recording-event-" + i,
            [row.Take is double t ? t.ToString("F2", CultureInfo.InvariantCulture) : "-", row.Label,
                row.Flight.ToString("F1", CultureInfo.InvariantCulture)], row.Take, row.Flight)).ToList();
        _rowEvents = events;
        _rowMarkers = markers;
        return _recordingRows;
    }

    private static void DrawRecordingEvents()
    {
        if (!ReplaySession.IsPlayingBack)
        {
            Widgets.Muted("Open a recording to see its events.");
            return;
        }

        List<RecordingRow> rows = RecordingRows();
        if (rows.Count == 0)
        {
            Widgets.Muted("The recording has no events.");
            return;
        }

        float size = ImGui.GetFontSize();
        float width = ConsoleStyle.ContentAvailWidth();
        float[] columns = { ConsoleWidgets.RowLabelInsetPx, size * 5.0f, Math.Max(size * 10.0f, width * 0.75f) };
        Widgets.ListHeader(RecordingHeaders, columns);
        foreach (RecordingRow row in rows)
        {
            if (Widgets.ListRow(row.Id, row.Cells, columns, false, false, row.Take.HasValue ? -1 : 0) && row.Take is double at)
                ReplaySession.Seek(Math.Clamp(at, 0.0, ReplaySession.Duration));
        }

        Widgets.Muted("Click an event to move the replay there. An event without a take time comes after the end of the take.");
    }

    private static readonly string[] EventHeaders = { "Time (s)", "Event", "State" };

    private static void DrawEvents(EditSession session, LiveEvents live)
    {
        ActiveTake? running = TakeRunner.Current;
        bool useTake = running != null && running.Preview == null;
        IReadOnlyDictionary<string, double> seen = useTake ? running!.Director.Events.Seen : live.Seen;
        IReadOnlyDictionary<string, double> predictions = useTake ? running!.Director.Events.Predictions : live.Predictions;
        string timeBase = useTake ? "take time" : "time since the window started to watch";

        if (!useTake && !live.HasVehicle)
        {
            Widgets.Muted("No controlled vehicle in flight.");
            return;
        }

        VehicleSample? sample = live.Sample;
        if (sample is { } s)
        {
            Widgets.Muted(string.Create(CultureInfo.InvariantCulture,
                $"engine {(s.EngineOn ? "on" : "off")}, contact {(s.Contact ? "yes" : "no")}, sequence {s.ActiveSequence}, altitude {s.Altitude:F0} m, speed {s.Speed:F1} m/s"));
        }

        var awaited = new List<string>();
        Take? take = session.Take;
        if (take != null && session.Document != null)
            awaited = EventDetector.Watched(take, session.Document.Events).Select(n => n.Canonical).ToList();
        List<EventRow> rows = ViewText.EventRows(seen, predictions, awaited);

        if (rows.Count == 0)
            Widgets.Muted("Nothing yet.");
        else
        {
            float size = ImGui.GetFontSize();
            float width = ConsoleStyle.ContentAvailWidth();
            float[] columns = { ConsoleWidgets.RowLabelInsetPx, size * 5.0f, size * 5.0f + (width - size * 5.0f) * 0.55f };
            Widgets.ListHeader(EventHeaders, columns);
            for (int i = 0; i < rows.Count; i++)
            {
                EventRow row = rows[i];
                Widgets.ListRow("event-" + i, [row.Time, Widgets.EventName(row.Name), row.Status], columns, false, row.Seen, row.Seen ? -1 : 2);
            }
        }

        Widgets.Muted($"Times are {timeBase}.");
        if (!useTake && Widgets.Button("CLEAR EVENTS", true, "Forgets the events seen and starts the clock again."))
            live.Reset();
    }
}
