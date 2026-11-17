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
    private static readonly string Off = UiText.MessageText("text.ca7981b46ecf", "Off");

    private static readonly float[] SpeedPresets = { 0.1f, 0.25f, 0.5f, 1f, 2f, 4f };

    private const float MinSpeed = 0.05f;
    private const float MaxSpeed = 30f;

    private static readonly (KeySetting Setting, string Label, string Tip)[] PlayerKeys =
    {
        (KeySetting.Window, UiText.MessageText("text.99c252395ebc", "Open or close this window"), UiText.MessageText("text.017152f6206f", "Opens and closes the CineCam window.")),
        (KeySetting.StartStop, UiText.MessageText("text.ecad74183fd1", "Start or stop the take"), UiText.MessageText("text.cbbfee0d48ce", "Starts the default take, or stops the running take.")),
        (KeySetting.Edit, UiText.MessageText("text.b0ce9f5effb8", "Pause the take for an edit"), UiText.MessageText("text.c574c7013ae5", "Pauses the running take so you can move the camera and add keys, and resumes it.")),
        (KeySetting.Reset, UiText.MessageText("text.1d7a75186e50", "Reset to the checkpoint"), UiText.MessageText("text.3b66a58daece", "Loads the checkpoint save of the default take. Use a key that you do not press by chance, because a reset replaces the flight.")),
        (KeySetting.Mark, UiText.MessageText("text.133c7489678b", "Mark a moment"), UiText.MessageText("text.ed27eb9c26ea", "Gives a mark event during a take (mark, mark:2 and so on), for example as a cut point in the event log.")),
        (KeySetting.PlayPause, UiText.MessageText("text.65f7223b3d32", "Play or pause a replay"), UiText.MessageText("text.f27cfb18f714", "Plays and pauses an open replay. Space does not work for this, because the game moves the free camera up with it.")),
    };

    private static readonly (KeySetting Setting, string Label, string Tip)[] DeveloperKeys =
    {
        (KeySetting.Reload, UiText.MessageText("text.4ce62bd4d73f", "Check the shot file"), UiText.MessageText("text.a91cc114d23e", "Checks the shot file and logs the result.")),
    };

    private static double _sunElevation = 5.0;
    private static bool _sunSetting;
    private static string _aliasNew = "";

    public static void Draw(EditSession session)
    {
        UiLanguage language = UiText.Language;
        if (Widgets.Choice("Language / Sprache", ref language, Enum.GetValues<UiLanguage>(),
                choice => choice switch { UiLanguage.English => "English", UiLanguage.German => "Deutsch", _ => "Auto" }))
            CineCamSettings.SetLanguage(language);
        DrawLoadError();
        Widgets.Section(UiText.MessageText("text.d534be829e32", "Video"));
        DrawFileOptions(session);
        DrawCapture(session);
        LookPanel.DrawStill();
        DrawAliasFold(session);
        DrawSound();
        Widgets.Section(UiText.MessageText("text.effd7b9faaad", "Interface during a take"));
        DrawInterface(session);
        Widgets.Section(UiText.MessageText("text.cb2286a32f09", "Game sound"));
        LookPanel.DrawSettings();
        Widgets.Section(UiText.MessageText("text.f0d66a79c138", "Keys"));
        DrawKeys(PlayerKeys);
        Widgets.Section(UiText.MessageText("text.a78ef31f8e97", "Free camera"));
        DrawCamera();
        Widgets.Section(UiText.MessageText("text.2e6651c0ed88", "Game speed"));
        DrawSpeed();
        Widgets.Section(UiText.MessageText("text.318fb174f5eb", "Time of day"));
        DrawTimeOfDay();
        Widgets.Section(UiText.MessageText("text.3dc9f569422d", "Experimental"));
        DrawExperimental();
        Widgets.Section(UiText.MessageText("text.3fb7b39416f1", "Developer"));
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
        if (Widgets.Button(UiText.MessageText("text.c82cd31fc30a", "READ THE SETTINGS FILE AGAIN"), true, UiText.MessageText("text.5dfc5c31072b", "Reads the settings file again. A file that is gone is made again with the defaults.")))
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
            if (Widgets.Choice(UiText.Format("", $"{label}##key-{setting}"), ref picked, options, n => n, null,
                    UiText.Concat(tip, UiText.MessageText("text.260336a2f272", " A key that another action uses is taken from it. The change is saved at once."))))
                CineCamSettings.SetKey(setting, picked == Off ? null : Enum.Parse<GlfwKey>(picked));
        }
    }

    private static void DrawSound()
    {
        bool record = CineCamSettings.RecordTakeSound;
        if (Widgets.Check(UiText.MessageText("text.7d5b2c2626e8", "Record the game sound of a take"), ref record,
                UiText.MessageText("text.48c890f91b36", "Saves the game sound of each real-time take as game-sound.wav (32-bit float, stereo) in the take folder, as you hear it with your volume settings. The event log gives the take time of its first sample. An offline capture, a dry run and a preview record no sound, because an offline capture runs slower than real time. About 23 MB per minute.")))
            CineCamSettings.SetRecordTakeSound(record);
    }

    private static void DrawExperimental()
    {
        bool replay = CineCamSettings.ExperimentalReplay;
        if (Widgets.Check(UiText.MessageText("text.02c69723b7c8", "Replay of recorded flights"), ref replay,
                UiText.MessageText("text.94b4c8b3e34b", "Shows the Replay source on the Editor page: record a flight and play a take of the editor on it. The replay is experimental and may break with a game update.")))
            CineCamSettings.SetExperimentalReplay(replay);
        if (!CineCamSettings.ExperimentalReplay)
            return;
        bool record = CineCamSettings.RecordWithTake;
        if (Widgets.Check(UiText.MessageText("text.5846afae232f", "Record the flight with each take"), ref record,
                UiText.MessageText("text.c47264b4b8c1", "Starts a flight recording when a take starts and saves it when the take ends, so the flight can be cut again in a replay. The recording has the name of the take. A dry run and a preview record nothing, and a recording that runs already goes on.")))
            CineCamSettings.SetRecordWithTake(record);
        bool sound = CineCamSettings.ReplaySound;
        if (Widgets.Check(UiText.MessageText("text.ad4c715f0822", "Engine sounds in a replay"), ref sound,
                UiText.MessageText("text.53f2aa642ec9", "Plays the engine sounds of the recorded burning engines at their plumes. The pitch follows the replay speed as the game sound follows the game speed. A pause holds the sounds, and a render or a speed above 10x plays none.")))
            CineCamSettings.SetReplaySound(sound);
    }

    private static void DrawDeveloper(EditSession session)
    {
        bool developer = CineCamSettings.DeveloperMode;
        if (Widgets.Check(UiText.MessageText("text.b044b8b0eb0b", "Developer mode"), ref developer, UiText.MessageText("text.b24b9d80eb09", "Shows the file paths, the file section of the take, the local control and the autotest.")))
            CineCamSettings.SetDeveloperMode(developer);
        if (!CineCamSettings.DeveloperMode)
            return;

        DrawKeys(DeveloperKeys);
        Widgets.Muted(UiText.Concat(UiText.MessageText("text.02c398744fde", "Settings file: "), CineCamPaths.SettingsFile));
        Widgets.Muted(UiText.Concat(UiText.MessageText("text.e574d3e352bc", "Shot file: "), session.Path));
        Widgets.Muted(UiText.Concat(UiText.MessageText("text.10c0b09dfad8", "Take folders: "), CineCamPaths.TakesFolder));
        if (session.Document is { } file
            && Widgets.Text(UiText.MessageText("text.3515bf4688b5", "Site of the file (Landmark id)"), "file-site", file.Site ?? "", out string site))
        {
            file.Site = string.IsNullOrWhiteSpace(site) ? null : site.Trim();
            session.Changed();
        }

        Widgets.Muted(CineCamSettings.EnableLocalControl
            ? UiText.Concat(UiText.Concat(UiText.MessageText("text.ceea213fc772", "Local control is on: programs on this computer can control the replay through the files in "), Path.Combine(CineCamPaths.Root, "control")), ".")
            : UiText.MessageText("text.5ea7395f932c", "Local control is off. Set \"enableLocalControl\": true in the settings file and start the game again to turn it on."));
        Widgets.Muted(AutotestRunner.IsActive
            ? UiText.MessageText("text.8e14981eb3d3", "The autotest runs in this game session.")
            : UiText.Format("text.822f1e38e7d0", $"The autotest runs when the environment variable {AutotestRunner.Variable} names a result folder at the game start."));
    }

    private static void DrawInterface(EditSession session)
    {
        bool showWindow = CineCamSettings.ShowWindowDuringTake;
        if (Widgets.Check(UiText.MessageText("text.6622bf3104fc", "Keep this window open during a take"), ref showWindow, UiText.MessageText("text.455841ddad5e", "Off hides the window while a take records, so it is not in the video.")))
            CineCamSettings.SetShowWindowDuringTake(showWindow);
        ShotFile? file = session.Document;
        if (file == null)
            return;
        bool changed = false;
        bool hide = file.HideUi;
        if (Widgets.Check(UiText.MessageText("text.efefbbb21590", "Hide the game interface during a take"), ref hide, UiText.MessageText("text.64c3e3f2366d", "Hides the interface of the game while a take records. A pause for an edit shows it again.")))
        {
            file.HideUi = hide;
            changed = true;
        }

        ImGui.BeginDisabled(!file.HideUi);
        try
        {
            if (Widgets.Text(UiText.MessageText("text.0971b05feb04", "Hide at (event)"), "file-hideat", file.HideUiAt ?? "", out string at, 512,
                    UiText.MessageText("text.3617815bcf29", "The event at which the interface is hidden, for example ignition. Empty or start hides it at the take start, so the interface stays usable until then.")))
            {
                file.HideUiAt = string.IsNullOrWhiteSpace(at) ? null : at.Trim();
                changed = true;
            }

            if (session.Take is { } take && Widgets.Text(UiText.MessageText("text.ba7955fb3060", "Hide at, this take"), UiText.Concat("take-hideat-", session.TakeIndex), take.HideUiAt ?? "", out string takeAt, 512,
                    UiText.MessageText("text.a24bcca3eba9", "Overrides the event of the file for the selected take. Empty uses the event of the file.")))
            {
                take.HideUiAt = string.IsNullOrWhiteSpace(takeAt) ? null : takeAt.Trim();
                changed = true;
            }
        }
        finally { ImGui.EndDisabled(); }

        if (!file.HideUi)
            Widgets.Reason(UiText.MessageText("text.64f68aaeb7c1", "Turn on hiding to set the event."));
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
            if (Widgets.Slider(UiText.MessageText("text.f451e482b00c", "Free camera FOV (deg)"), ref fov, 15f, 120f, fov.ToString("0.0", CultureInfo.InvariantCulture)))
                FreeCamera.SetFov(fov);
        }
        finally { ImGui.EndDisabled(); }

        Widgets.Tip(UiText.MessageText("text.5ad688088e23", "Changes the field of view of the game camera now. It goes back to the earlier value when the window closes."));
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
            if (Widgets.Slider(UiText.MessageText("text.051a10419193", "Game speed now"), ref log, MathF.Log(MinSpeed), MathF.Log(MaxSpeed), UiText.Format("", $"{speed:0.00}x")))
                Universe.SetSimulationSpeed(Math.Clamp(MathF.Exp(log), MinSpeed, MaxSpeed), alert: false);

            for (int i = 0; i < SpeedPresets.Length; i++)
            {
                float preset = SpeedPresets[i];
                if (i > 0)
                    Widgets.SameLine();
                if (Widgets.Button(UiText.Format("", $"{preset:0.##}x##speed{preset}")))
                    Universe.SetSimulationSpeed(preset, alert: false);
            }
        }
        finally { ImGui.EndDisabled(); }

        Widgets.Tip(UiText.MessageText("text.9948254e7d85", "The speed track of a recording take sets the speed itself."));
        if (recording)
            Widgets.Muted(UiText.MessageText("text.3134c19086f6", "A recording take owns the sim speed."));
        else if (warping)
            Widgets.Muted(UiText.MessageText("text.3dc20291d982", "The game moves the time, so the sim speed is locked."));
        else if (paused)
            Widgets.Muted(UiText.MessageText("text.d106bdb468fc", "The game is paused by a menu."));
    }

    private static void DrawTimeOfDay()
    {
        if (SunWarp.IsRunning)
        {
            Widgets.Colored(UiText.MessageText("text.598c233bf1f8", "The game time is moving to the sun elevation."), Widgets.GoodColor);
            if (Widgets.Button(UiText.MessageText("text.c5d79c4398bc", "CANCEL MOVE")))
                SunWarp.Cancel();
            return;
        }

        Widgets.Num(UiText.MessageText("text.dabb5511e011", "Sun elevation (deg)"), ref _sunElevation, 0.1f, -30.0, 80.0, "%.1f", UiText.MessageText("text.b7228568c32b", "The angle of the sun over the horizon at the launch site."));
        bool setting = _sunSetting;
        if (Widgets.Check(UiText.MessageText("text.b7801661b8cb", "Sun going down"), ref setting))
            _sunSetting = setting;
        string? unsupported = CineCamPatches.FrameBlock;
        bool canMove = unsupported == null && !TakeRunner.IsRunning && Program.Editor == null;
        if (Widgets.Button(UiText.MessageText("text.191e4240e6c0", "MOVE GAME TIME"), canMove, unsupported ?? UiText.MessageText("text.5b8880e83b1b", "Moves the game time forward to the next time the sun at the launch site has this elevation. The game time cannot go back.")))
            SunWarp.Start(_sunElevation, _sunSetting, azimuthDeg: null, searchDays: 400.0);
    }

    private static void DrawFileOptions(EditSession session)
    {
        ShotFile? file = session.Document;
        if (file == null)
            return;

        bool changed = false;

        double fps = file.Fps;
        if (Widgets.Num(UiText.MessageText("text.d12bbc9e3984", "Frames per second"), ref fps, 1f, 1.0, 1000.0, "%.0f", UiText.MessageText("text.b20a2279c936", "The frame rate of the video that the take is cut for. It sets the offline picture rate when no other rate is given.")))
        {
            file.Fps = fps;
            changed = true;
        }

        bool flash = file.SyncFlash;
        if (Widgets.Check(UiText.MessageText("text.1483d21f4d95", "White frame at the take start"), ref flash))
        {
            file.SyncFlash = flash;
            changed = true;
        }

        Widgets.Tip(UiText.MessageText("text.fa7e3c4f84ee", "One white frame marks the start in the video, so the video and the event log line up."));
        if (changed)
            session.Changed();
    }

    private static void DrawAliasFold(EditSession session)
    {
        if (session.Document is { } file && Widgets.Fold(UiText.MessageText("text.a1b9e2e79efa", "Event names of the file"), "file-aliases") && DrawAliases(file))
            session.Changed();
    }

    private static bool DrawAliases(ShotFile file)
    {
        bool changed = false;
        Widgets.Muted(UiText.MessageText("text.0a63aaa291b3", "Event names (aliases)"));
        string? remove = null;
        int row = 0;
        foreach (var pair in file.Events.ToList())
        {
            ImGui.PushID(row++);
            try
            {
                if (Widgets.ButtonRow(UiText.Format("", $"{pair.Key} = {Widgets.EventName(pair.Value)}"), UiText.MessageText("text.7432ec99f16b", "REMOVE##alias"), true, null, Widgets.Tone.Danger))
                    remove = pair.Key;
            }
            finally { ImGui.PopID(); }
        }

        if (remove != null)
        {
            file.Events.Remove(remove);
            changed = true;
        }

        Widgets.Text(UiText.MessageText("text.04eea2a4ae80", "New name"), "alias-new", _aliasNew, out _aliasNew);
        string target = _aliasTarget;
        Widgets.Text(UiText.MessageText("text.4e1f49a9c8ae", "Event"), "alias-target", target, out _aliasTarget);
        if (Widgets.Button(UiText.MessageText("text.3d32f762f199", "ADD NAME"), _aliasNew.Trim().Length > 0 && _aliasTarget.Trim().Length > 0))
        {
            file.Events[_aliasNew.Trim()] = _aliasTarget.Trim();
            _aliasNew = "";
            _aliasTarget = "";
            changed = true;
        }

        Widgets.Tip(UiText.MessageText("text.6ec63c534924", "Example: the name fairing for the event sequence:3. A shot can then start on fairing."));
        return changed;
    }

    private static string _aliasTarget = "";

    private static readonly CaptureGrab[] GrabOptions = [CaptureGrab.Swapchain, CaptureGrab.Clean];

    private static void DrawCapture(EditSession session)
    {
        ShotFile? file = session.Document;
        if (file == null)
        {
            Widgets.Muted(UiText.MessageText("text.fbb9abd7783a", "Make a take on the Editor page to set the video options."));
            return;
        }

        CaptureOptions capture = file.Capture;
        bool changed = false;

        CaptureMode mode = capture.Mode;
        if (Widgets.Enum(UiText.MessageText("text.06fa7cec8d6f", "Capture mode"), ref mode, null, UiText.MessageText("text.cbf8840f4a29", "Realtime: record the screen with a program such as OBS. Offline: the game saves one picture per frame.")))
        {
            capture.Mode = mode;
            changed = true;
        }

        if (capture.Mode == CaptureMode.Realtime)
        {
            Widgets.Muted(UiText.MessageText("text.e7f883be2ddc", "Record the screen of a take with OBS. The game runs in real time."));
        }
        else
        {
            double fps = file.CaptureFps;
            if (Widgets.Num(UiText.MessageText("text.d32779c87775", "Pictures per second"), ref fps, 1f, 1.0, 1000.0, "%.0f"))
            {
                capture.Fps = fps;
                changed = true;
            }

            Widgets.Tip(UiText.MessageText("text.0173c1955b8e", "The game steps by one over this value for each picture, so the speed of the video is exact at any render speed."));

            if (Widgets.Text(UiText.MessageText("text.998915e5dfe6", "Output folder"), "capture-folder", capture.Folder ?? "", out string folder))
            {
                capture.Folder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
                changed = true;
            }

            Widgets.Tip(UiText.MessageText("text.358905ae0ff4", "Empty means the folder of the take. A relative path is relative to the folder of the take."));

            CaptureGrab grab = capture.Grab;
            if (Widgets.Choice(UiText.MessageText("text.b6dcc2699b32", "Picture grab"), ref grab, GrabOptions, g => g == CaptureGrab.Clean ? UiText.MessageText("text.564031cfbc8a", "Clean scene") : UiText.MessageText("text.9758cf9190da", "Screen (default)"),
                    g => g == CaptureGrab.Clean
                        ? UiText.MessageText("text.f9610e59de16", "Draws the scene of the main view into its own picture, without the interface and the overlays of other mods. The game hides the interface for the frame of each picture.")
                        : UiText.MessageText("text.2415418b2d79", "Copies the finished screen image, with everything that is drawn on it, also the overlays of other mods."),
                    UiText.MessageText("text.090af7dd2746", "How the offline capture and the render of a replay take each picture.")))
            {
                capture.Grab = grab;
                changed = true;
            }
            Widgets.Muted(UiText.MessageText("text.7337db756454", "The game saves one numbered picture per frame in the folder frames, with the size of the game window. The game may run slower than real time and makes no sound. Run make-video.cmd in the output folder with ffmpeg to make the video."));
            Widgets.Muted(UiText.MessageText("text.2ee395716ec3", "A take or a dry run from this window uses the mode. A dry run and a preview never save pictures."));
        }

        if (changed)
            session.Changed();
    }
}
