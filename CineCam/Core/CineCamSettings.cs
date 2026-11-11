using System.Text;
using System.Text.Json;
using Brutal.GlfwApi;
using Brutal.Logging;

namespace CineCam.Core;

internal enum KeySetting { StartStop, Edit, Window, Reset, Reload, Mark, PlayPause }

// The settings file is written with the defaults when it is missing and again after each change
// in the window, so the player always finds a complete file to edit.
internal static class CineCamSettings
{
    // Each name parses as a GlfwKey. Only keys without a default binding in the game Input are
    // offered, because the mod reads its keys in a postfix of Program.OnKey and a key that the game
    // also uses would do both actions.
    public static readonly string[] KeyChoices =
    {
        "Pause", "ScrollLock", "Insert", "Home", "End",
        "KpDecimal", "KpDivide", "KpMultiply", "KpEnter",
    };

    public static GlfwKey? StartStopKey { get; private set; } = GlfwKey.Pause;

    public static GlfwKey? ReloadKey { get; private set; } = GlfwKey.ScrollLock;

    public static GlfwKey? WindowKey { get; private set; } = GlfwKey.Insert;

    public static GlfwKey? ResetKey { get; private set; }

    public static GlfwKey? EditKey { get; private set; } = GlfwKey.End;

    public static GlfwKey? MarkKey { get; private set; }

    // Space is the stock CameraUp key, which moves the free camera of a replay, so the replay has a key of its own.
    public static GlfwKey? PlayPauseKey { get; private set; } = GlfwKey.Home;

    public static bool ShowWindowDuringTake { get; private set; }

    public static bool EnableLocalControl { get; private set; }

    public static bool ExperimentalReplay { get; private set; }

    // Records the flight while each take runs, so the take can be cut again in a replay.
    public static bool RecordWithTake { get; private set; }

    public static bool DeveloperMode { get; private set; }

    // Empty when the last write succeeded.
    public static string SaveError { get; private set; } = "";

    // Set when an existing settings file cannot be read. Save then writes nothing, so a change in
    // the window never replaces the player's file with the defaults.
    public static string LoadError { get; private set; } = "";

    public static void Load()
    {
        StartStopKey = GlfwKey.Pause;
        ReloadKey = GlfwKey.ScrollLock;
        WindowKey = GlfwKey.Insert;
        ResetKey = null;
        EditKey = GlfwKey.End;
        MarkKey = null;
        PlayPauseKey = GlfwKey.Home;
        ShowWindowDuringTake = false;
        EnableLocalControl = false;
        ExperimentalReplay = false;
        RecordWithTake = false;
        DeveloperMode = false;
        LoadError = "";
        SaveError = "";
        string path = CineCamPaths.SettingsFile;
        try
        {
            if (!File.Exists(path))
            {
                Save();
                return;
            }

            var options = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), options);
            StartStopKey = ReadKey(document.RootElement, "startStopKey", StartStopKey);
            ReloadKey = ReadKey(document.RootElement, "reloadKey", ReloadKey);
            WindowKey = ReadKey(document.RootElement, "windowKey", WindowKey);
            ResetKey = ReadKey(document.RootElement, "resetKey", ResetKey);
            EditKey = ReadKey(document.RootElement, "editKey", EditKey);
            MarkKey = ReadKey(document.RootElement, "markKey", MarkKey);
            PlayPauseKey = ReadKey(document.RootElement, "playPauseKey", PlayPauseKey);
            ShowWindowDuringTake = ReadBool(document.RootElement, "showWindowDuringTake", ShowWindowDuringTake);
            EnableLocalControl = ReadBool(document.RootElement, "enableLocalControl", EnableLocalControl);
            ExperimentalReplay = ReadBool(document.RootElement, "experimentalReplay", ExperimentalReplay);
            RecordWithTake = ReadBool(document.RootElement, "recordWithTake", RecordWithTake);
            DeveloperMode = ReadBool(document.RootElement, "developerMode", DeveloperMode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadError = $"The settings file {path} could not be read ({ex.Message}). The default keys are used, and changes are not saved until the file is correct.";
            DefaultCategory.Log.Warning("[CineCam] " + LoadError);
        }
    }

    #region Changes from the window

    public static GlfwKey? GetKey(KeySetting setting) => setting switch
    {
        KeySetting.StartStop => StartStopKey,
        KeySetting.Edit => EditKey,
        KeySetting.Window => WindowKey,
        KeySetting.Reset => ResetKey,
        KeySetting.Mark => MarkKey,
        KeySetting.PlayPause => PlayPauseKey,
        _ => ReloadKey,
    };

    // A key that another setting uses already is taken from that setting, so one key never does two things.
    public static void SetKey(KeySetting setting, GlfwKey? key)
    {
        if (key.HasValue)
        {
            foreach (KeySetting other in Enum.GetValues<KeySetting>())
            {
                if (other != setting && GetKey(other) == key)
                    Assign(other, null);
            }
        }

        Assign(setting, key);
        Save();
    }

    private static void Assign(KeySetting setting, GlfwKey? key)
    {
        switch (setting)
        {
            case KeySetting.StartStop: StartStopKey = key; break;
            case KeySetting.Edit: EditKey = key; break;
            case KeySetting.Window: WindowKey = key; break;
            case KeySetting.Reset: ResetKey = key; break;
            case KeySetting.Mark: MarkKey = key; break;
            case KeySetting.PlayPause: PlayPauseKey = key; break;
            default: ReloadKey = key; break;
        }
    }

    public static void SetShowWindowDuringTake(bool value)
    {
        ShowWindowDuringTake = value;
        Save();
    }

    public static void SetExperimentalReplay(bool value)
    {
        ExperimentalReplay = value;
        Save();
    }

    public static void SetRecordWithTake(bool value)
    {
        RecordWithTake = value;
        Save();
    }

    public static void SetDeveloperMode(bool value)
    {
        DeveloperMode = value;
        Save();
    }

    // The text goes to a temporary file first, so a failed write never leaves half a file.
    public static bool Save()
    {
        if (LoadError.Length > 0)
        {
            SaveError = "The change is not saved, because the settings file could not be read. Correct the file or delete it, then use Read the settings file again.";
            return false;
        }

        try
        {
            string path = CineCamPaths.SettingsFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, Text(), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            SaveError = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = $"The settings file could not be written: {ex.Message}";
            DefaultCategory.Log.Warning("[CineCam] " + SaveError);
            return false;
        }
    }

    #endregion

    private static string Name(GlfwKey? key) => key.HasValue ? key.Value.ToString() : "";

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Text() => $$"""
{
  // Starts a take, or stops the running take, and closes a replay. Use a key name of the GLFW
  // key list, for example "Pause", "Home" or "KpEnter". Use a key that the game does not use,
  // because the game also does its own action for the key. An empty text turns the key off.
  "startStopKey": "{{Name(StartStopKey)}}",

  // Checks the shot file and logs the result. A take reads the file again at its start.
  "reloadKey": "{{Name(ReloadKey)}}",

  // Opens and closes the CineCam window, and shows the game interface again in a replay.
  // Insert has no stock binding in the game.
  "windowKey": "{{Name(WindowKey)}}",

  // Loads the checkpoint save of the default take and makes the take ready again. Empty turns
  // the key off. Use a key that you do not press by chance, because a reset replaces the flight.
  "resetKey": "{{Name(ResetKey)}}",

  // Pauses the running take so you can edit it, and resumes it when you press the key again.
  // The sim stops, the take clock stops and the camera is free. Empty turns the key off.
  "editKey": "{{Name(EditKey)}}",

  // Gives a mark during a take: the events "mark", "mark:2" and so on, for example as a cut
  // point in the event log. Empty turns the key off.
  "markKey": "{{Name(MarkKey)}}",

  // Plays and pauses an open replay. Home has no stock binding in the game. Space does not
  // work here, because the game moves the free camera up with it. Empty turns the key off.
  "playPauseKey": "{{Name(PlayPauseKey)}}",

  // The window is hidden while a take records. Set this to true to keep it on the screen.
  "showWindowDuringTake": {{Bool(ShowWindowDuringTake)}},

  // Shows the replay of recorded flights. The replay is experimental.
  "experimentalReplay": {{Bool(ExperimentalReplay)}},

  // Records the flight with each take that is not a dry run, for a replay. Needs experimentalReplay.
  "recordWithTake": {{Bool(RecordWithTake)}},

  // Shows the items for mod developers in the window: file paths, the local control and the autotest.
  "developerMode": {{Bool(DeveloperMode)}},

  // Allows local programs to control replay through files in the control folder.
  "enableLocalControl": {{Bool(EnableLocalControl)}}
}

""";

    private static bool ReadBool(JsonElement root, string name, bool fallback)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out JsonElement value))
            return fallback;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();

        DefaultCategory.Log.Warning($"[CineCam] Setting \"{name}\" must be true or false. The default stays.");
        return fallback;
    }

    private static GlfwKey? ReadKey(JsonElement root, string name, GlfwKey? fallback)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out JsonElement value))
            return fallback;
        if (value.ValueKind != JsonValueKind.String)
        {
            DefaultCategory.Log.Warning($"[CineCam] Setting \"{name}\" must be a key name as text. The default stays.");
            return fallback;
        }

        string text = value.GetString()!.Trim();
        if (text.Length == 0)
            return null;
        if (Enum.TryParse(text, true, out GlfwKey key) && Enum.IsDefined(key))
            return key;

        DefaultCategory.Log.Warning($"[CineCam] Setting \"{name}\": \"{text}\" is not a known key name. The default stays.");
        return fallback;
    }
}
