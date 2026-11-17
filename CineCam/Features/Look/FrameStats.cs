using System.Runtime.CompilerServices;
using CineCam.Core;
using KSA;

namespace CineCam.Features.Look;

// Program.DrawFps draws the frame statistics (GameSettings.ShowFrameStatistics) in every frame, also
// while Program.DrawUI is false, so they would be in every picture and screen recording of a take
// that hides the interface. The setting is turned off for that time and on again after it. The file
// of the settings is never written. A player who turns the statistics on again during the hide (the
// stock key) keeps them.
internal static class FrameStats
{
    private static GameSettings? _owner;
    private static bool _released;
    private static bool _broken;

    public static void Update(bool hide)
    {
        if (_broken)
            return;
        try
        {
            UpdateCore(hide);
        }
        catch (Exception ex)
        {
            _broken = true;
            _owner = null;
            LogHelper.WarnOnce("frame-stats", $"[CineCam] The frame statistics of the game could not be hidden: {ex.Message}");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void UpdateCore(bool hide)
    {
        if (!hide)
        {
            _released = false;
            Restore();
            return;
        }

        GameSettings current = GameSettings.Current;
        if (_owner != null)
        {
            // The player turned them on, or applied other settings, during the hide.
            if (!ReferenceEquals(current, _owner) || current.Interface.FrameStatistics)
            {
                _owner = null;
                _released = true;
            }

            return;
        }

        if (_released || !current.Interface.FrameStatistics)
            return;
        current.Interface.FrameStatistics = false;
        _owner = current;
    }

    // The settings window copies GameSettings.Current when it opens, so the value goes back first.
    public static void Restore()
    {
        if (_owner == null)
            return;
        GameSettings owner = _owner;
        _owner = null;
        try
        {
            RestoreCore(owner);
        }
        catch (Exception ex)
        {
            _broken = true;
            LogHelper.WarnOnce("frame-stats-restore", $"[CineCam] The frame statistics of the game could not be shown again: {ex.Message}");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RestoreCore(GameSettings owner)
    {
        if (ReferenceEquals(GameSettings.Current, owner))
            owner.Interface.FrameStatistics = true;
    }

    public static void Reset()
    {
        _owner = null;
        _released = false;
        _broken = false;
    }
}
