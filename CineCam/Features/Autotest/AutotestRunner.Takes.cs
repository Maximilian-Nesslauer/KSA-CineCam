using System.Diagnostics;
using System.Globalization;
using CineCam.Core;
using CineCam.Director;
using CineCam.Features.Takes;
using KSA;

namespace CineCam.Features.Autotest;

internal static partial class AutotestRunner
{
    private static double _height = 20.0;
    private static string _enginePart = "";
    private static ViewState _before = new("", "", 0, 0, false);
    private static string _takeFolder = "";
    private static string _takeName = "";
    private static bool _sawTake;
    private static readonly List<AutotestSample> _samples = new();
    private static DirectorFrame? _lastLogged;
    private static TakeReport _current = new();

    private static bool _firesFirst;
    private static string _holdEvent = "sequence:2";
    private static List<string> _expectedEvents = new();
    private static bool _ignited;
    private static bool _staged;
    private static bool _limitAsked;

    #region Take A

    private static void StartTake(string name, Phase next)
    {
        Vehicle? vehicle = Controlled();
        if (vehicle == null)
        {
            Fail("the controlled vehicle is gone before a take");
            return;
        }

        if (name == AutotestPlan.TakeAName)
            PrepareA(vehicle);
        else
            PrepareBFiles(vehicle);

        _before = Snapshot();
        _samples.Clear();
        _lastLogged = null;
        _sawTake = false;
        _takeName = name;
        _current = name == AutotestPlan.TakeAName ? _report.TakeA : _report.TakeB;
        _current.Name = name;
        _current.Before = _before;
        Log(Inv($"State before {name}: mode {_before.Mode}, follow {_before.Following}, fov {_before.FovDegrees:F2}, speed {_before.SimSpeed:0.###}, DrawUI {_before.DrawUi}"));
        TakeRunner.RequestStart(name);
        SetPhase(next);
    }

    private static void PrepareA(Vehicle vehicle)
    {
        var reader = new VehicleReader(vehicle);
        reader.RefreshExtent(0.0);
        _height = reader.Height;
        _enginePart = LowestEnginePart(vehicle);
        DescribeVehicle(vehicle);
        string folder = Path.Combine(_root, "takeA");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "shots.json");
        File.WriteAllText(path, AutotestPlan.TakeAJson(_height, _enginePart));
        ShotFileStore.PathOverride = path;
        string raw = Path.Combine(folder, "raw");
        _collector = new ScreenshotCollector(CineCamPaths.ScreenshotsFolder, raw);
        _nextShot = 0;
    }

    private static string LowestEnginePart(Vehicle vehicle)
    {
        string id = "";
        double lowest = double.MaxValue;
        Span<EngineController> engines = vehicle.Parts.Modules.Get<EngineController>();
        for (int i = 0; i < engines.Length; i++)
        {
            double x = engines[i].Parent.PositionVehicleAsmb.X;
            if (x < lowest)
            {
                lowest = x;
                id = engines[i].Parent.Id;
            }
        }

        return id;
    }

    private static void DescribeVehicle(Vehicle vehicle)
    {
        SequenceList list = vehicle.Parts.SequenceList;
        var sequences = new List<string>();
        foreach (Sequence sequence in list.Sequences)
            sequences.Add(string.Create(CultureInfo.InvariantCulture, $"{sequence.Number}{(sequence.Activated ? " (activated)" : "")}: {sequence.Parts.Length} parts"));
        int engines = vehicle.Parts.Modules.Get<EngineController>().Length;
        string text = string.Create(CultureInfo.InvariantCulture,
            $"Vehicle {vehicle.Id}: {vehicle.Parts.Count} parts, height {_height:F1} m, {engines} engines, lowest engine part \"{_enginePart}\", active sequence {list.ActiveSequence}, next {list.GetNextSequenceNumber()}, manual throttle {vehicle.GetManualThrottle():F2}, situation {vehicle.Situation}. Sequences: {string.Join("; ", sequences)}");
        Log(text);
        _report.Vehicle = text;
    }

    private static ActiveTake? Observe()
    {
        ActiveTake? take = TakeRunner.Current;
        if (take == null)
            return null;
        if (!_sawTake)
        {
            _sawTake = true;
            _takeFolder = take.Folder;
            Log($"The take {take.Take.Name} runs. Folder: {take.Folder}");
        }

        DirectorFrame? frame = take.LastFrame;
        if (frame == null)
            return take;
        _samples.Add(new AutotestSample(take.Seconds, Universe.GetElapsedSeconds(), Universe.GetSimulationSpeed(), frame.TargetSpeed, frame.SpeedActive));
        if (!ReferenceEquals(frame, _lastLogged))
        {
            _lastLogged = frame;
            foreach (DetectedEvent seen in frame.NewEvents)
                Log(string.Create(CultureInfo.InvariantCulture, $"Event {seen.Name} at take time {seen.TakeTime:F2} s {seen.Detail}"));
            foreach (int index in frame.StartedShots)
                Log(string.Create(CultureInfo.InvariantCulture, $"Shot camera[{index}] ({take.Take.Shots[index].Type}) starts at {take.Seconds:F2} s"));
        }

        return take;
    }

    private static void RunA()
    {
        ActiveTake? take = Observe();
        if (take == null)
        {
            if (_sawTake)
            {
                Log("Take A ended.");
                SetPhase(Phase.EndA);
            }
            else if (FramesInPhase > 120 && SecondsInPhase > 10)
                Fail("take A did not start, see the game log");
            return;
        }

        GrabShots(take);
    }

    private static void FinishA()
    {
        TakeReport report = _report.TakeA;
        IReadOnlyList<CsvRow> rows = CollectCsv(report, "takeA");
        string reason = AutotestAnalysis.EndReason(rows);
        report.Checks.Add(new AutotestCheck("take ended normally", reason == "the last shot is done", $"end reason: {reason}"));
        var expected = new List<double?>();
        for (int i = 0; i < AutotestPlan.ShotNames.Length; i++)
            expected.Add(i * AutotestPlan.ShotSeconds);
        report.Checks.Add(AutotestAnalysis.ShotsStarted(rows, expected, 0.6));
        int keys = rows.Count(r => r.Kind == "speed");
        report.Checks.Add(new AutotestCheck("speed keys started", keys == 2, $"{keys} of 2 speed keys started"));
        report.Checks.Add(AutotestAnalysis.SimSpeedFollowsTarget(_samples, rows));
        report.Checks.Add(ScreenshotCheck());
        report.Checks.Add(AutotestAnalysis.Restored(_before, Snapshot()));
        report.Samples = _samples.Count;
        LogChecks(report);
    }

    private static void LogChecks(TakeReport report)
    {
        foreach (AutotestCheck check in report.Checks)
            Log($"Check {report.Name} / {check.Name}: {(check.Pass ? "pass" : "FAIL")}. {check.Reason}");
    }

    private static IReadOnlyList<CsvRow> CollectCsv(TakeReport report, string sub)
    {
        report.Folder = _takeFolder;
        string target = Path.Combine(_root, sub);
        Directory.CreateDirectory(target);
        string csv = Path.Combine(_takeFolder, "events.csv");
        if (_takeFolder.Length == 0 || !File.Exists(csv))
        {
            report.Checks.Add(new AutotestCheck("event log", false, $"no events.csv in \"{_takeFolder}\""));
            return Array.Empty<CsvRow>();
        }

        File.Copy(csv, Path.Combine(target, "events.csv"), overwrite: true);
        List<CsvRow> rows = AutotestAnalysis.ParseCsv(File.ReadAllText(csv));
        report.EndReason = AutotestAnalysis.EndReason(rows);
        report.TakeSeconds = rows.LastOrDefault(r => r.Kind == "take" && r.Name == "end")?.RealSeconds ?? 0.0;
        report.Events = rows.Where(r => r.Kind == "event").Select(r => new RowReport(r.Name, r.RealSeconds, r.GameSeconds, r.Detail)).ToList();
        report.Shots = rows.Where(r => r.Kind == "shot").Select(r => new RowReport(r.Name, r.RealSeconds, r.GameSeconds, r.Detail)).ToList();
        report.SpeedKeys = rows.Where(r => r.Kind == "speed").Select(r => new RowReport(r.Name, r.RealSeconds, r.GameSeconds, r.Detail)).ToList();
        report.Warnings = rows.Where(r => r.Kind is "warning").Select(r => r.Detail).ToList();
        report.Checks.Add(new AutotestCheck("event log", true, $"{rows.Count} rows, {report.Warnings.Count} warnings"));
        foreach (string warning in report.Warnings)
            Log($"Warning row in the log of {report.Name}: {warning}");
        return rows;
    }

    #endregion

    #region Screenshots of take A

    private static ScreenshotCollector? _collector;
    private static Action<int, string>? _screenshot;
    private static int _nextShot;
    private static string? _grabName;
    private static int _grabNumber;
    private static long _grabFrame;
    private static readonly Stopwatch _grabWatch = new();

    private static bool GrabIdle() => _grabName == null;

    // The request runs at the end of a frame, so the picture shows the next frame.
    private static void GrabShots(ActiveTake take)
    {
        if (_collector == null)
            return;
        if (_grabName != null)
        {
            if (_frame - _grabFrame < 2)
                return;
            if (_collector.TryCollect(_grabNumber, out string? moved) == CollectResult.Done && moved != null)
            {
                string dest = Path.Combine(_root, "takeA", _grabName + ".png");
                File.Move(moved, dest, overwrite: true);
                long size = new FileInfo(dest).Length;
                _report.TakeA.Screenshots.Add(new ShotReport(_grabName, dest, size, true));
                Log($"Screenshot {_grabName} saved ({size} bytes).");
                _grabName = null;
            }
            else if (_grabWatch.Elapsed.TotalSeconds > 15.0)
            {
                _report.TakeA.Screenshots.Add(new ShotReport(_grabName, "", 0, false));
                Log($"Screenshot {_grabName} did not come in 15 s.");
                _grabName = null;
            }

            return;
        }

        if (_nextShot >= AutotestPlan.ShotNames.Length || take.Seconds < AutotestPlan.MiddleOfShot(_nextShot))
            return;
        if (Program.Instance?.IsScreenshotCaptureActive == true)
            return;
        if (GameReflection.Program_Screenshot == null)
        {
            _report.TakeA.Screenshots.Add(new ShotReport("no Program.Screenshot", "", 0, false));
            _nextShot = int.MaxValue;
            return;
        }

        _screenshot ??= GameReflection.Program_Screenshot.CreateDelegate<Action<int, string>>();
        _grabName = string.Create(CultureInfo.InvariantCulture, $"{_nextShot + 1:00}-{AutotestPlan.ShotNames[_nextShot]}");
        _grabNumber = _nextShot + 1;
        _grabFrame = _frame;
        _grabWatch.Restart();
        _nextShot++;
        _screenshot(1, "");
        Log(Inv($"Screenshot {_grabName} requested at take time {take.Seconds:F2} s."));
    }

    private static AutotestCheck ScreenshotCheck()
    {
        List<ShotReport> shots = _report.TakeA.Screenshots;
        int good = shots.Count(s => s.Saved && s.Bytes > 5000);
        bool pass = good == AutotestPlan.ShotNames.Length;
        return new AutotestCheck("screenshots", pass,
            $"{good} of {AutotestPlan.ShotNames.Length} pictures saved: " + string.Join(", ", shots.Select(s => $"{s.Name} {s.Bytes} bytes")));
    }

    #endregion

    #region Take B

    // Reads from the sequence list which sequence numbers the ignition step and the later staging
    // step fire, because the last shot waits for the second one.
    private static void PrepareBFiles(Vehicle vehicle)
    {
        SequenceList list = vehicle.Parts.SequenceList;
        int active = list.ActiveSequence;
        int next = list.GetNextSequenceNumber();
        _firesFirst = active == 0 && next > 0;
        int afterFirst = _firesFirst ? next : active;
        int second = -1;
        bool skippedFirst = !_firesFirst;
        foreach (Sequence sequence in list.Sequences)
        {
            if (sequence.Activated || sequence.Parts.IsEmpty)
                continue;
            if (!skippedFirst)
            {
                skippedFirst = true;
                continue;
            }

            second = sequence.Number;
            break;
        }

        _expectedEvents = new List<string> { "ignition", "liftoff" };
        if (_firesFirst)
            for (int n = 1; n <= next; n++)
                _expectedEvents.Add("sequence:" + n.ToString(CultureInfo.InvariantCulture));
        _holdEvent = "sequence:" + (afterFirst + 1).ToString(CultureInfo.InvariantCulture);
        if (second > afterFirst)
            for (int n = afterFirst + 1; n <= second; n++)
                _expectedEvents.Add("sequence:" + n.ToString(CultureInfo.InvariantCulture));
        else
            _report.TakeB.Notes.Add("The sequence list has no sequence after the active one, so the second staging step changes nothing.");
        Log($"Plan for take B: first staging step {(_firesFirst ? "yes" : "no (a sequence is active already)")}, active sequence {active}, next {next}, hold shot on {_holdEvent}, expected events {string.Join(", ", _expectedEvents)}.");
        string folder = Path.Combine(_root, "takeB");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "shots.json");
        File.WriteAllText(path, AutotestPlan.TakeBJson(_height, _holdEvent, folder));
        ShotFileStore.PathOverride = path;
        _ignited = false;
        _staged = false;
        _limitAsked = false;
    }

    private static void PrepareB()
    {
        StartTake(AutotestPlan.TakeBName, Phase.RunB);
    }

    private static void RunB()
    {
        ActiveTake? take = Observe();
        Vehicle? vehicle = Controlled();
        if (take == null)
        {
            if (_sawTake)
            {
                Log("Take B ended.");
                SetPhase(Phase.EndB);
            }
            else if (FramesInPhase > 120 && SecondsInPhase > 10)
                Fail("take B did not start, see the game log");
            return;
        }

        if (vehicle == null)
            return;
        double t = take.Seconds;
        ReleaseThrottle(vehicle);
        if (!_ignited && t >= AutotestPlan.IgnitionAt)
        {
            _ignited = true;
            if (_firesFirst)
                Stage(vehicle, "first sequence");
            StartEngines(vehicle);
        }

        if (!_staged && t >= AutotestPlan.NextSequenceAt)
        {
            _staged = true;
            Stage(vehicle, "next sequence");
        }

        if (!_limitAsked && t >= AutotestPlan.OfflineLimitSeconds)
        {
            _limitAsked = true;
            Log("The take reached the time limit of the test, so it is stopped.");
            TakeRunner.RequestStop();
        }

        if (_frame % 150 == 0)
            Log(string.Create(CultureInfo.InvariantCulture,
                $"Take B at {t:F1} s: altitude {vehicle.GetBarometricAltitude():F1} m, speed {vehicle.GetSurfaceSpeed():F1} m/s, sequence {vehicle.Parts.SequenceList.ActiveSequence}, sim speed {Universe.GetSimulationSpeed():0.###}, contact {vehicle.Situation.HasAnyContact()}"));
    }

    private static void FinishB()
    {
        TakeReport report = _report.TakeB;
        IReadOnlyList<CsvRow> rows = CollectCsv(report, "takeB");
        string reason = AutotestAnalysis.EndReason(rows);
        report.Checks.Add(new AutotestCheck("take ended normally", reason == "the last shot is done", $"end reason: {reason}"));
        report.Checks.Add(AutotestAnalysis.EventsSeen(rows, _expectedEvents));
        double? ignition = AutotestAnalysis.EventTime(rows, "ignition");
        double? liftoff = AutotestAnalysis.EventTime(rows, "liftoff");
        double? hold = AutotestAnalysis.EventTime(rows, _holdEvent);
        var expected = new List<double?>
        {
            0.0,
            ignition,
            liftoff + AutotestPlan.ChaseDelay,
            liftoff + AutotestPlan.TrackDelay,
            hold,
        };
        report.Checks.Add(AutotestAnalysis.ShotsStarted(rows, expected, 0.25));
        report.Checks.Add(AutotestAnalysis.SimSpeedFollowsTarget(_samples, rows));
        report.Checks.Add(AutotestAnalysis.FrameSteps(_samples, AutotestPlan.OfflineFps));

        string frames = Path.Combine(_root, "takeB", OfflineOutput.FramesFolderName);
        int files = Directory.Exists(frames) ? Directory.EnumerateFiles(frames, "*.png").Count() : 0;
        long lastFrame = rows.LastOrDefault(r => r.Kind == "take" && r.Name == "end")?.Frame ?? -1;
        report.Checks.Add(AutotestAnalysis.FrameCount(files, lastFrame, report.TakeSeconds, AutotestPlan.OfflineFps, 150));
        report.FrameFiles = files;
        report.Checks.Add(AutotestAnalysis.Restored(_before, Snapshot()));
        report.Samples = _samples.Count;
        LogChecks(report);
    }

    #endregion
}

internal sealed record RowReport(string Name, double TakeSeconds, double GameSeconds, string Detail);

internal sealed record ShotReport(string Name, string Path, long Bytes, bool Saved);

internal sealed class TakeReport
{
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public string EndReason { get; set; } = "";
    public double TakeSeconds { get; set; }
    public int Samples { get; set; }
    public int FrameFiles { get; set; }
    public ViewState? Before { get; set; }
    public List<RowReport> Events { get; set; } = new();
    public List<RowReport> Shots { get; set; } = new();
    public List<RowReport> SpeedKeys { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<ShotReport> Screenshots { get; } = new();
    public List<string> Notes { get; } = new();
    public List<AutotestCheck> Checks { get; } = new();
}

internal sealed class Report
{
    public bool Passed { get; set; }
    public string? Error { get; set; }
    public string GameVersion { get; set; } = "";
    public double Seconds { get; set; }
    public string Vehicle { get; set; } = "";
    public List<string> Notes { get; } = new();
    public TakeReport TakeA { get; } = new();
    public TakeReport TakeB { get; } = new();

    public IEnumerable<AutotestCheck> AllChecks() => TakeA.Checks.Concat(TakeB.Checks);
}
