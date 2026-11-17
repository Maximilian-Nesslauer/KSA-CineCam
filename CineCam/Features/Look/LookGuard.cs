using CineCam.Core;
using CineCam.Director;
using CineCam.Features.Replay;
using CineCam.Features.Takes;
using KSA;

namespace CineCam.Features.Look;

// Decides in every frame which game state CineCam changes now: the frame statistics, the game sound
// and the picture settings. Each part compares the wanted state with the one it set and gives the
// state of the player back as soon as nothing wants it any more, so every end of a take, a replay or
// a render restores it without its own call.
internal static class LookGuard
{
    /// <summary>True when the prefix of the GameSettings.Show setter is in place.</summary>
    public static bool SettingsGuarded { get; set; }

    // Runs in the prefix of Program.OnFrame after the requests of the frame, so a take that starts
    // in this frame has its sound and picture before the frame is drawn.
    public static void Update()
    {
        try
        {
            FrameStats.Update(HidesInterface());
            GameSound.Update(WantedSound());
            PictureGuard.Update(WantedPicture());
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("look-guard", $"[CineCam] Error in the sound and picture guard: {ex}");
        }
    }

    // The settings window copies GameSettings.Current when it opens and saves the copy on Apply.
    public static void BeforeSettingsOpen()
    {
        FrameStats.Restore();
        PictureGuard.Restore();
    }

    public static void Shutdown()
    {
        FrameStats.Restore();
        GameSound.Restore();
        PictureGuard.Restore();
    }

    public static void Reset()
    {
        SettingsGuarded = false;
        FrameStats.Reset();
        GameSound.Reset();
        PictureGuard.Reset();
    }

    private static bool HidesInterface() =>
        TakeRunner.HidesInterface || ReplaySession.IsRendering || (ReplaySession.IsPlayingBack && !Program.DrawUI);

    /// <summary>The take whose sound and picture apply now, from a live take or from the replay.</summary>
    private static Take? LookTake()
    {
        if (TakeRunner.Current is { Preview: null } running)
            return running.Take;
        if (ReplaySession.RenderTake is { } rendered)
            return rendered;
        return ReplaySession.IsPlayingBack && ReplaySession.CameraMode == ReplayCameraMode.Take ? ReplaySession.Plan?.Take : null;
    }

    private static SoundLevels WantedSound()
    {
        SoundLevels levels = SoundLevels.Of(LookTake()?.Sound);
        bool offline = TakeRunner.Current is { Offline: not null } || ReplaySession.IsRendering;
        if ((offline && CineCamSettings.MuteOfflineCapture) || (ReplaySession.IsPlayingBack && CineCamSettings.MuteReplay))
            levels = levels.Min(SoundLevels.Muted);
        return levels;
    }

    // Without the settings guard a pinned value could reach the settings window, so the take then pins
    // only while the interface is hidden and the window cannot be open.
    private static TakePicture? WantedPicture()
    {
        if (!PictureGuard.SaveGuarded || GameSettings.Show || (!SettingsGuarded && Program.DrawUI))
            return null;
        return LookTake()?.Picture;
    }
}
