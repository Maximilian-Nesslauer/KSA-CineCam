using Brutal.ImGuiApi.Abstractions;
using Brutal.Logging;
using CineCam.Features.Takes;
using CineCam.Features.TimeOfDay;
using KSA;

namespace CineCam.Features;

// The terminal uses the default values of the parameters when the player leaves an argument out.
internal sealed class CineCamCommands
{
    private static CineCamCommands? _registered;

    // The terminal may not exist yet when the mod loads, so the caller tries again until this succeeds.
    public static bool TryRegister()
    {
        if (_registered != null)
            return true;
        TerminalInterface? terminal = Program.ConsoleWindow?.Terminal;
        if (terminal == null)
            return false;

        var commands = new CineCamCommands();
        terminal.RegisterObject(commands);
        _registered = commands;
        DefaultCategory.Log.Info("[CineCam] Console commands registered: cinecam_start, cinecam_stop, cinecam_reload, cinecam_sun, cinecam_checkpoint, cinecam_reset, cinecam_pause, cinecam_mark.");
        return true;
    }

    public static void Unregister()
    {
        if (_registered == null)
            return;
        try
        {
            Program.ConsoleWindow?.Terminal?.UnregisterObject(_registered);
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Warning($"[CineCam] The console commands could not be removed: {ex.Message}");
        }

        _registered = null;
    }

    [TerminalAction("cinecam_start", "starts a CineCam take; optional argument: the name of the take", ArgParseMode.Default)]
    public void Start(string take = "") => TakeRunner.RequestStart(take);

    [TerminalAction("cinecam_stop", "stops the running CineCam take and gives the camera back", ArgParseMode.Default)]
    public void Stop() => TakeRunner.RequestStop();

    [TerminalAction("cinecam_reload", "reads the CineCam shot file and logs its takes and errors", ArgParseMode.Default)]
    public void Reload() => TakeRunner.RequestReload();

    [TerminalAction("cinecam_pause", "pauses the running CineCam take for an edit, or resumes a paused take", ArgParseMode.Default)]
    public void Pause() => TakeRunner.RequestPauseToggle();

    [TerminalAction("cinecam_mark", "gives a mark in the running CineCam take or flight recording: the events mark, mark:2 and so on", ArgParseMode.Default)]
    public void Mark() => TakeRunner.RequestMark();

    [TerminalAction("cinecam_checkpoint", "saves the game as the checkpoint of a CineCam take and stores the save name in the shot file; optional argument: the name of the take", ArgParseMode.Default)]
    public void Checkpoint(string take = "") => TakeRunner.RequestCheckpoint(take);

    [TerminalAction("cinecam_reset", "ends a running CineCam take, loads the checkpoint save of a take and makes the take ready; optional argument: the name of the take", ArgParseMode.Default)]
    public void Reset(string take = "") => TakeRunner.RequestReset(take);

    [TerminalAction("cinecam_sun", "moves the game time to the next time the sun at the launch site has this elevation in degrees; optional: set instead of rise (use \"set\")", ArgParseMode.Default)]
    public void Sun(double elevation = 5.0, string phase = "rise")
    {
        bool setting = string.Equals(phase, "set", StringComparison.OrdinalIgnoreCase);
        SunWarp.Start(elevation, setting, azimuthDeg: null, searchDays: 400.0);
    }
}
