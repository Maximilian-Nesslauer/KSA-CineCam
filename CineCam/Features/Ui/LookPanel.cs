using System.Globalization;
using CineCam.Core;
using CineCam.Director;
using CineCam.Editing;
using CineCam.Features.Look;

namespace CineCam.Features.Ui;

// The sound and picture options of a take (Take setup), and the sound, grab and still settings of the
// Settings page.
internal static class LookPanel
{
    private static readonly double?[] Shares = [null, 0.0, 0.25, 0.5, 0.75];
    private static readonly bool?[] Pins = [null, true, false];

    public static void DrawTake(EditSession session)
    {
        if (session.Take is not { } take)
            return;
        bool changed = false;

        Widgets.Section("Sound of the take");
        TakeSound sound = take.Sound ?? new TakeSound();
        double? music = sound.Music;
        if (ShareChoice("Music", ref music, "The music of the game during the take, as a share of the music volume of the game settings."))
        {
            sound.Music = music;
            changed = true;
        }

        double? effects = sound.Effects;
        if (ShareChoice("Sound effects", ref effects, "The engines, the wind, the alerts and the other sounds of the world during the take, as a share of the effects volume of the game settings."))
        {
            sound.Effects = effects;
            changed = true;
        }

        if (changed)
            take.Sound = sound.IsEmpty ? null : sound;
        Widgets.Muted(CineCamSettings.MuteOfflineCapture || CineCamSettings.MuteReplay
            ? "The game volumes come back when the take ends. An offline capture, a replay and a render can mute all sound, see the Settings page."
            : "The game volumes come back when the take ends.");

        Widgets.Section("Picture of the take");
        TakePicture picture = take.Picture ?? new TakePicture();
        bool pictureChanged = false;
        bool fixedExposure = picture.Exposure.HasValue;
        if (Widgets.Check("Fixed exposure", ref fixedExposure, "Uses this exposure of the tonemapper during the take and its render, in place of Tonemap Exposure of the game settings."))
        {
            picture.Exposure = fixedExposure ? Math.Round((double)KSA.GameSettings.Current.Graphics.TonemapExposure, 3) : null;
            pictureChanged = true;
        }

        if (picture.Exposure is double exposure)
        {
            if (Widgets.Num("Exposure", ref exposure, 0.01f, TakePicture.MinExposure, TakePicture.MaxExposure, "%.3f",
                    "Higher is brighter. The game default is 1.25."))
            {
                picture.Exposure = exposure;
                pictureChanged = true;
            }

            if (!PictureGuard.ExposureAvailable)
                Widgets.Reason("This game build cannot change the exposure, see the log at the start of the game.");
        }

        bool? ambient = picture.CloudAmbientLight;
        if (PinChoice("Cloud ambient light", ref ambient, "The light that the clouds throw on the vehicle and the ground."))
        {
            picture.CloudAmbientLight = ambient;
            pictureChanged = true;
        }

        bool? shadows = picture.CloudShadows;
        if (PinChoice("Cloud shadows", ref shadows, "The shadows of the clouds. On works only when they are on in the game settings, because the game sets their quality at its start."))
        {
            picture.CloudShadows = shadows;
            pictureChanged = true;
        }

        bool? reflections = picture.Reflections;
        if (PinChoice("Reflections", ref reflections, "The reflections and the ambient light on the vehicle (Vessel Ambient Lighting in the debug menu of the game)."))
        {
            picture.Reflections = reflections;
            pictureChanged = true;
        }

        if (pictureChanged)
        {
            take.Picture = picture.IsEmpty ? null : picture;
            changed = true;
        }

        Widgets.Muted("The settings apply while the take runs, while it renders, and in a replay with the take camera. The game settings of the player come back after it and are never saved.");

        bool exact = take.ExactApsides;
        if (Widgets.Check("Land pictures on the apsides", ref exact,
                "In an offline capture, a picture lands exactly on each apoapsis and periapsis of the orbit. The two pictures before it take a shorter step, so the video stays smooth."))
        {
            take.ExactApsides = exact;
            changed = true;
        }

        if (changed)
            session.Changed();
    }

    public static void DrawSettings()
    {
        bool muteOffline = CineCamSettings.MuteOfflineCapture;
        if (Widgets.Check("Mute the game during an offline capture and a render", ref muteOffline,
                "The pictures have no sound, so the game sound only disturbs. The volumes come back at the end."))
            CineCamSettings.SetMuteOfflineCapture(muteOffline);
        bool muteReplay = CineCamSettings.MuteReplay;
        if (Widgets.Check("Mute the game during a replay", ref muteReplay,
                "The live flight is paused under the replay, so its sounds do not belong to the replay. The volumes come back when the replay closes."))
            CineCamSettings.SetMuteReplay(muteReplay);
        if (GameSound.Current is { IsFull: false } now)
            Widgets.Muted(string.Create(CultureInfo.InvariantCulture,
                $"CineCam lowers the game sound now: master {now.Master * 100.0:0} percent, music {now.Music * 100.0:0} percent, effects {now.Effects * 100.0:0} percent of the game settings."));
    }

    public static void DrawStill()
    {
        double scale = CineCamSettings.StillScale;
        if (Widgets.Num("Still size (times the window)", ref scale, 0.05f, CineCamSettings.MinStillScale, CineCamSettings.MaxStillScale, "%.0f",
                "A still is one picture at this multiple of the window size. The game lowers a size that does not fit into the video memory."))
            CineCamSettings.SetStillScale((int)Math.Round(scale));
        string? block = StillCapture.BlockReason;
        if (Widgets.Button("TAKE STILL", block == null, block ?? "Saves one picture of the current view, from a take, a replay or the free camera, without the interface. The game draws 30 frames at the larger size first, so a running flight moves on by half a second; a replay pauses."))
            StillCapture.Request(null);
        Widgets.Reason(block != null && !StillCapture.IsBusy ? block : "");
        if (StillCapture.Status.Length > 0)
            Widgets.Muted(StillCapture.Status);
        Widgets.Muted("Stills go to " + CineCamPaths.StillsFolder + ". The console command cinecam_still [scale] does the same.");
    }

    private static bool ShareChoice(string label, ref double? value, string tip)
    {
        List<double?> options = Shares.ToList();
        if (!options.Contains(value))
            options.Add(value);
        return Widgets.Choice(label, ref value, options, ShareName, null, tip);
    }

    private static string ShareName(double? share) => share switch
    {
        null => "As in the game",
        0.0 => "Off",
        double v => string.Create(CultureInfo.InvariantCulture, $"{v * 100.0:0.#} percent"),
    };

    private static bool PinChoice(string label, ref bool? value, string tip) =>
        Widgets.Choice(label, ref value, Pins, v => v switch { null => "As in the game", true => "On", false => "Off" }, null, tip);
}
