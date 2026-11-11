using System.Diagnostics;
using Brutal.Logging;
using CineCam.Core;
using CineCam.Director;
using CineCam.Editing;
using CineCam.Features.Replay;
using CineCam.Features.TimeOfDay;
using CineCam.Patches;
using KSA;

namespace CineCam.Features.Takes;

// The checkpoint of a take is a game save. Saves and loads run at the end of a frame, as the game
// does for a load that it starts itself, so no vehicle worker is in the middle of a step.
internal static partial class TakeRunner
{
    private static readonly ResetFlow Flow = new();

    private static bool _checkpointRequested;
    private static string? _checkpointTake;
    private static bool _resetRequested;
    private static string? _resetTake;
    private static Vehicle? _resetOldVehicle;
    private static Vehicle? _resetNewVehicle;
    private static bool _resetHoldsSim;

    // The load that the last reset did, so a take that starts after it can say so in its log.
    private static int _resetLoadMark = -1;
    private static string _resetLoadedTake = "";
    private static string _resetLoadedSave = "";

    // Updated here in every frame, so it sees the end of a reset even while the window is closed.
    private static ReadyLatch _ready = new();

    public static ResetFlow Reset => Flow;

    /// <summary>True from the end of a reset until the next take starts.</summary>
    public static bool ReadyAfterReset => _ready.Ready;

    public static double ClockSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    #region Requests

    // A null take name uses the default take of the file.
    public static void RequestCheckpoint(string? takeName)
    {
        if (Refused("checkpoint", CineCamPatches.FrameBlock))
            return;
        if (ReplaySession.CheckpointBlock(reset: false) is { Length: > 0 } block)
        {
            DefaultCategory.Log.Warning($"[CineCam] No checkpoint saved: {block}");
            return;
        }

        _checkpointRequested = true;
        _checkpointTake = string.IsNullOrWhiteSpace(takeName) ? null : takeName.Trim();
    }

    public static void RequestReset(string? takeName)
    {
        if (Refused("reset", CineCamPatches.FrameBlock))
            return;
        if (ReplaySession.CheckpointBlock(reset: true) is { Length: > 0 } block)
        {
            DefaultCategory.Log.Warning($"[CineCam] No reset: {block}");
            return;
        }

        _resetRequested = true;
        _resetTake = string.IsNullOrWhiteSpace(takeName) ? null : takeName.Trim();
    }

    #endregion

    public static void OnFrameLate()
    {
        try
        {
            if (_checkpointRequested)
            {
                _checkpointRequested = false;
                string? name = _checkpointTake;
                _checkpointTake = null;
                SaveCheckpoint(name);
            }

            if (_resetRequested)
            {
                _resetRequested = false;
                string? name = _resetTake;
                _resetTake = null;
                BeginReset(name);
            }

            StepReset();
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("checkpoint", $"[CineCam] Error in the checkpoint code: {ex}");
            CancelReset("an error, see the log");
        }

        _ready.Update(Flow.Phase, _take != null);
    }

    private static Take? FindRequestedTake(ShotFile file, string? name)
    {
        Take? take = name == null ? file.DefaultTake : file.FindTake(name);
        if (take == null)
            DefaultCategory.Log.Error($"[CineCam] The shot file has no take \"{name}\". Takes: {string.Join(", ", file.Takes.Select(t => t.Name))}.");
        return take;
    }

    private static ShotFile? ResolveForCheckpoint()
    {
        try
        {
            return ResolveFile(out _, out _);
        }
        catch (ShotFileException ex)
        {
            DefaultCategory.Log.Error($"[CineCam] The unsaved changes of the window have errors: {ex.Message}");
            return null;
        }
    }

    #region Save

    private static void SaveCheckpoint(string? takeName)
    {
        if (_take != null || Flow.IsActive || SunWarp.IsRunning)
        {
            DefaultCategory.Log.Warning("[CineCam] A checkpoint is saved when no take, reset or time move runs.");
            return;
        }

        Vehicle? vehicle = Program.ControlledVehicle;
        if (!Program.IsLoaded || vehicle == null || vehicle.IsDisposed)
        {
            DefaultCategory.Log.Warning("[CineCam] A checkpoint needs a controlled vehicle in flight.");
            return;
        }

        if (GameSaves.RefusedInEditor())
            return;

        ShotFile? file = ResolveForCheckpoint();
        if (file == null)
            return;
        Take? take = FindRequestedTake(file, takeName);
        if (take == null)
            return;

        string save = take.Checkpoint
            ?? CheckpointName.ForTake(take.Name, file.Takes.Where(t => !ReferenceEquals(t, take)).Select(t => t.Checkpoint));
        if (!CheckpointName.IsValid(save))
        {
            DefaultCategory.Log.Error($"[CineCam] The checkpoint name \"{save}\" is not valid: {CheckpointName.RuleText}.");
            return;
        }

        // UncompressedSave.Make replaces a save of the same name without a question.
        GameSave? made = UncompressedSave.Make(save);
        if (made == null)
        {
            DefaultCategory.Log.Error($"[CineCam] The game could not write the save \"{save}\".");
            return;
        }

        DefaultCategory.Log.Info($"[CineCam] Checkpoint of take \"{take.Name}\" saved as the game save \"{save}\".");
        if (ShotFileStore.RecordCheckpoint(take.Name, save, out string where))
            DefaultCategory.Log.Info($"[CineCam] The name of the save is stored in {where}.");
        else
            DefaultCategory.Log.Warning($"[CineCam] The save \"{save}\" was made, but its name is not stored in the shot file ({where}). Set \"checkpoint\" of the take to this name.");
    }

    #endregion

    #region Reset

    private static void BeginReset(string? takeName)
    {
        if (Flow.IsActive)
        {
            DefaultCategory.Log.Warning("[CineCam] A reset runs already.");
            return;
        }

        if (SunWarp.IsRunning)
        {
            DefaultCategory.Log.Warning("[CineCam] The game time is moving to the sun elevation. Reset when that is done.");
            return;
        }

        if (!Program.IsLoaded || Program.Editor != null)
        {
            DefaultCategory.Log.Warning("[CineCam] A reset needs a loaded game, and it does not run in the vehicle editor.");
            return;
        }

        ShotFile? file = ResolveForCheckpoint();
        if (file == null)
            return;
        Take? take = FindRequestedTake(file, takeName);
        if (take == null)
            return;

        if (take.Checkpoint == null)
        {
            DefaultCategory.Log.Error($"[CineCam] Take \"{take.Name}\" has no checkpoint. Save one first (cinecam_checkpoint).");
            return;
        }

        string folder = Path.Combine(GameSaves.SaveFolderPath, take.Checkpoint);
        if (!File.Exists(Path.Combine(folder, "universe.xml")))
        {
            DefaultCategory.Log.Error($"[CineCam] The save \"{take.Checkpoint}\" of take \"{take.Name}\" does not exist in {GameSaves.SaveFolderPath}.");
            return;
        }

        _resetOldVehicle = Program.ControlledVehicle;
        _resetNewVehicle = null;
        _resetHoldsSim = false;
        // Without the camera hook the reset only loads the save, so the sim is not held for a start that cannot come.
        bool autoStart = take.AutoStart && !Refused("automatic start after the reset", CineCamPatches.CameraBlock);
        LogReset(Flow.Begin(take.Name, take.Checkpoint, autoStart, take.AutoStartDelay, ClockSeconds));
    }

    private static void StepReset()
    {
        if (!Flow.IsActive)
            return;

        ResetStep step = Flow.Step(new ResetInput(ClockSeconds, _take != null, NewVehicleIsReady()));
        LogReset(step.Message);
        switch (step.Action)
        {
            case ResetAction.EndTake:
                End("reset to the checkpoint");
                break;
            case ResetAction.LoadSave:
                LoadCheckpoint();
                break;
            case ResetAction.StartTake:
                // Starts in this frame, so the held sim never runs in between.
                if (_take != null)
                    End("another run was active at the automatic start");
                Start(Flow.TakeName, dryRun: false);
                if (_take == null)
                    LogReset(Flow.StartFailed("see the log lines above"));
                break;
        }

        if (!Flow.IsActive)
            ReleaseSimHold();
    }

    private static bool NewVehicleIsReady()
    {
        Vehicle? current = Program.ControlledVehicle;
        if (current == null || current.IsDisposed || Program.Editor != null)
            return false;
        return _resetNewVehicle != null && ReferenceEquals(current, _resetNewVehicle);
    }

    private static void LoadCheckpoint()
    {
        // A stale automatic warp would carry on in the new universe, and a speed that waits for a
        // menu to close would overwrite the speed of the load.
        _pendingSpeed = null;
        if (Universe.IsAutoWarpActive)
            Universe.AutoWarpStop(false);

        int before = SaveLoadWatch.Completed;
        bool completed;
        try
        {
            GameSaves.LoadSaveGame(Flow.SaveName);
            Vehicle? now = Program.ControlledVehicle;
            completed = SaveLoadWatch.Available
                ? SaveLoadWatch.Completed != before
                : now != null && !now.IsDisposed && !ReferenceEquals(now, _resetOldVehicle);
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error($"[CineCam] The load of the save \"{Flow.SaveName}\" threw an exception: {ex}");
            completed = false;
        }

        LogReset(Flow.LoadReturned(completed, ClockSeconds));
        if (!completed)
            return;

        _resetNewVehicle = Program.ControlledVehicle;
        _resetLoadMark = SaveLoadWatch.Completed;
        _resetLoadedTake = Flow.TakeName;
        _resetLoadedSave = Flow.SaveName;

        // Universe.DeserializeSave sets the sim speed to 1x. For an automatic start the sim stays
        // stopped until the take starts, so the vehicle waits in the state of the save.
        if (Flow.HoldsSim)
        {
            Universe.SetSimulationSpeed(0.0, alert: false);
            _resetHoldsSim = true;
            LogReset("Reset: the sim is stopped until the take starts.");
        }
    }

    private static void ReleaseSimHold()
    {
        if (!_resetHoldsSim)
            return;
        _resetHoldsSim = false;
        try
        {
            if (Universe.GetSimulationSpeed() == 0.0 && !Universe.IsAutoWarpActive)
                Universe.SetSimulationSpeed(1.0, alert: false);
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Warning($"[CineCam] The sim speed could not be given back: {ex.Message}");
        }
    }

    private static void CancelReset(string reason)
    {
        LogReset(Flow.Cancel(reason));
        ReleaseSimHold();
    }

    private static void LogReset(string message)
    {
        if (message.Length == 0)
            return;
        DefaultCategory.Log.Info($"[CineCam] {message}");
        if (_take is { } take)
            WriteRow(take, "info", "reset", message);
    }

    private static string ResetNoteFor(Take take)
    {
        bool fresh = _resetLoadMark >= 0 && _resetLoadMark == SaveLoadWatch.Completed
            && string.Equals(_resetLoadedTake, take.Name, StringComparison.OrdinalIgnoreCase);
        return fresh ? $"; afterReset=true; checkpoint={_resetLoadedSave}" : "; afterReset=false";
    }

    private static void ShutdownReset()
    {
        _checkpointRequested = false;
        _checkpointTake = null;
        _resetRequested = false;
        _resetTake = null;
        if (Flow.IsActive)
            CancelReset("the mod unloaded");
        ReleaseSimHold();
        _resetOldVehicle = null;
        _resetNewVehicle = null;
        _resetLoadMark = -1;
        _ready = new ReadyLatch();
    }

    #endregion
}
