using System.Runtime.CompilerServices;
using CineCam.Core;
using CineCam.Director;
using KSA;

namespace CineCam.Features.Look;

// Lowers the channel groups of the game (GameAudio.GetChannelGroup) below the volumes of the game
// settings. The volume is set again in every frame from the settings, so a change in the audio
// settings during a take keeps its share. GameAudio.ApplySettings gives the volumes of the settings
// back, the same call that the game makes when the player applies the settings.
internal static class GameSound
{
    private static bool _applied;
    private static bool _broken;

    public static SoundLevels Current { get; private set; } = SoundLevels.Full;

    public static void Update(SoundLevels levels)
    {
        if (_broken)
            return;
        if (levels.IsFull)
        {
            Restore();
            return;
        }

        try
        {
            ApplyCore(levels);
            _applied = true;
            Current = levels;
        }
        catch (Exception ex)
        {
            _broken = true;
            LogHelper.WarnOnce("game-sound", $"[CineCam] The game sound could not be lowered: {ex.Message}");
            Restore();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ApplyCore(SoundLevels levels)
    {
        GameSettings.AudioSettings audio = GameSettings.Current.Audio;
        Set(ChannelGroupType.Master, audio.MasterVolume, levels.Master);
        Set(ChannelGroupType.Music, audio.MusicVolume, levels.Music);
        Set(ChannelGroupType.Sfx, audio.SfxVolume, levels.Effects);
    }

    private static void Set(ChannelGroupType group, int setting, double share) =>
        GameAudio.SetChannelGroupVolume(GameAudio.GetChannelGroup(group), GameAudio.MapVolumeSetting(setting) * (float)share);

    public static void Restore()
    {
        if (!_applied)
            return;
        _applied = false;
        Current = SoundLevels.Full;
        try
        {
            RestoreCore();
        }
        catch (Exception ex)
        {
            _broken = true;
            LogHelper.WarnOnce("game-sound-restore", $"[CineCam] The game sound could not be set back: {ex.Message}");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RestoreCore() => GameAudio.ApplySettings();

    public static void Reset()
    {
        _applied = false;
        _broken = false;
        Current = SoundLevels.Full;
    }
}
