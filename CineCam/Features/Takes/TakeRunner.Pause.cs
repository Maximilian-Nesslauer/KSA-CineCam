using System.Globalization;
using Brutal.Logging;
using CineCam.Director;
using CineCam.Editing;
using CineCam.Patches;
using KSA;

namespace CineCam.Features.Takes;

// An edit pause stops the sim and the take clock and gives the camera to the player. The take
// time follows the flight, so a pause leaves no gap in it.
internal static partial class TakeRunner
{
    private enum EditRequest
    {
        None,
        Pause,
        Resume,
        Toggle,
    }

    #region Requests

    public static void RequestPause() => RequestEdit(EditRequest.Pause);

    public static void RequestResume() => RequestEdit(EditRequest.Resume);

    public static void RequestPauseToggle() => RequestEdit(EditRequest.Toggle);

    private static void RequestEdit(EditRequest request)
    {
        if (!Refused("pause", CineCamPatches.FrameBlock))
            _editRequest = request;
    }

    #endregion

    private static void ProcessEditRequest(EditRequest request)
    {
        ActiveTake? take = _take;
        if (take == null)
        {
            DefaultCategory.Log.Warning("[CineCam] No take runs, so there is nothing to pause.");
            return;
        }

        bool pause = request == EditRequest.Pause || (request == EditRequest.Toggle && !take.IsPaused);
        if (pause)
            Pause(take);
        else
            Resume(take);
    }

    private static void Pause(ActiveTake take)
    {
        if (take.IsPaused)
            return;
        if (take.Preview != null)
        {
            DefaultCategory.Log.Warning("[CineCam] A preview cannot be paused for an edit. Use its own pause.");
            return;
        }

        if (take.Offline != null)
        {
            DefaultCategory.Log.Warning("[CineCam] An offline capture cannot be paused, because its take time follows the pictures.");
            return;
        }

        try
        {
            double speed = Universe.GetSimulationSpeed();
            take.PausedSpeed = RehearsalEdit.SpeedToRemember(speed, take.LastWrittenSpeed, take.SpeedBefore);
            if (Universe.IsAutoWarpActive)
                Universe.AutoWarpStop(false);
            Universe.SetSimulationSpeed(0.0, alert: false);

            take.Clock.Pause(ClockSeconds);
            if (take.HidUi)
            {
                Program.DrawUI = true;
                Program.ConsoleWindow?.EnableMessageFading();
            }

            take.Camera.BeginFree();
            int shot = take.LastFrame?.ShotIndex ?? -1;
            string detail = string.Create(CultureInfo.InvariantCulture,
                $"the take is paused for an edit; simSpeedBefore={take.PausedSpeed}; shot={(shot >= 0 ? (shot + 1).ToString(CultureInfo.InvariantCulture) : "none")}; pause={take.Clock.PauseCount}");
            WriteRow(take, "pause", "start", detail);
            DefaultCategory.Log.Info(FormattableString.Invariant($"[CineCam] Take \"{take.Take.Name}\" paused at {take.Seconds:F1} s for an edit."));
        }
        catch (Exception ex)
        {
            Fail("the pause", ex);
        }
    }

    private static void Resume(ActiveTake take)
    {
        if (!take.IsPaused)
            return;

        try
        {
            double length = take.Clock.Resume(ClockSeconds);
            take.Camera.EndFree();
            take.LastTickFrame = _frame;
            if (take.HidUi)
            {
                Program.DrawUI = false;
                Program.ConsoleWindow?.DisableMessageFading();
                // Keeps the alert of the camera mode switch out of the pictures.
                TimedAlert.ClearAll();
            }
            else if (take.HideUiDeferred)
            {
                HideInterface(take);
                TimedAlert.ClearAll();
                WriteRow(take, "marker", "hideUi", "the game interface is hidden");
            }

            take.HideUiDeferred = false;

            Universe.SetSimulationSpeed(SpeedOrOne(take.PausedSpeed), alert: false);
            WriteRow(take, "pause", "end", string.Create(CultureInfo.InvariantCulture,
                $"the take goes on; length={length:F3} s real; total={take.Clock.PausedSeconds(ClockSeconds):F3} s real"));
            DefaultCategory.Log.Info(FormattableString.Invariant($"[CineCam] Take \"{take.Take.Name}\" resumed at {take.Seconds:F1} s after a pause of {length:F1} s."));
        }
        catch (Exception ex)
        {
            Fail("the resume", ex);
        }
    }

    // The camera hook does not run during a pause, so this keeps the vehicle events and the log
    // going. The take time stands still, so an event gets the time of the pause.
    private static void TickPaused(ActiveTake take)
    {
        double t = take.Seconds;
        WorldSample? world = BuildWorld(take, t);
        if (world == null)
            return;

        DirectorFrame frame = take.Director.Update(t, world);
        take.LastFrame = frame;
        LogFrame(take, t, frame);
    }

    private static double SpeedOrOne(double? speed) => speed is > 0.0 ? speed.Value : 1.0;
}
