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

    // __0 of the patch, the dtPlayer parameter of Universe.GetJobSimStep.
    public static void BeforeSimStep(double dtPlayer)
    {
        GuardSpeed();
        try
        {
            PlanExactStep(dtPlayer);
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("sim-step-exact", $"[CineCam] Error in the plan of an exact picture time: {ex}");
        }
    }

    private static void GuardSpeed()
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

    // An offline take with exactApsides lands one picture exactly on each apoapsis and periapsis that
    // the orbit of the vehicle forecasts (Orbit.GetNextApoapsisTime, Orbit.GetNextPeriapsisTime). The
    // limit must be set here, because Universe.ApplyVehicleSolvers clears it earlier in the frame, and
    // Universe.GetJobSimStep cuts the step at it (Universe.LimitNextStep). Only the step of a frame that
    // makes a picture is planned.
    private static void PlanExactStep(double dtPlayer)
    {
        if (_take is not { Offline: { } offline } take)
            return;
        offline.LandingTarget = null;
        offline.PlannedStep = null;
        if (!take.Take.ExactApsides || take.IsPaused || offline.StepFrame != _frame)
            return;
        double regular = dtPlayer * Universe.GetSimulationSpeed() * Universe.GetAchivedSpeedFraction();
        if (!(regular > 0.0))
            return;

        double now = Universe.GetElapsedSeconds();
        (double Time, string Name)? nearest = null;
        foreach (var target in ApsisTimes(take.Vehicle.Vehicle))
        {
            if (target.Time - now > ExactStep.Reached && (nearest == null || target.Time < nearest.Value.Time))
                nearest = target;
        }

        if (nearest is not { } next || ExactStep.Next(regular, next.Time - now) is not double step)
            return;
        Universe.LimitNextStep(Universe.GetElapsedTime() + step);
        offline.PlannedStep = step;
        if (Math.Abs(step - (next.Time - now)) <= ExactStep.Reached)
        {
            offline.LandingTarget = next.Time;
            offline.LandingName = next.Name;
        }
    }

    // The same free flight as the apsis events of VehicleReader.Sample: no ground contact, outside the
    // atmosphere, a closed orbit, and on an airless body a periapsis above the terrain. A vehicle on a
    // pad or in a hop in the air has an orbit too, but its apsides are no events.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static List<(double Time, string Name)> ApsisTimes(Vehicle vehicle)
    {
        var times = new List<(double, string)>(2);
        if (vehicle.IsDisposed || vehicle.Situation.HasAnyContact())
            return times;
        Orbit orbit = vehicle.Orbit;
        IParentBody parent = vehicle.Parent;
        double atmosphere = parent.GetAtmosphereRadius();
        if (orbit.GetOrbitType() != Orbit.OrbitType.Elliptical || vehicle.GetPositionCci().Length() <= atmosphere
            || (atmosphere <= 0.0 && orbit.Periapsis < parent.GetNearSurfaceRadius()))
            return times;
        UniverseTime now = Universe.GetElapsedTime();
        if (orbit.GetNextApoapsisTime(now) is UniverseTime apoapsis)
            times.Add((apoapsis.Seconds(), "apoapsis"));
        if (orbit.GetNextPeriapsisTime(now) is UniverseTime periapsis)
            times.Add((periapsis.Seconds(), "periapsis"));
        return times;
    }

    // Universe.LimitNextStep cuts the step at the time of a forecast (life support, radiation dose,
    // probe power) without a change of Universe.GetAchivedSpeedFraction, so the slow sim check of the
    // offline capture does not see it. A step that CineCam cut for an exact picture time is no warning.
    public static void AfterSimStep(double stepSeconds, double dtPlayer)
    {
        try
        {
            if (_take is not { Offline: { } offline } take)
                return;
            double? planned = offline.PlannedStep;
            offline.PlannedStep = null;
            if (planned is double plan && stepSeconds >= plan * 0.999)
            {
                if (offline.LandingTarget is double landing)
                {
                    // The step of this frame is applied in the next frame, whose picture shows it.
                    WriteRow(take, "info", "exactTime", string.Create(CultureInfo.InvariantCulture,
                        $"picture {offline.FrameNumber + 1} shows the {offline.LandingName} at game time {landing:F6}; the step was {stepSeconds:G6} s"));
                }

                offline.LandingTarget = null;
                return;
            }

            offline.LandingTarget = null;
            if (offline.WarnedStepLimit)
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
