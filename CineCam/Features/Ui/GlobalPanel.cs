using System.Globalization;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using CineCam.Core;
using CineCam.Director;
using CineCam.Editing;
using CineCam.Features.Autotest;
using CineCam.Features.Takes;
using CineCam.Features.TimeOfDay;
using CineCam.Patches;
using KSA;

namespace CineCam.Features.Ui;

internal static class GlobalPanel
{
    private const string Off = "Off";

    private static readonly float[] SpeedPresets = { 0.1f, 0.25f, 0.5f, 1f, 2f, 4f };

    private const float MinSpeed = 0.05f;
    private const float MaxSpeed = 30f;

    private static readonly (KeySetting Setting, string Label, string Tip)[] PlayerKeys =
    {
        (KeySetting.Window, "Open or close this window", "Opens and closes the CineCam window."),
        (KeySetting.StartStop, "Start or stop the take", "Starts the default take, or stops the running take."),
        (KeySetting.Edit, "Pause the take for an edit", "Pauses the running take so you can move the camera and add keys, and resumes it."),
        (KeySetting.Reset, "Reset to the checkpoint", "Loads the checkpoint save of the default take. Use a key that you do not press by chance, because a reset replaces the flight."),
        (KeySetting.Mark, "Mark a moment", "Gives a mark event during a take (mark, mark:2 and so on), for example as a cut point in the event log."),
        (KeySetting.PlayPause, "Play or pause a replay", "Plays and pauses an open replay. Space does not work for this, because the game moves the free camera up with it."),
    };

    private static readonly (KeySetting Setting, string Label, string Tip)[] DeveloperKeys =
    {
        (KeySetting.Reload, "Check the shot file", "Checks the shot file and logs the result."),
    };

    private static double _sunElevation = 5.0;
    private static bool _sunSetting;
    private static string _aliasNew = "";

    public static void Draw(EditSession session)
    {
        DrawLoadError();
        Widgets.Section("Video");
        DrawFileOptions(session);
        DrawCapture(session);
        DrawAliasFold(session);
        Widgets.Section("Interface during a take");
        DrawInterface(session);
        Widgets.Section("Keys");
        DrawKeys(PlayerKeys);
        Widgets.Section("Free camera");
        DrawCamera();
        Widgets.Section("Game speed");
        DrawSpeed();
        Widgets.Section("Time of day");
        DrawTimeOfDay();
        Widgets.Section("Experimental");
        DrawExperimental();
        Widgets.Section("Developer");
        DrawDeveloper(session);
        if (CineCamSettings.SaveError.Length > 0)
            Widgets.Colored(CineCamSettings.SaveError, Widgets.ErrorColor);
    }

    // A settings file that could not be read is not overwritten, so the error stays until the player corrects the file.
    private static void DrawLoadError()
    {
        if (CineCamSettings.LoadError.Length == 0)
            return;
        Widgets.Colored(CineCamSettings.LoadError, Widgets.ErrorColor);
        if (Widgets.Button("READ THE SETTINGS FILE AGAIN", true, "Reads the settings file again. A file that is gone is made again with the defaults."))
            CineCamSettings.Load();
    }

    private static readonly List<string> KeyOptions = new[] { Off }.Concat(CineCamSettings.KeyChoices).ToList();

    // The list holds the keys that the game leaves free, plus the key of the settings file when it is another one.
    private static void DrawKeys((KeySetting Setting, string Label, string Tip)[] keys)
    {
        foreach (var (setting, label, tip) in keys)
        {
            GlfwKey? key = CineCamSettings.GetKey(setting);
            string current = key.HasValue ? key.Value.ToString() : Off;
            List<string> options = KeyOptions;
            if (!options.Contains(current))
                options = new List<string>(KeyOptions) { current };
            string picked = current;
            if (Widgets.Choice($"{label}##key-{setting}", ref picked, options, n => n, null,
                    tip + " A key that another action uses is taken from it. The change is saved at once."))
                CineCamSettings.SetKey(setting, picked == Off ? null : Enum.Parse<GlfwKey>(picked));
        }
    }

    private static void DrawExperimental()
    {
        bool replay = CineCamSettings.ExperimentalReplay;
        if (Widgets.Check("Replay of recorded flights", ref replay,
                "Shows the Replay source on the Editor page: record a flight and play a take of the editor on it. The replay is experimental and may break with a game update."))
            CineCamSettings.SetExperimentalReplay(replay);
        if (!CineCamSettings.ExperimentalReplay)
            return;
        bool record = CineCamSettings.RecordWithTake;
        if (Widgets.Check("Record the flight with each take", ref record,
                "Starts a flight recording when a take starts and saves it when the take ends, so the flight can be cut again in a replay. The recording has the name of the take. A dry run and a preview record nothing, and a recording that runs already goes on."))
            CineCamSettings.SetRecordWithTake(record);
    }

    private static void DrawDeveloper(EditSession session)
    {
        bool developer = CineCamSettings.DeveloperMode;
        if (Widgets.Check("Developer mode", ref developer, "Shows the file paths, the file section of the take, the local control and the autotest."))
            CineCamSettings.SetDeveloperMode(developer);
        if (!CineCamSettings.DeveloperMode)
            return;

        DrawKeys(DeveloperKeys);
        Widgets.Muted("Settings file: " + CineCamPaths.SettingsFile);
        Widgets.Muted("Shot file: " + session.Path);
        Widgets.Muted("Take folders: " + CineCamPaths.TakesFolder);
        if (session.Document is { } file
            && Widgets.Text("Site of the file (Landmark id)", "file-site", file.Site ?? "", out string site))
        {
            file.Site = string.IsNullOrWhiteSpace(site) ? null : site.Trim();
            session.Changed();
        }

        Widgets.Muted(CineCamSettings.EnableLocalControl
            ? "Local control is on: programs on this computer can control the replay through the files in " + Path.Combine(CineCamPaths.Root, "control") + "."
            : "Local control is off. Set \"enableLocalControl\": true in the settings file and start the game again to turn it on.");
        Widgets.Muted(AutotestRunner.IsActive
            ? "The autotest runs in this game session."
            : $"The autotest runs when the environment variable {AutotestRunner.Variable} names a result folder at the game start.");
    }

    private static void DrawInterface(EditSession session)
    {
        bool showWindow = CineCamSettings.ShowWindowDuringTake;
        if (Widgets.Check("Keep this window open during a take", ref showWindow, "Off hides the window while a take records, so it is not in the video."))
            CineCamSettings.SetShowWindowDuringTake(showWindow);
        ShotFile? file = session.Document;
        if (file == null)
            return;
        bool changed = false;
        bool hide = file.HideUi;
        if (Widgets.Check("Hide the game interface during a take", ref hide, "Hides the interface of the game while a take records. A pause for an edit shows it again."))
        {
            file.HideUi = hide;
            changed = true;
        }

        ImGui.BeginDisabled(!file.HideUi);
        try
        {
            if (Widgets.Text("Hide at (event)", "file-hideat", file.HideUiAt ?? "", out string at, 512,
                    "The event at which the interface is hidden, for example ignition. Empty or start hides it at the take start, so the interface stays usable until then."))
            {
                file.HideUiAt = string.IsNullOrWhiteSpace(at) ? null : at.Trim();
                changed = true;
            }

            if (session.Take is { } take && Widgets.Text("Hide at, this take", "take-hideat-" + session.TakeIndex, take.HideUiAt ?? "", out string takeAt, 512,
                    "Overrides the event of the file for the selected take. Empty uses the event of the file."))
            {
                take.HideUiAt = string.IsNullOrWhiteSpace(takeAt) ? null : takeAt.Trim();
                changed = true;
            }
        }
        finally { ImGui.EndDisabled(); }

        if (!file.HideUi)
            Widgets.Reason("Turn on hiding to set the event.");
        if (changed)
            session.Changed();
    }

    private static void DrawCamera()
    {
        bool free = FreeCamera.CanUse && !TakeRunner.IsRunning;
        float fov = FreeCamera.CurrentFovDegrees();
        ImGui.BeginDisabled(!free);
        try
        {
            if (Widgets.Slider("Free camera FOV (deg)", ref fov, 15f, 120f, fov.ToString("0.0", CultureInfo.InvariantCulture)))
                FreeCamera.SetFov(fov);
        }
        finally { ImGui.EndDisabled(); }

        Widgets.Tip("Changes the field of view of the game camera now. It goes back to the earlier value when the window closes.");
    }

    private static void DrawSpeed()
    {
        ActiveTake? running = TakeRunner.Current;
        bool recording = running is { IsRecording: true };
        float speed = (float)Universe.GetSimulationSpeed();
        bool warping = Universe.IsAutoWarpActive || SunWarp.IsRunning;
        bool paused = speed <= 0f;
        ImGui.BeginDisabled(recording || warping || paused);
        try
        {
            // The kit slider is linear, so it moves the logarithm of the speed, as a logarithmic slider would.
            float log = MathF.Log(Math.Clamp(speed, MinSpeed, MaxSpeed));
            if (Widgets.Slider("Game speed now", ref log, MathF.Log(MinSpeed), MathF.Log(MaxSpeed), FormattableString.Invariant($"{speed:0.00}x")))
                Universe.SetSimulationSpeed(Math.Clamp(MathF.Exp(log), MinSpeed, MaxSpeed), alert: false);

            for (int i = 0; i < SpeedPresets.Length; i++)
            {
                float preset = SpeedPresets[i];
                if (i > 0)
                    Widgets.SameLine();
                if (Widgets.Button(FormattableString.Invariant($"{preset:0.##}x##speed{preset}")))
                    Universe.SetSimulationSpeed(preset, alert: false);
            }
        }
        finally { ImGui.EndDisabled(); }

        Widgets.Tip("The speed track of a recording take sets the speed itself.");
        if (recording)
            Widgets.Muted("A recording take owns the sim speed.");
        else if (warping)
            Widgets.Muted("The game moves the time, so the sim speed is locked.");
        else if (paused)
            Widgets.Muted("The game is paused by a menu.");
    }

    private static void DrawTimeOfDay()
    {
        if (SunWarp.IsRunning)
        {
            Widgets.Colored("The game time is moving to the sun elevation.", Widgets.GoodColor);
            if (Widgets.Button("CANCEL MOVE"))
                SunWarp.Cancel();
            return;
        }

        Widgets.Num("Sun elevation (deg)", ref _sunElevation, 0.1f, -30.0, 80.0, "%.1f", "The angle of the sun over the horizon at the launch site.");
        bool setting = _sunSetting;
        if (Widgets.Check("Sun going down", ref setting))
            _sunSetting = setting;
        string? unsupported = CineCamPatches.FrameBlock;
        bool canMove = unsupported == null && !TakeRunner.IsRunning && Program.Editor == null;
        if (Widgets.Button("MOVE GAME TIME", canMove, unsupported ?? "Moves the game time forward to the next time the sun at the launch site has this elevation. The game time cannot go back."))
            SunWarp.Start(_sunElevation, _sunSetting, azimuthDeg: null, searchDays: 400.0);
    }

    private static void DrawFileOptions(EditSession session)
    {
        ShotFile? file = session.Document;
        if (file == null)
            return;

        bool changed = false;

        double fps = file.Fps;
        if (Widgets.Num("Frames per second", ref fps, 1f, 1.0, 1000.0, "%.0f", "The frame rate of the video that the take is cut for. It sets the offline picture rate when no other rate is given."))
        {
            file.Fps = fps;
            changed = true;
        }

        bool flash = file.SyncFlash;
        if (Widgets.Check("White frame at the take start", ref flash))
        {
            file.SyncFlash = flash;
            changed = true;
        }

        Widgets.Tip("One white frame marks the start in the video, so the video and the event log line up.");
        if (changed)
            session.Changed();
    }

    private static void DrawAliasFold(EditSession session)
    {
        if (session.Document is { } file && Widgets.Fold("Event names of the file", "file-aliases") && DrawAliases(file))
            session.Changed();
    }

    private static bool DrawAliases(ShotFile file)
    {
        bool changed = false;
        Widgets.Muted("Event names (aliases)");
        string? remove = null;
        int row = 0;
        foreach (var pair in file.Events.ToList())
        {
            ImGui.PushID(row++);
            try
            {
                if (Widgets.ButtonRow($"{pair.Key} = {Widgets.EventName(pair.Value)}", "REMOVE##alias", true, null, Widgets.Tone.Danger))
                    remove = pair.Key;
            }
            finally { ImGui.PopID(); }
        }

        if (remove != null)
        {
            file.Events.Remove(remove);
            changed = true;
        }

        Widgets.Text("New name", "alias-new", _aliasNew, out _aliasNew);
        string target = _aliasTarget;
        Widgets.Text("Event", "alias-target", target, out _aliasTarget);
        if (Widgets.Button("ADD NAME", _aliasNew.Trim().Length > 0 && _aliasTarget.Trim().Length > 0))
        {
            file.Events[_aliasNew.Trim()] = _aliasTarget.Trim();
            _aliasNew = "";
            _aliasTarget = "";
            changed = true;
        }

        Widgets.Tip("Example: the name fairing for the event sequence:3. A shot can then start on fairing.");
        return changed;
    }

    private static string _aliasTarget = "";

    private static void DrawCapture(EditSession session)
    {
        ShotFile? file = session.Document;
        if (file == null)
        {
            Widgets.Muted("Make a take on the Editor page to set the video options.");
            return;
        }

        CaptureOptions capture = file.Capture;
        bool changed = false;

        CaptureMode mode = capture.Mode;
        if (Widgets.Enum("Capture mode", ref mode, null, "Realtime: record the screen with a program such as OBS. Offline: the game saves one picture per frame."))
        {
            capture.Mode = mode;
            changed = true;
        }

        if (capture.Mode == CaptureMode.Realtime)
        {
            Widgets.Muted("Record the screen of a take with OBS. The game runs in real time.");
        }
        else
        {
            double fps = file.CaptureFps;
            if (Widgets.Num("Pictures per second", ref fps, 1f, 1.0, 1000.0, "%.0f"))
            {
                capture.Fps = fps;
                changed = true;
            }

            Widgets.Tip("The game steps by one over this value for each picture, so the speed of the video is exact at any render speed.");

            if (Widgets.Text("Output folder", "capture-folder", capture.Folder ?? "", out string folder))
            {
                capture.Folder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
                changed = true;
            }

            Widgets.Tip("Empty means the folder of the take. A relative path is relative to the folder of the take.");
            Widgets.Muted("The game saves one numbered picture per frame in the folder frames, with the size of the game window. The game may run slower than real time and makes no sound. Run make-video.cmd in the output folder with ffmpeg to make the video.");
            Widgets.Muted("A take or a dry run from this window uses the mode. A dry run and a preview never save pictures.");
        }

        if (changed)
            session.Changed();
    }
}
