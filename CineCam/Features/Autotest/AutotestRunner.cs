using System.Diagnostics;
using System.Globalization;
using Brutal.GlfwApi;
using Brutal.Logging;
using CineCam.Core;
using CineCam.Director;
using CineCam.Features.Takes;
using CineCam.Patches;
using KSA;

namespace CineCam.Features.Autotest;

// An in-game self test, active only when the environment variable CINECAM_AUTOTEST names a result
// folder. It writes result.json there and ends the game.
internal static partial class AutotestRunner
{
    public const string Variable = "CINECAM_AUTOTEST";

    private const double LoadTimeoutSeconds = 300.0;
    private const double NoContactTeleportSeconds = 30.0;
    private const double NoContactFailSeconds = 90.0;
    private const int SettleFrames = 150;
    private const int GapFrames = 60;
    private const int EndFrames = 40;
    private const string PadId = "CCSFS LC-39A";

    private enum Phase
    {
        WaitLoad,
        Settle,
        StartA,
        RunA,
        EndA,
        Gap,
        StartB,
        RunB,
        EndB,
        Exiting,
    }

    private static string _root = "";
    private static bool _active;
    private static volatile bool _refused;
    private static volatile bool _finished;
    private static Stopwatch _clock = new();
    private static StreamWriter? _log;
    private static Phase _phase;
    private static long _frame;
    private static long _phaseFrame;
    private static double _phaseSeconds;
    private static string? _failure;
    private static readonly Report _report = new();

    // The watchdog thread writes the log, the failure and the result too.
    private static readonly object Gate = new();

    public static bool IsActive => _active;

    public static void TryStart()
    {
        string? folder = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(folder))
            return;
        try
        {
            _root = Path.GetFullPath(folder);
            Directory.CreateDirectory(_root);
            _log = new StreamWriter(new FileStream(Path.Combine(_root, "autotest.log"), FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
            _clock = Stopwatch.StartNew();
            Log($"Autotest starts. Result folder: {_root}");
            // The test runs in the frame hook and drives takes, so without the hooks only the
            // watchdog can end the game.
            if (CineCamPatches.CameraBlock is { } block)
            {
                _refused = true;
                Fail($"the self test cannot run: {block}");
            }
            else
            {
                _active = true;
            }

            StartWatchdog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DefaultCategory.Log.Error($"[CineCam] The autotest folder {folder} could not be used: {ex.Message}");
        }
    }

    public static void Shutdown()
    {
        _finished = true;
        lock (Gate)
        {
            _log?.Dispose();
            _log = null;
        }
    }

    public static void OnFrameEnd()
    {
        if (!_active || _finished)
            return;
        MarkFrame();
        _frame++;
        lock (Gate)
        {
            try
            {
                Step();
            }
            catch (Exception ex)
            {
                Log($"Exception: {ex}");
                Fail($"exception in the test: {ex.Message}");
            }
        }
    }

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static double Now => _clock.Elapsed.TotalSeconds;

    private static void Log(string text)
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"[{Now,7:F1}] {text}");
        lock (Gate)
        {
            try
            {
                _log?.WriteLine(line);
            }
            catch (IOException)
            {
                // The game log still has the line.
            }
        }

        DefaultCategory.Log.Info("[CineCam] Autotest: " + text);
    }

    private static void SetPhase(Phase phase)
    {
        _phase = phase;
        _phaseFrame = _frame;
        _phaseSeconds = Now;
        Log($"Phase {phase}");
    }

    private static int FramesInPhase => (int)(_frame - _phaseFrame);

    private static double SecondsInPhase => Now - _phaseSeconds;

    // The checks made so far stay in the result.
    private static void Fail(string reason)
    {
        if (_failure != null)
            return;
        _failure = reason;
        Log($"FAILED: {reason}");
        BeginExit();
    }

    private static void BeginExit()
    {
        if (_phase == Phase.Exiting)
            return;
        TakeRunner.RequestStop();
        SetPhase(Phase.Exiting);
    }

    private static void Step()
    {
        switch (_phase)
        {
            case Phase.WaitLoad:
                WaitLoad();
                break;
            case Phase.Settle:
                Settle();
                break;
            case Phase.StartA:
                StartTake(AutotestPlan.TakeAName, Phase.RunA);
                break;
            case Phase.RunA:
                RunA();
                break;
            case Phase.EndA:
                if (FramesInPhase >= EndFrames && GrabIdle())
                {
                    FinishA();
                    SetPhase(Phase.Gap);
                }

                break;
            case Phase.Gap:
                if (FramesInPhase >= GapFrames)
                    SetPhase(Phase.StartB);
                break;
            case Phase.StartB:
                PrepareB();
                break;
            case Phase.RunB:
                RunB();
                break;
            case Phase.EndB:
                if (FramesInPhase >= EndFrames)
                {
                    FinishB();
                    FinishRun();
                }

                break;
            case Phase.Exiting:
                if (FramesInPhase >= EndFrames)
                    Exit();
                break;
        }
    }

    #region Start of the game

    private static Vehicle? Controlled()
    {
        Vehicle? vehicle = Program.ControlledVehicle;
        return vehicle == null || vehicle.IsDisposed ? null : vehicle;
    }

    private static void WaitLoad()
    {
        if (_frame == 1)
            SetPhase(Phase.WaitLoad);
        if (Program.IsLoaded && Controlled() != null && Program.Editor == null)
        {
            Log($"The game is loaded. Controlled vehicle: {Controlled()!.Id}");
            SetPhase(Phase.Settle);
            return;
        }

        if (SecondsInPhase > LoadTimeoutSeconds)
            Fail($"the game did not load a controlled vehicle in {LoadTimeoutSeconds:F0} s");
    }

    private static bool _teleported;

    private static void Settle()
    {
        Vehicle? vehicle = Controlled();
        if (vehicle == null)
        {
            Fail("the controlled vehicle is gone while the test waits for the pad");
            return;
        }

        if (!vehicle.Situation.HasAnyContact())
        {
            if (FramesInPhase % 300 == 0)
                Log(Inv($"No ground contact yet. Situation {vehicle.Situation}, altitude {vehicle.GetBarometricAltitude():F0} m, speed {vehicle.GetSurfaceSpeed():F1} m/s"));
            if (!_teleported && SecondsInPhase > NoContactTeleportSeconds)
            {
                _teleported = true;
                TeleportToPad(vehicle);
            }

            if (SecondsInPhase > NoContactFailSeconds)
                Fail("the vehicle has no ground contact");
            return;
        }

        if (FramesInPhase < SettleFrames || vehicle.GetSurfaceSpeed() > 1.0 && SecondsInPhase < NoContactFailSeconds)
            return;
        Log(Inv($"The vehicle stands on the ground: {vehicle.Situation}, speed {vehicle.GetSurfaceSpeed():F2} m/s, sim speed {Universe.GetSimulationSpeed():0.###}"));
        SetPhase(Phase.StartA);
    }

    private static void TeleportToPad(Vehicle vehicle)
    {
        Celestial? home = Universe.CurrentSystem?.HomeBody as Celestial;
        LocationReference? pad = home?.GetLocation(PadId);
        if (home == null || pad == null)
        {
            Log($"The vehicle is not on the ground and the pad {PadId} was not found for a teleport.");
            return;
        }

        Log(Inv($"The vehicle is not on the ground after {NoContactTeleportSeconds:F0} s, so the test teleports it to {PadId}."));
        _report.Notes.Add($"The vehicle did not start on the pad, so the test teleported it to {PadId}.");
        vehicle.TeleportToLocation(home, pad.Latitude.ToDegrees(), pad.Longitude.ToDegrees());
    }

    #endregion

    #region Actions on the vehicle

    private static void SendInput(Vehicle vehicle, InputAction action, GlfwKeyAction keyAction)
    {
        InputEvents.VehicleInputBuffer.Add(new InputEvents.VehicleInputData
        {
            Vehicle = vehicle,
            Action = action,
            KeyAction = keyAction,
            Modifiers = default,
        });
    }

    // The game stages on the release of InputAction.CameraUp, the staging key.
    private static void Stage(Vehicle vehicle, string why)
    {
        SequenceList list = vehicle.Parts.SequenceList;
        Log($"Staging key ({why}). Active sequence {list.ActiveSequence}, next {list.GetNextSequenceNumber()}.");
        SendInput(vehicle, InputAction.CameraUp, GlfwKeyAction.Release);
    }

    private static bool _throttleHeld;

    private static void StartEngines(Vehicle vehicle)
    {
        Log(Inv($"Engine start. Manual throttle {vehicle.GetManualThrottle():F2}, active sequence {vehicle.Parts.SequenceList.ActiveSequence}."));
        SendInput(vehicle, InputAction.MainEngineStartup, GlfwKeyAction.Press);
        if (vehicle.GetManualThrottle() < 0.99f)
        {
            _throttleHeld = true;
            SendInput(vehicle, InputAction.MainEngineThrottleUp, GlfwKeyAction.Press);
        }
    }

    private static void ReleaseThrottle(Vehicle vehicle)
    {
        if (!_throttleHeld || vehicle.GetManualThrottle() < 0.99f)
            return;
        _throttleHeld = false;
        SendInput(vehicle, InputAction.MainEngineThrottleUp, GlfwKeyAction.Release);
        Log("The throttle is at full, so the throttle key is released.");
    }

    #endregion

    #region State of the view

    private static ViewState Snapshot()
    {
        IGameViewport viewport = Program.MainViewport;
        Camera camera = viewport.BaseCamera;
        return new ViewState(viewport.Mode.ToString(), camera.Following?.Id ?? "none",
            camera.GetFieldOfView() * (180.0 / Math.PI), Universe.GetSimulationSpeed(), Program.DrawUI);
    }

    #endregion

    #region Exit

    private static void FinishRun()
    {
        _report.Passed = _failure == null && _report.AllChecks().All(c => c.Pass);
        Log(_report.Passed ? "All checks passed." : "At least one check failed.");
        BeginExit();
    }

    private static void Exit()
    {
        WriteResult();
        _finished = true;
        int code = _failure != null || !_report.Passed ? 1 : 0;
        Log($"The test ends the game with exit code {code}.");
        Program.Instance?.RequestExit(code);
    }

    private static void WriteResult()
    {
        _report.Error = _failure;
        _report.Seconds = Math.Round(Now, 1);
        _report.GameVersion = VersionInfo.Current.VersionString;
        if (_failure != null)
            _report.Passed = false;
        try
        {
            File.WriteAllText(Path.Combine(_root, "result.json"), AutotestAnalysis.ToJson(_report));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"result.json could not be written: {ex.Message}");
        }
    }

    #endregion

    #region Watchdog

    private static long _lastFrameMs = -1;

    private static void MarkFrame() => Interlocked.Exchange(ref _lastFrameMs, _clock.ElapsedMilliseconds);

    // A background thread that only asks the game to end, and never stops a process. The game may
    // load for a long time before the first frame, so that wait is longer.
    private static void StartWatchdog()
    {
        var thread = new Thread(() =>
        {
            const double noFrameSeconds = 120.0;
            const double noFirstFrameSeconds = 600.0;
            const double totalSeconds = 1200.0;
            double nextAsk = 0.0;
            while (!_finished)
            {
                Thread.Sleep(1000);
                double now = _clock.Elapsed.TotalSeconds;
                long last = Interlocked.Read(ref _lastFrameMs);
                string? reason = null;
                if (_refused)
                    reason = "the self test cannot run on this game build";
                else if (now > totalSeconds)
                    reason = "the test ran for 20 minutes";
                else if (last < 0 && now > noFirstFrameSeconds)
                    reason = "no frame came in 10 minutes after the start";
                else if (last >= 0 && now - last / 1000.0 > noFrameSeconds)
                    reason = "no frame came for 120 s";
                if (reason == null || now < nextAsk)
                    continue;
                nextAsk = now + 30.0;
                int code = _refused ? 1 : 3;
                // A main thread that hangs in a step holds the gate, and the exit must still be asked for.
                bool locked = Monitor.TryEnter(Gate, 5000);
                try
                {
                    if (locked)
                    {
                        Log($"Watchdog: {reason}. The game is asked to end with exit code {code}.");
                        _failure ??= "watchdog: " + reason;
                        WriteResult();
                    }
                    else
                    {
                        DefaultCategory.Log.Warning($"[CineCam] Autotest watchdog: {reason}. The game is asked to end with exit code {code}, without result.json.");
                    }
                }
                finally
                {
                    if (locked)
                        Monitor.Exit(Gate);
                }

                Program.Instance?.RequestExit(code);
            }
        })
        {
            IsBackground = true,
            Name = "CineCam autotest watchdog",
        };
        thread.Start();
    }

    #endregion
}
