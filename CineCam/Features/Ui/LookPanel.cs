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

        Widgets.Section(UiText.MessageText("text.d3d5794cbfa5", "Sound of the take"));
        TakeSound sound = take.Sound ?? new TakeSound();
        double? music = sound.Music;
        if (ShareChoice(UiText.MessageText("text.6eb00b4b2614", "Music"), ref music, UiText.MessageText("text.aa972b8d67ed", "The music of the game during the take, as a share of the music volume of the game settings.")))
        {
            sound.Music = music;
            changed = true;
        }

        double? effects = sound.Effects;
        if (ShareChoice(UiText.MessageText("text.a7180005e20c", "Sound effects"), ref effects, UiText.MessageText("text.1af063bc225e", "The engines, the wind, the alerts and the other sounds of the world during the take, as a share of the effects volume of the game settings.")))
        {
            sound.Effects = effects;
            changed = true;
        }

        if (changed)
            take.Sound = sound.IsEmpty ? null : sound;
        Widgets.Muted(CineCamSettings.MuteOfflineCapture || CineCamSettings.MuteReplay
            ? UiText.MessageText("text.f668ba29a96e", "The game volumes come back when the take ends. An offline capture, a replay and a render can mute all sound, see the Settings page.")
            : UiText.MessageText("text.1241aca83491", "The game volumes come back when the take ends."));

        Widgets.Section(UiText.MessageText("text.acac58aa4d26", "Picture of the take"));
        TakePicture picture = take.Picture ?? new TakePicture();
        bool pictureChanged = false;
        bool fixedExposure = picture.Exposure.HasValue;
        if (Widgets.Check(UiText.MessageText("text.2a15e9d8728d", "Fixed exposure"), ref fixedExposure, UiText.MessageText("text.c9b2c4017f82", "Uses this exposure of the tonemapper during the take and its render, in place of Tonemap Exposure of the game settings.")))
        {
            picture.Exposure = fixedExposure ? Math.Round((double)KSA.GameSettings.Current.Graphics.TonemapExposure, 3) : null;
            pictureChanged = true;
        }

        if (picture.Exposure is double exposure)
        {
            if (Widgets.Num(UiText.MessageText("text.cc5454a4b7ce", "Exposure"), ref exposure, 0.01f, TakePicture.MinExposure, TakePicture.MaxExposure, "%.3f",
                    UiText.MessageText("text.949cfbcd4cc2", "Higher is brighter. The game default is 1.25.")))
            {
                picture.Exposure = exposure;
                pictureChanged = true;
            }

            if (!PictureGuard.ExposureAvailable)
                Widgets.Reason(UiText.MessageText("text.6744abdfdd56", "This game build cannot change the exposure, see the log at the start of the game."));
        }

        bool? ambient = picture.CloudAmbientLight;
        if (PinChoice(UiText.MessageText("text.e647eb277eed", "Cloud ambient light"), ref ambient, UiText.MessageText("text.17eaa1bde1d6", "The light that the clouds throw on the vehicle and the ground.")))
        {
            picture.CloudAmbientLight = ambient;
            pictureChanged = true;
        }

        bool? shadows = picture.CloudShadows;
        if (PinChoice(UiText.MessageText("text.482e3fdf195b", "Cloud shadows"), ref shadows, UiText.MessageText("text.4a00ef7dd673", "The shadows of the clouds. On works only when they are on in the game settings, because the game sets their quality at its start.")))
        {
            picture.CloudShadows = shadows;
            pictureChanged = true;
        }

        bool? reflections = picture.Reflections;
        if (PinChoice(UiText.MessageText("text.9e676ed4a2c9", "Reflections"), ref reflections, UiText.MessageText("text.4b352de07cf7", "The reflections and the ambient light on the vehicle (Vessel Ambient Lighting in the debug menu of the game).")))
        {
            picture.Reflections = reflections;
            pictureChanged = true;
        }

        if (pictureChanged)
        {
            take.Picture = picture.IsEmpty ? null : picture;
            changed = true;
        }

        Widgets.Muted(UiText.MessageText("text.2818b4bb9827", "The settings apply while the take runs, while it renders, and in a replay with the take camera. The game settings of the player come back after it and are never saved."));

        bool exact = take.ExactApsides;
        if (Widgets.Check(UiText.MessageText("text.0f1e192c328e", "Land pictures on the apsides"), ref exact,
                UiText.MessageText("text.2c2f41efef25", "In an offline capture, a picture lands exactly on each apoapsis and periapsis of the orbit. The two pictures before it take a shorter step, so the video stays smooth.")))
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
        if (Widgets.Check(UiText.MessageText("text.2ce1a073c89c", "Mute the game during an offline capture and a render"), ref muteOffline,
                UiText.MessageText("text.68b1877c1a9e", "The pictures have no sound, so the game sound only disturbs. The volumes come back at the end.")))
            CineCamSettings.SetMuteOfflineCapture(muteOffline);
        bool muteReplay = CineCamSettings.MuteReplay;
        if (Widgets.Check(UiText.MessageText("text.6c7c27c9d7eb", "Mute the game during a replay"), ref muteReplay,
                UiText.MessageText("text.97b739763267", "Mutes all game sound, including recorded engine sounds. Turn this off to hear Engine sounds in a replay. The volumes come back when the replay closes.")))
            CineCamSettings.SetMuteReplay(muteReplay);
        if (muteReplay && CineCamSettings.ReplaySound)
            Widgets.Muted(UiText.MessageText("text.c47246c0b46d", "Replay engine sounds are enabled but muted here."));
        if (GameSound.Current is { IsFull: false } now)
            Widgets.Muted(UiText.Format("text.232e0b2a9869", $"CineCam lowers the game sound now: master {now.Master * 100.0:0} percent, music {now.Music * 100.0:0} percent, effects {now.Effects * 100.0:0} percent of the game settings."));
    }

    public static void DrawStill()
    {
        double scale = CineCamSettings.StillScale;
        if (Widgets.Num(UiText.MessageText("text.080c4b55076d", "Still size (times the window)"), ref scale, 0.05f, CineCamSettings.MinStillScale, CineCamSettings.MaxStillScale, "%.0f",
                UiText.MessageText("text.d779fb279777", "A still is one picture at this multiple of the window size. The game lowers a size that does not fit into the video memory.")))
            CineCamSettings.SetStillScale((int)Math.Round(scale));
        string? block = StillCapture.BlockReason;
        if (Widgets.Button(UiText.MessageText("text.e4626f6cb291", "TAKE STILL"), block == null, block ?? UiText.MessageText("text.33a6abe25148", "Saves one picture of the current view, from a take, a replay or the free camera, without the interface. The game draws 30 frames at the larger size first, so a running flight moves on by half a second; a replay pauses.")))
            StillCapture.Request(null);
        Widgets.Reason(block != null && !StillCapture.IsBusy ? block : "");
        if (StillCapture.Status.Length > 0)
            Widgets.Muted(StillCapture.Status);
        Widgets.Muted(UiText.Concat(UiText.Concat(UiText.MessageText("text.4d5fcd5856fa", "Stills go to "), CineCamPaths.StillsFolder), UiText.MessageText("text.d0a4de0e0555", ". The console command cinecam_still [scale] does the same.")));
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
        null => UiText.MessageText("text.abdfab2a4777", "As in the game"),
        0.0 => UiText.MessageText("text.ca7981b46ecf", "Off"),
        double v => UiText.Format("text.d58dcd19eef8", $"{v * 100.0:0.#} percent"),
    };

    private static bool PinChoice(string label, ref bool? value, string tip) =>
        Widgets.Choice(label, ref value, Pins, v => v switch { null => UiText.MessageText("text.abdfab2a4777", "As in the game"), true => UiText.MessageText("text.130011756125", "On"), false => UiText.MessageText("text.ca7981b46ecf", "Off") }, null, tip);
}
