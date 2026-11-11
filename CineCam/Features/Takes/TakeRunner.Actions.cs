using System.Globalization;
using Brutal.GlfwApi;
using Brutal.Logging;
using CineCam.Director;
using KSA;

namespace CineCam.Features.Takes;

internal static partial class TakeRunner
{
    #region Actions

    // Actions fire only in a frame that is not paused, so the vehicle never stages during an edit.
    private static void QueueActions(ActiveTake take, DirectorFrame frame)
    {
        foreach (int index in frame.StartedActions)
            take.PendingActions.Add(index);
    }

    private static void RunActions(ActiveTake take, double t)
    {
        if (take.PendingActions.Count == 0)
            return;

        if (take.DryRun)
        {
            take.PendingActions.Clear();
            return;
        }

        while (take.PendingActions.Count > 0)
        {
            int index = take.PendingActions[0];
            if (Run(take, index, take.Take.Actions[index], t) == StageVerdict.Wait)
                break;
            take.PendingActions.RemoveAt(0);
        }
    }

    private static StageVerdict Run(ActiveTake take, int index, TakeAction action, double t)
    {
        string name = action.Kind.ToString().ToLowerInvariant();
        string path = $"actions[{index}]";
        Vehicle vehicle = take.Vehicle.Vehicle;
        if (vehicle.IsDisposed)
        {
            WriteRow(take, "warning", name, $"{path}: skipped, the vehicle of the take is gone", t);
            return StageVerdict.Skip;
        }

        SequenceList list = vehicle.Parts.SequenceList;
        int next = list.GetNextSequenceNumber();
        int active = list.ActiveSequence;
        StageDecision decision = take.StageGuard.Decide(action.Sequence, next, active, t);
        string state = string.Create(CultureInfo.InvariantCulture, $"next={next}; active={active}");
        switch (decision.Verdict)
        {
            case StageVerdict.Wait:
                return StageVerdict.Wait;
            case StageVerdict.Skip:
                DefaultCategory.Log.Warning($"[CineCam] {path} ({name}) skipped: {decision.Reason}.");
                WriteRow(take, "warning", name, $"{path}: skipped; {decision.Reason}; {state}", t);
                return StageVerdict.Skip;
        }

        // Vehicle input handling calls SequenceList.ActivateNextSequence on the release of
        // InputAction.CameraUp, the staging key. Going through the input buffer stages at the same
        // place in the frame as a key press, which a direct call would not.
        InputEvents.VehicleInputBuffer.Add(new InputEvents.VehicleInputData
        {
            Vehicle = vehicle,
            Action = InputAction.CameraUp,
            KeyAction = GlfwKeyAction.Release,
            Modifiers = default,
        });
        DefaultCategory.Log.Info(FormattableString.Invariant($"[CineCam] {path} ({name}) sent the staging key at {t:F1} s; {state}."));
        WriteRow(take, "action", name, $"{path}; {decision.Reason}; {state}", t);
        return StageVerdict.Fire;
    }

    private static void ReportUnfiredActions(ActiveTake take, double seconds)
    {
        if (take.DryRun || take.Preview != null)
            return;
        for (int i = 0; i < take.Take.Actions.Count; i++)
        {
            if (!take.Director.Timeline.ActionStarted(i))
                WriteRow(take, "warning", take.Take.Actions[i].Kind.ToString().ToLowerInvariant(),
                    $"actions[{i}]: the trigger was not reached before the take ended", seconds);
            else if (take.PendingActions.Contains(i))
                WriteRow(take, "warning", take.Take.Actions[i].Kind.ToString().ToLowerInvariant(),
                    $"actions[{i}]: not fired before the take ended", seconds);
        }
    }

    #endregion
}
