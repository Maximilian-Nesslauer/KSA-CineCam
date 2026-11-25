using System.Globalization;

namespace CineCam.Director;

public enum ResetPhase
{
    Idle,
    Requested,
    EndingTake,
    Loading,
    WaitingVehicle,
    Countdown,
    Done,
    Failed,
    Cancelled,
}

public enum ResetAction
{
    None,
    EndTake,
    LoadSave,
    StartTake,
}

/// <param name="Now">A clock in seconds that never goes back, for example the real time of the process.</param>
/// <param name="TakeRunning">True while a take, a dry run or a preview runs.</param>
/// <param name="VehicleReady">True while the controlled vehicle is the one that the load made and it is usable.</param>
public readonly record struct ResetInput(double Now, bool TakeRunning, bool VehicleReady);

public readonly record struct ResetStep(ResetAction Action, string Message)
{
    public static readonly ResetStep Nothing = new(ResetAction.None, "");
}

/// <summary>
/// The steps of a reset to a checkpoint, with no game access. The caller calls <see cref="Step"/>
/// once per frame, and <see cref="LoadFinished"/> when the requested save applies or the request fails.
/// </summary>
public sealed class ResetFlow
{
    /// <summary>Frames in a row in which the vehicle must be ready, because the solver of a new vehicle builds its state in the first frames.</summary>
    public const int ReadyFrames = 3;

    public const double EndTimeoutSeconds = 5.0;

    public const double LoadTimeoutSeconds = 20.0;

    public const double VehicleTimeoutSeconds = 20.0;

    private double _phaseStart;
    private int _readyCount;

    public ResetPhase Phase { get; private set; } = ResetPhase.Idle;

    public string TakeName { get; private set; } = "";

    public string SaveName { get; private set; } = "";

    public bool AutoStart { get; private set; }

    public double Delay { get; private set; }

    public string Outcome { get; private set; } = "";

    public bool IsActive => Phase is >= ResetPhase.Requested and <= ResetPhase.Countdown;

    public bool IsCountingDown => Phase == ResetPhase.Countdown;

    /// <summary>
    /// True when the game must stay paused until the take starts, because a game that runs during the
    /// delay would move the vehicle away from the state of the checkpoint.
    /// </summary>
    public bool HoldsSim => IsActive && AutoStart;

    public double? SecondsToStart(double now) =>
        Phase == ResetPhase.Countdown ? Math.Max(0.0, Delay - (now - _phaseStart)) : null;

    public string Begin(string takeName, string saveName, bool autoStart, double delaySeconds, double now)
    {
        TakeName = takeName;
        SaveName = saveName;
        AutoStart = autoStart;
        Delay = Math.Max(0.0, delaySeconds);
        _phaseStart = now;
        _readyCount = 0;
        Phase = ResetPhase.Requested;
        Outcome = "";
        return UiText.Format("text.a8506a046f62", $"Reset of take \"{takeName}\" to the checkpoint \"{saveName}\" requested.");
    }

    public ResetStep Step(ResetInput input)
    {
        switch (Phase)
        {
            case ResetPhase.Requested:
                if (input.TakeRunning)
                {
                    Enter(ResetPhase.EndingTake, input.Now);
                    return new ResetStep(ResetAction.EndTake, UiText.MessageText("text.03205a6b2683", "Reset: the running take ends first."));
                }

                Enter(ResetPhase.Loading, input.Now);
                return new ResetStep(ResetAction.LoadSave, UiText.Format("text.87a03f8ae71c", $"Reset: loading the save \"{SaveName}\"."));

            case ResetPhase.EndingTake:
                if (!input.TakeRunning)
                {
                    Enter(ResetPhase.Loading, input.Now);
                    return new ResetStep(ResetAction.LoadSave, UiText.Format("text.c2e2ca283c9b", $"Reset: the take ended, loading the save \"{SaveName}\"."));
                }

                if (input.Now - _phaseStart > EndTimeoutSeconds)
                    return Fail(UiText.MessageText("text.1df402967a54", "the running take did not end"));
                return ResetStep.Nothing;

            case ResetPhase.Loading:
                if (input.Now - _phaseStart > LoadTimeoutSeconds)
                    return new ResetStep(ResetAction.None, LoadFinished(false, input.Now));
                return ResetStep.Nothing;

            case ResetPhase.WaitingVehicle:
                _readyCount = input.VehicleReady ? _readyCount + 1 : 0;
                if (_readyCount >= ReadyFrames)
                    return VehicleIsReady(input.Now);
                if (input.Now - _phaseStart > VehicleTimeoutSeconds)
                    return Fail(UiText.Format("text.eae913eee564", $"the vehicle was not ready {VehicleTimeoutSeconds:0} s after the load"));
                return ResetStep.Nothing;

            case ResetPhase.Countdown:
                if (!input.VehicleReady)
                    return Fail(UiText.MessageText("text.da3da91a788b", "the controlled vehicle changed before the take started"));
                if (input.Now - _phaseStart >= Delay)
                {
                    Phase = ResetPhase.Done;
                    Outcome = UiText.Format("text.9410a77812ef", $"Take \"{TakeName}\" started after the reset.");
                    return new ResetStep(ResetAction.StartTake, UiText.MessageText("text.07e542282f01", "Reset: the delay is over, the take starts."));
                }

                return ResetStep.Nothing;

            default:
                return ResetStep.Nothing;
        }
    }

    public string LoadFinished(bool completed, double now)
    {
        if (Phase != ResetPhase.Loading)
            return "";
        if (!completed)
            return Fail(UiText.MessageText("text.84e195da7aca", "the game did not load the save (see the lines above in the log)")).Message;
        Enter(ResetPhase.WaitingVehicle, now);
        return UiText.Format("text.63cc34029942", $"Reset: the save \"{SaveName}\" is loaded, waiting for the vehicle.");
    }

    public string StartFailed(string reason)
    {
        if (Phase != ResetPhase.Done)
            return "";
        return Fail(UiText.Concat(UiText.Concat(UiText.MessageText("text.0eb72c023980", "the take did not start after the reset ("), reason), ")")).Message;
    }

    public string Cancel(string reason)
    {
        if (!IsActive)
            return "";
        Phase = ResetPhase.Cancelled;
        Outcome = UiText.Concat(UiText.Concat(UiText.MessageText("text.d8a00cbfbd80", "Reset cancelled: "), reason), ".");
        return Outcome;
    }

    private ResetStep VehicleIsReady(double now)
    {
        if (!AutoStart)
        {
            Phase = ResetPhase.Done;
            Outcome = UiText.MessageText("text.1847b172683a", "Ready. Start the take when you want.");
            return new ResetStep(ResetAction.None, UiText.Format("text.bfbf05839f8c", $"Reset: the vehicle is ready. The take \"{TakeName}\" waits for the player."));
        }

        Enter(ResetPhase.Countdown, now);
        return new ResetStep(ResetAction.None,
            UiText.Format("text.19fdd6a59f7b", $"Reset: the vehicle is ready. The take \"{TakeName}\" starts in {Delay:0.##} s."));
    }

    private ResetStep Fail(string reason)
    {
        Phase = ResetPhase.Failed;
        Outcome = UiText.Concat(UiText.Concat(UiText.MessageText("text.c9346576c9ad", "Reset failed: "), reason), ".");
        return new ResetStep(ResetAction.None, Outcome);
    }

    private void Enter(ResetPhase phase, double now)
    {
        Phase = phase;
        _phaseStart = now;
    }
}
