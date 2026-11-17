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
            Widgets.Muted(UiText.MessageText("text.7a9e3a3fa7b2", "There is no take yet. Press New take above."));
            if (CineCamSettings.DeveloperMode)
                DrawFileSection(session);
            return;
        }

        Widgets.Section(UiText.MessageText("text.8b862ba45dce", "Take"));
        DrawTakeManage(session);
        LookPanel.DrawTake(session);
        if (replay)
        {
            Widgets.Section(UiText.MessageText("text.02ccb2da2fe0", "Events of the recording"));
            DrawRecordingEvents();
        }
        else
        {
            Widgets.Section(UiText.MessageText("text.ee325afd9b4e", "Checkpoint"));
            DrawCheckpoint(session);
            Widgets.Section(UiText.MessageText("text.314d476b2204", "Events seen on the vehicle"));
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
        if (Widgets.Button(UiText.MessageText("text.c210bfdf28ce", "SAVE"), session.Document != null && session.Dirty, session.Dirty
                ? UiText.MessageText("text.01c30a7e3020", "Writes the takes to the file of the mod. The old file stays as a .bak copy.")
                : UiText.MessageText("text.6e7edf4d50a2", "Nothing to save. The takes are as in the file.")))
        {
            if (session.Save())
                ShotFileStore.PathOverride = session.Path;
        }

        Widgets.SameLine();
        string fileBlock = ViewText.FileEditBlock(TakeRunner.Current != null);
        bool canRevert = session.Dirty && fileBlock.Length == 0 && File.Exists(session.Path);
        if (Widgets.Button(UiText.MessageText("text.0607a789d22d", "REVERT"), canRevert, fileBlock.Length > 0 ? fileBlock
                : session.Dirty ? UiText.MessageText("text.d7f76487ff82", "Drops the unsaved changes and reads the saved takes again.") : UiText.MessageText("text.07195d59b0e2", "Nothing to revert.")))
            LoadPath(session, session.Path);
        if (session.Dirty)
        {
            Widgets.SameLine();
            Widgets.Label(UiText.MessageText("text.c4751a7ab84a", "unsaved changes"), Widgets.WarnColor);
        }
    }

    // A message that is no error goes to the footer.
    private static void DrawRunNotes(EditSession session, bool replay)
    {
        if (!replay)
        {
            Widgets.Reason(_startBlock);
            if (ReplaySession.IsRecording)
                Widgets.Muted(UiText.MessageText("text.0d257f36bc70", "The flight records. A take can run during the recording, and the recording goes on after it."));
            else if (CineCamSettings.ExperimentalReplay && CineCamSettings.RecordWithTake)
                Widgets.Muted(UiText.MessageText("text.66f4752ceee0", "The flight records with the take, see the Settings page."));
        }

        if (session.Message.Length > 0 && session.MessageIsError)
            Widgets.Colored(session.Message, Widgets.ErrorColor);
    }

    private static void DrawFileSection(EditSession session)
    {
        if (Widgets.Fold(UiText.MessageText("text.b8e59037981b", "File (developer)"), "take-file"))
            DrawFile(session);
    }

    private static void DrawFile(EditSession session)
    {
        string path = session.Path;
        if (Widgets.Text(UiText.MessageText("text.50009ce1da4d", "File"), "file-path", path, out string edited))
            session.Path = edited;

        string fileBlock = ViewText.FileEditBlock(TakeRunner.Current != null);
        bool canLoad = fileBlock.Length == 0;
        if (Widgets.Button(UiText.MessageText("text.d438686e54df", "LOAD"), canLoad, canLoad ? UiText.MessageText("text.d241555e4298", "Reads the file at the path above.") : fileBlock))
            LoadPath(session, session.Path);
        Widgets.SameLine();
        if (Widgets.Button(UiText.MessageText("text.8229c4ee6826", "RELOAD"), session.Document != null && canLoad, canLoad ? UiText.MessageText("text.70a4c58d2987", "Reads the file again and drops the changes of the window.") : fileBlock))
            LoadPath(session, session.Path);
        Widgets.SameLine();
        if (Widgets.Button(UiText.MessageText("text.c210bfdf28ce", "SAVE"), session.Document != null, UiText.MessageText("text.d2c26d392f74", "Writes the file. A file with problems is not written.")))
        {
            if (session.Save())
                ShotFileStore.PathOverride = session.Path;
        }

        Widgets.SameLine();
        if (Widgets.Button(UiText.MessageText("text.ba46bbeabe77", "DEFAULT PATH"), true, UiText.MessageText("text.d5a6b27eb0ba", "Puts the path of the stock location into the field. Load reads it.")))
            session.Path = CineCamPaths.ShotFile;

        if (session.Dirty)
        {
            Widgets.SameLine();
            Widgets.Colored(UiText.MessageText("text.c4751a7ab84a", "unsaved changes"), Widgets.WarnColor);
        }
    }

    private static void LoadPath(EditSession session, string path)
    {
        if (!File.Exists(path))
        {
            session.SetMessage(UiText.Format("text.b81180be6e57", $"There is no file at {path}. Save to make it from the current shots."), true);
            return;
        }

        if (session.Load(path))
            ShotFileStore.PathOverride = path;
    }

    private static readonly string NewTakeTip = UiText.Concat(UiText.MessageText("text.460bc5353a08", "Adds a take with one chase shot that suits any vehicle, and selects it. "), UiText.MessageText("text.c002a0a72a5b", "A take is a shot list: the cameras of a flight, with the game speed and the stage actions. Add more shots with Add key here or Add preset."));

    private static void DrawTakePicker(EditSession session)
    {
        string block = ViewText.FileEditBlock(TakeRunner.Current != null);
        if (session.Document is not { } file)
        {
            if (Widgets.Button(UiText.MessageText("text.a5735683a398", "NEW TAKE"), block.Length == 0, block.Length > 0 ? block : NewTakeTip, Widgets.Tone.Primary))
                session.AddTake();
            return;
        }

        Take? take = session.Take;
        string preview = take == null ? "" : UiText.Concat(take.Name, (take.IsDefault ? UiText.MessageText("text.12fd6081e435", " (default)") : ""));
        if (!Widgets.BeginComboRow(UiText.MessageText("text.4a8b13c75149", "Take##picker"), preview, UiText.MessageText("text.ab68a0b9a4df", "The take that the editor shows and that Start take runs. The last item of the list makes a new take.")))
            return;
        try
        {
            for (int i = 0; i < file.Takes.Count; i++)
            {
                ImGui.PushID(i);
                try
                {
                    string label = UiText.Concat(file.Takes[i].Name, (file.Takes[i].IsDefault ? UiText.MessageText("text.12fd6081e435", " (default)") : ""));
                    if (ImGui.Selectable(UiText.Control(label).AsSpan(), i == session.TakeIndex))
                        session.SelectTake(i);
                }
                finally { ImGui.PopID(); }
            }

            ImGui.Separator();
            ImGui.BeginDisabled(block.Length > 0);
            try
            {
                if (ImGui.Selectable(UiText.Control(UiText.MessageText("text.9b6911fb14e4", "New take##cinecam-new-take")).AsSpan(), false))
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
        if (Widgets.Button(UiText.MessageText("text.a45dd8c57dcc", "DUPLICATE"), free, free ? UiText.MessageText("text.223f42370cc5", "Copies the selected take. The copy has no checkpoint, because the save belongs to the original.") : block))
            session.DuplicateTake();
        Widgets.SameLine();
        if (Widgets.Button(UiText.MessageText("text.5fdf0b2bbe27", "REMOVE"), free && file.Takes.Count > 1, !free ? block : UiText.MessageText("text.87bf5571010a", "The last take of a file cannot be removed."), Widgets.Tone.Danger))
            session.RemoveTake();
        Widgets.SameLine();
        if (Widgets.Button(UiText.MessageText("text.55259baa7ab1", "MAKE DEFAULT"), free && !take.IsDefault, !free ? block : UiText.MessageText("text.026164d03af1", "The default take starts when no name is given, for example from the start key.")))
            session.MakeDefault();

        string key = UiText.Concat("take-", session.TakeIndex.ToString(CultureInfo.InvariantCulture));
        if (Widgets.Text(UiText.MessageText("text.dcd1d5223f73", "Name"), UiText.Concat(key, "-name"), take.Name, out string name) && name.Trim().Length > 0)
            session.RenameTake(name.Trim());
        if (CineCamSettings.DeveloperMode)
        {
            if (Widgets.Text(UiText.MessageText("text.7ec65d09243f", "Site (Landmark id)"), UiText.Concat(key, "-site"), take.Site ?? "", out string site))
                session.SetTakeSite(site);
            Widgets.Tip(UiText.MessageText("text.e62984eb9663", "The id of a Landmark for the world point frame. Empty uses the site of the file, then the place that the mod finds."));
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
            ? UiText.MessageText("text.8d75d7c401c5", "No checkpoint yet. Stand at the start of the flight and save one.")
            : UiText.Format("text.8405e68fec35", $"Game save: {take.Checkpoint}"));

        if (Widgets.Button(UiText.MessageText("text.6910fdb69dbe", "SAVE CHECKPOINT"), idle && saveBusy.Length == 0 && hasVehicle,
                saveBusy.Length > 0 ? saveBusy : !hasVehicle ? UiText.MessageText("text.484a738d6fee", "No controlled vehicle in flight.")
                : UiText.MessageText("text.ed809aca9c75", "Saves the game now as the checkpoint of this take. An earlier checkpoint of the take is replaced.")))
            TakeRunner.RequestCheckpoint(take.Name);
        Widgets.SameLine();
        bool canReset = idle && resetBusy.Length == 0 && take.Checkpoint != null && session.Validation.IsValid;
        if (Widgets.Button(UiText.MessageText("text.9077937aa387", "RESET TO CHECKPOINT"), canReset,
                resetBusy.Length > 0 ? resetBusy : take.Checkpoint == null ? UiText.MessageText("text.49bb90f95f31", "Save a checkpoint first.")
                : UiText.MessageText("text.3190a6154bad", "Ends a running take, loads the checkpoint and makes the take ready.")))
            TakeRunner.RequestReset(take.Name);
        if (flow.IsActive)
        {
            Widgets.SameLine();
            if (Widgets.Button(UiText.MessageText("text.ad8d698573fc", "CANCEL")))
                TakeRunner.RequestStop();
        }

        bool autoStart = take.AutoStart;
        if (Widgets.Check(UiText.MessageText("text.a9f747415594", "Start the take after a reset"), ref autoStart))
            session.SetAutoStart(autoStart);
        Widgets.Tip(UiText.MessageText("text.18a473bef5a2", "The game stays stopped after the load until the take starts. Without this, press Start take yourself."));
        if (take.AutoStart && CineCamPatches.FrameAvailable && CineCamPatches.CameraBlock is { } noStart)
            Widgets.Reason(noStart);
        if (take.AutoStart)
        {
            double delay = take.AutoStartDelay;
            if (Widgets.Num(UiText.MessageText("text.b11fc051f0c9", "Delay (s)##autostart"), ref delay, 0.1f, 0.0, 600.0))
                session.SetAutoStartDelay(delay);
        }

        bool pauseAt = take.PauseAt.HasValue;
        if (Widgets.Check(UiText.MessageText("text.410c8cb8ed17", "Pause the run at a take time"), ref pauseAt))
            session.SetPauseAt(pauseAt ? take.PauseAt ?? PauseAtDefault : null);
        Widgets.Tip(UiText.MessageText("text.0b9a4ef5656a", "For a rehearsal of the start: a take or a dry run plays once up to this time and then pauses for an edit, as Pause and edit does. Resume plays on, and Reset to checkpoint tries again. An offline capture and a replay do not pause. Turn it off for the final take."));
        if (take.PauseAt is double at)
        {
            if (Widgets.Num(UiText.MessageText("text.93fcca668bf8", "Pause at (s)##pauseat"), ref at, 0.1f, RehearsalEdit.MinPauseAt, Take.MaxPauseAt))
                session.SetPauseAt(at);
        }

        double? left = flow.SecondsToStart(TakeRunner.ClockSeconds);
        if (left.HasValue)
            Widgets.Colored(UiText.Format("text.7f1c97259809", $"The take starts in {left.Value:F1} s."), Widgets.GoodColor);
        else if (flow.IsActive)
            Widgets.Colored(UiText.Concat(UiText.MessageText("text.26e32ddbd321", "Reset: "), flow.Phase), Widgets.GoodColor);
        else if (flow.Outcome.Length > 0)
            Widgets.Colored(flow.Outcome, flow.Phase == ResetPhase.Failed ? Widgets.ErrorColor : Widgets.MutedColor);
        Widgets.Muted(UiText.MessageText("text.5bd3676efd17", "A load clears the state of other mods. Arm them again before the flight."));
    }

    private const double PauseAtDefault = 10.0;

    private static string _startBlock = "";

    public static void Reset() => _startBlock = "";

    private static readonly string[] LongLabels = [UiText.MessageText("text.0cefbd1d21f7", "START TAKE"), UiText.MessageText("text.4617870bc32e", "DRY RUN"), UiText.MessageText("text.04dedf8afeb8", "STOP"), UiText.MessageText("text.dcc0670ed57d", "PAUSE AND EDIT"), UiText.MessageText("text.0747c96a23db", "RESUME")];
    private static readonly string[] ShortLabels = [UiText.MessageText("text.39f17ec6ebcf", "START"), UiText.MessageText("text.4617870bc32e", "DRY RUN"), UiText.MessageText("text.04dedf8afeb8", "STOP"), UiText.MessageText("text.ca6456daeccf", "PAUSE"), UiText.MessageText("text.0747c96a23db", "RESUME")];

    // The ### ids stay the same for the long and the short labels, so a held button keeps its state when the labels switch.
    private static void DrawRun(EditSession session)
    {
        ActiveTake? running = TakeRunner.Current;
        Vehicle? vehicle = Program.ControlledVehicle;
        bool hasVehicle = vehicle != null && !vehicle.IsDisposed && Program.Editor == null;
        string whyNot = CineCamPatches.CameraBlock ?? ViewText.StartBlock(running != null, TakeRunner.Reset.IsActive, hasVehicle, SunWarp.IsRunning,
            session.Take != null && session.Validation.IsValid);
        if (whyNot.Length == 0 && ReplaySession.IsPlayingBack)
            whyNot = UiText.MessageText("text.98668075c14d", "A replay is open. It plays the selected take.");
        _startBlock = whyNot;
        bool canStart = whyNot.Length == 0;
        string name = session.Take?.Name ?? "";
        bool paused = running is { IsPaused: true };
        string[] labels = RunRowWidth(LongLabels, paused) > ConsoleStyle.ContentAvailWidth() ? ShortLabels : LongLabels;

        string startTip = UiText.Concat(UiText.MessageText("text.b5e02f158837", "Starts the take with its log, speed track and camera track."), (session.Dirty ? UiText.MessageText("text.8879b04a8670", " The take uses the unsaved changes of this window.") : ""));
        if (Widgets.Button(UiText.Concat(labels[0], "###run-start"), canStart, canStart ? startTip : whyNot, Widgets.Tone.Primary))
            TakeRunner.RequestStart(name);
        Widgets.SameLine();
        if (Widgets.Button(UiText.Concat(labels[1], "###run-dry"), canStart, canStart ? UiText.MessageText("text.5e3bb4586b3a", "Runs the camera track only. The sim speed stays alone and no log is written.") : whyNot))
            TakeRunner.RequestStart(name, dryRun: true);
        Widgets.SameLine();
        bool canStop = running != null || TakeRunner.Reset.IsActive;
        if (Widgets.Button(UiText.Concat(labels[2], "###run-stop"), canStop, canStop ? UiText.MessageText("text.5fc7ca0fab04", "Ends the take, the dry run or the preview.") : UiText.MessageText("text.50909d1ce0e0", "Nothing runs."), Widgets.Tone.Danger))
            TakeRunner.RequestStop();
        Widgets.SameLine();
        bool canPause = running is { Preview: null, Offline: null, IsPaused: false };
        if (paused)
        {
            if (Widgets.Button(UiText.Concat(labels[4], "###run-resume"), true, UiText.MessageText("text.fe843a61faad", "Gives the camera back to the shot and the sim speed back to what it was.")))
                TakeRunner.RequestResume();
        }
        else if (Widgets.Button(UiText.Concat(labels[3], "###run-pause"), canPause,
                 running == null ? UiText.MessageText("text.72f0852286dd", "Needs a running take. The sim and the take clock stop and the camera is free.")
                 : running.Offline != null ? UiText.MessageText("text.7db5c643989b", "An offline capture cannot be paused.")
                 : running.Preview != null ? UiText.MessageText("text.880e2752f9af", "A preview has its own pause.")
                 : UiText.MessageText("text.d50b0bd703a0", "Stops the sim and the take clock and frees the camera. The same as the edit key.")))
        {
            TakeRunner.RequestPause();
        }
    }

    private static float RunRowWidth(string[] labels, bool paused)
    {
        float gap = ImGui.GetStyle().ItemSpacing.X;
        return ConsoleWidgets.ButtonWidth(UiText.Visible(labels[0]).AsSpan()) + ConsoleWidgets.ButtonWidth(UiText.Visible(labels[1]).AsSpan())
            + ConsoleWidgets.ButtonWidth(UiText.Visible(labels[2]).AsSpan()) + ConsoleWidgets.ButtonWidth(UiText.Visible(labels[paused ? 4 : 3]).AsSpan()) + gap * 3f;
    }

    private static readonly string[] RecordingHeaders = { UiText.MessageText("text.b9b5a916fff7", "Take (s)"), UiText.MessageText("text.4e1f49a9c8ae", "Event"), UiText.MessageText("text.71d5827ac585", "Flight (s)") };

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

        _recordingRows = rows.Select((row, i) => new RecordingRow(UiText.Concat("recording-event-", i),
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
            Widgets.Muted(UiText.MessageText("text.08d3d1b5f50c", "Open a recording to see its events."));
            return;
        }

        List<RecordingRow> rows = RecordingRows();
        if (rows.Count == 0)
        {
            Widgets.Muted(UiText.MessageText("text.290b313de9fa", "The recording has no events."));
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

        Widgets.Muted(UiText.MessageText("text.4cfb8c9dfb64", "Click an event to move the replay there. An event without a take time comes after the end of the take."));
    }

    private static readonly string[] EventHeaders = { UiText.MessageText("text.d8717b312aa4", "Time (s)"), UiText.MessageText("text.4e1f49a9c8ae", "Event"), UiText.MessageText("text.a3b50c476732", "State") };

    private static void DrawEvents(EditSession session, LiveEvents live)
    {
        ActiveTake? running = TakeRunner.Current;
        bool useTake = running != null && running.Preview == null;
        IReadOnlyDictionary<string, double> seen = useTake ? running!.Director.Events.Seen : live.Seen;
        IReadOnlyDictionary<string, double> predictions = useTake ? running!.Director.Events.Predictions : live.Predictions;
        string timeBase = useTake ? UiText.MessageText("text.589043cfaf25", "take time") : UiText.MessageText("text.1dc98bfbce45", "time since the window started to watch");

        if (!useTake && !live.HasVehicle)
        {
            Widgets.Muted(UiText.MessageText("text.484a738d6fee", "No controlled vehicle in flight."));
            return;
        }

        VehicleSample? sample = live.Sample;
        if (sample is { } s)
        {
            Widgets.Muted(UiText.Format("text.9692e90e527d", $"engine {(s.EngineOn ? UiText.MessageText("value.on", "on") : UiText.MessageText("value.off", "off"))}, contact {(s.Contact ? UiText.MessageText("text.8a798890fe93", "yes") : UiText.MessageText("text.9390298f3fb0", "no"))}, sequence {s.ActiveSequence}, altitude {s.Altitude:F0} m, over ground {(s.Ground is double g ? UiText.Concat(g.ToString("F1", CultureInfo.InvariantCulture), " m") : UiText.MessageText("value.unknown", "unknown"))}, speed {s.Speed:F1} m/s"));
            Widgets.Muted(ViewText.FlightState(s));
        }

        var awaited = new List<string>();
        Take? take = session.Take;
        if (take != null && session.Document != null)
            awaited = EventDetector.Watched(take, session.Document.Events).Select(n => n.Canonical).ToList();
        List<EventRow> rows = ViewText.EventRows(seen, predictions, awaited);

        if (rows.Count == 0)
            Widgets.Muted(UiText.MessageText("text.e056734effa8", "Nothing yet."));
        else
        {
            float size = ImGui.GetFontSize();
            float width = ConsoleStyle.ContentAvailWidth();
            float[] columns = { ConsoleWidgets.RowLabelInsetPx, size * 5.0f, size * 5.0f + (width - size * 5.0f) * 0.55f };
            Widgets.ListHeader(EventHeaders, columns);
            for (int i = 0; i < rows.Count; i++)
            {
                EventRow row = rows[i];
                Widgets.ListRow(UiText.Concat("event-", i), [row.Time, Widgets.EventName(row.Name), row.Status], columns, false, row.Seen, row.Seen ? -1 : 2);
            }
        }

        Widgets.Muted(UiText.Format("text.b40049a11d77", $"Times are {timeBase}."));
        if (!useTake && Widgets.Button(UiText.MessageText("text.8a98de3f22a8", "CLEAR EVENTS"), true, UiText.MessageText("text.cf31c61aa1ea", "Forgets the events seen and starts the clock again.")))
            live.Reset();
    }
}
