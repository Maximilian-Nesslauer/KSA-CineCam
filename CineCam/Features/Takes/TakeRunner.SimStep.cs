using System.Globalization;
using Brutal.Logging;
using CineCam.Core;
using CineCam.Director;
using KSA;

namespace CineCam.Features.Takes;

// Universe.GetJobSimStep turns the sim speed into the step of the frame. Earlier in the same frame
// (Universe.ApplyVehicleSolvers) the game can set the speed to 1x by itself: Vehicle.UpdateControl
// does it when an uncrewed vehicle loses control, and Vehicle.UpdateProbePowerWarning when its probe
// core has 5 minutes of power left. The frame hook of CineCam runs only after that step, so the hold
// of a reset and the speed track of a take are written again here, before the step is made. The
// prefix runs after the keys of the frame (Program.OnKey in Program.PrepareFrame), so it also undoes a
// speed key of the player, as the speed track does in every frame. It leaves the speed alone where the
// take does not own it: an edit pause, a dry run, an automatic warp and a menu pause.
internal static partial class TakeRunner
{
    private static bool _resetHoldLogged;

    public static void BeforeSimStep()
    {
        try
        {
            double now = Universe.GetSimulationSpeed();
            if (_resetHoldsSim)
            {
                // Universe.ProcessAutoWarp sets the speed in every frame of a warp, so a warp that
                // started during the hold would never end against a speed of 0.
                if (Universe.IsAutoWarpActive)
                    Universe.AutoWarpStop(resetSimulationSpeed: false);
                if (now != 0.0)
                {
                    Universe.SetSimulationSpeed(0.0, alert: false);
                    if (!_resetHoldLogged)
                    {
                        _resetHoldLogged = true;
                        LogReset(string.Create(CultureInfo.InvariantCulture,
                            $"Reset: the sim speed was set to {now}; it stays stopped until the take starts."));
                    }
                }

                return;
            }

            if (_take is not { } take || take.DryRun || take.IsPaused || take.LastWrittenSpeed is not double last)
                return;
            // An automatic warp and the pause of a menu keep the speed, as in SpeedPolicy.
            if (Universe.IsAutoWarpActive || now == 0.0 || Math.Abs(now - last) <= 1e-6)
                return;
            Universe.SetSimulationSpeed(last, alert: false);
            // The stops of the game for an uncrewed vehicle (Vehicle.UpdateControl,
            // Vehicle.UpdateProbePowerWarning) and for life support always set exactly 1x.
            bool gameStop = now == 1.0;
            if (gameStop && !take.ReportedGameSpeedStop)
            {
                take.ReportedGameSpeedStop = true;
                WriteRow(take, "info", "gameSpeedStop", string.Create(CultureInfo.InvariantCulture,
                    $"the game set the sim speed to {now}; the take keeps {last}"));
            }
            else if (!gameStop && !take.ReportedSpeedOverride)
            {
                take.ReportedSpeedOverride = true;
                WriteRow(take, "info", "speedOverride", string.Create(CultureInfo.InvariantCulture,
                    $"the sim speed was {now} and not {last}; the take sets it again"));
            }
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("sim-step", $"[CineCam] Error in the sim step guard: {ex}");
        }
    }

    // Universe.LimitNextStep cuts the step at the time of a forecast (life support, radiation dose,
    // probe power) without a change of Universe.GetAchivedSpeedFraction, so the slow sim check of the
    // offline capture does not see it.
    public static void AfterSimStep(double stepSeconds, double dtPlayer)
    {
        try
        {
            if (_take is not { Offline: { } offline } take || offline.WarnedStepLimit)
                return;
            double expected = dtPlayer * Universe.GetSimulationSpeed() * Universe.GetAchivedSpeedFraction();
            if (!(expected > 0.0) || stepSeconds >= expected * 0.999)
                return;
            offline.WarnedStepLimit = true;
            string text = string.Create(CultureInfo.InvariantCulture,
                $"The game made a sim step of {stepSeconds:G4} s instead of {expected:G4} s, because a forecast of the game (life support, radiation dose or probe power) ends there. The video has a short step at this picture.");
            DefaultCategory.Log.Warning($"[CineCam] {text}");
            WriteRow(take, "warning", "simStepLimit", text);
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("sim-step-after", $"[CineCam] Error in the sim step check: {ex}");
        }
    }
}
