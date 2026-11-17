using CineCam.Core;
using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Editing;
using CineCam.Features.Look;
using CineCam.Features.Takes;
using HarmonyLib;
using GameProgram = KSA.Program;
using KSA;

namespace CineCam.ControlTests;

internal static class ControlTests
{
    public static void Run(Action<string, Action> check, Action<bool, string> expect)
    {
        check("picture save: serialize player values and reapply only pinned fields after success or failure", () =>
        {
            foreach (bool fail in new[] { false, true })
            {
                PictureGuard.Reset();
                PictureGuard.ExposureAvailable = true;
                GameSettings.Current = new();
                GameSettings owner = GameSettings.Current;
                PictureGuard.Update(new TakePicture { Exposure = 3, CloudAmbientLight = false, CloudShadows = false });
                owner.Graphics.Reflections = false;
                owner.FailSave = fail;
                var harmony = new Harmony("cinecam.control-tests.save");
                harmony.Patch(AccessTools.Method(typeof(GameSettings), nameof(GameSettings.Save)),
                    prefix: new HarmonyMethod(typeof(PictureGuard), nameof(PictureGuard.SavePrefix)),
                    finalizer: new HarmonyMethod(typeof(PictureGuard), nameof(PictureGuard.SaveFinalizer)));
                bool failed = false;
                try
                {
                    owner.Save();
                }
                catch (IOException) when (fail) { failed = true; }
                finally { harmony.UnpatchAll(harmony.Id); }
                var written = System.Text.Json.JsonSerializer.Deserialize<GameSettings.GraphicsSettings>(owner.SavedJson,
                    new System.Text.Json.JsonSerializerOptions { IncludeFields = true })!;
                expect(written.TonemapExposure == 1 && written.CloudAmbientLight
                    && written.CloudShadows == GameSettings.CloudShadows.High, "serialization sees the player values");
                expect(!written.Reflections && !owner.Graphics.Reflections, "an unpinned player change stays");
                expect(failed == fail, "the original save exception is preserved");
                expect(owner.Graphics.TonemapExposure == 3 && !owner.Graphics.CloudAmbientLight
                    && owner.Graphics.CloudShadows == GameSettings.CloudShadows.Off, "the current take keeps its look");
                expect(PictureGuard.BeforeSave(new GameSettings()) == null, "a different settings instance is untouched");
                PictureGuard.Restore();
                expect(owner.Graphics.TonemapExposure == 1 && !owner.Graphics.Reflections, "teardown restores only pinned fields");
            }
        });

        check("picture save: a settings replacement during a save does not receive stale overrides", () =>
        {
            PictureGuard.Reset();
            GameSettings.Current = new();
            PictureGuard.Update(new TakePicture { Reflections = false });
            GameSettings before = GameSettings.Current;
            Action? resume = PictureGuard.BeforeSave(before);
            GameSettings.Current = new();
            resume?.Invoke();
            expect(before.Graphics.Reflections && GameSettings.Current.Graphics.Reflections, "neither instance is repinned");
            PictureGuard.Restore();
        });

        check("clean still: a take end or replay close during warm-up wins over stock restore", () =>
        {
            foreach (bool finishByUnload in new[] { false, true })
            {
                ResetCapture();
                OfflineHooks.SetDrawUi(false);
                OfflineHooks.RequestStill(2, 30);
                ScreenshotCapture capture = GameProgram.Instance.Capture;
                capture.Prepare();
                PostRender(capture);
                OfflineHooks.SetDrawUi(true);
                OfflineHooks.Reset();
                for (int frame = 0; frame < 29; frame++)
                    PostRender(capture);
                if (finishByUnload)
                    OfflineHooks.Shutdown();
                else
                {
                    capture.Ready();
                    PostRender(capture);
                }
                expect(GameProgram.DrawUI && !capture.IsActive, "the interface is restored without a later frame");
                expect(capture.Scale == 2 && capture.Flags == "warm=30", "the still keeps its requested scale and warm-up");
            }
        });

        check("clean still: a new interface owner and stock abort keep the latest requested value", () =>
        {
            ResetCapture();
            OfflineHooks.RequestStill(2, 30);
            ScreenshotCapture capture = GameProgram.Instance.Capture;
            capture.Prepare();
            expect(!GameProgram.DrawUI && OfflineHooks.DrawUi, "a new take saves the interface value from before the still");
            OfflineHooks.SetDrawUi(true);
            OfflineHooks.SetDrawUi(false);
            capture.Abort();
            OfflineHooks.AfterGrab();
            expect(!GameProgram.DrawUI, "the new hidden take wins");
            OfflineHooks.Shutdown();
        });

        check("clean still: the Harmony finalizer restores the interface in the capture frame", () =>
        {
            ResetCapture();
            var harmony = new Harmony("cinecam.control-tests.capture");
            harmony.Patch(AccessTools.Method(typeof(ScreenshotCapture), nameof(ScreenshotCapture.OnPostRender)),
                prefix: new HarmonyMethod(typeof(OfflineHooks), nameof(OfflineHooks.PostRenderPrefix)),
                finalizer: new HarmonyMethod(typeof(OfflineHooks), nameof(OfflineHooks.PostRenderFinalizer)));
            try
            {
                OfflineHooks.SetDrawUi(false);
                OfflineHooks.RequestStill(2, 30);
                ScreenshotCapture capture = GameProgram.Instance.Capture;
                capture.Prepare();
                capture.OnPostRender();
                OfflineHooks.SetDrawUi(true);
                capture.Ready();
                capture.OnPostRender();
                expect(GameProgram.DrawUI, "the finalizer overrides the stale stock value without a later tick");
            }
            finally { harmony.UnpatchAll(harmony.Id); }
        });

        check("interface: frame statistics are suppressed on the first hidden frame", () =>
        {
            ResetCapture();
            expect(GameSettings.Current.Interface.FrameStatistics, "statistics start visible");
            OfflineHooks.SetDrawUi(false);
            expect(!GameSettings.Current.Interface.FrameStatistics, "statistics are off before DrawFps");
            OfflineHooks.SetDrawUi(true);
            expect(GameSettings.Current.Interface.FrameStatistics, "statistics return with the interface");
        });

        check("still quit: requested, capturing and writing all refuse quit", () =>
        {
            ResetCapture();
            string root = Path.Combine(Path.GetTempPath(), "cinecam-still-" + Guid.NewGuid().ToString("N"));
            CineCamPaths.Root = root;
            try
            {
                StillCapture.Request(2);
                Refuses("requested");
                StillCapture.OnFrame();
                GameProgram.Instance.Capture.Prepare();
                Refuses("capturing");
                GameProgram.Instance.Capture.Abort();
                StillCapture.OnFrame();
                Refuses("writing");
                StillCapture.Reset();
                expect(!StillCapture.IsBusy, "a finished still clears the busy state");
            }
            finally
            {
                StillCapture.Reset();
                OfflineHooks.Shutdown();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            void Refuses(string phase) => expect(ControlRules.QuitBlock(new QuitState(false, StillCapture.IsBusy,
                false, false, false, false))?.Code == "capture_busy", phase);
        });

        check("destroyed take: terminal explosions resolve cues once without a world sample", () =>
        {
            ShotFile file = ShotFileJson.Parse("""
                { "camera": [{ "type": "tripod", "position": [1,2,3], "lookAt": [0,0,0], "duration": 60 }],
                  "actions": [ { "action": "cue", "name": "destroyed", "at": { "event": "explosion" } },
                               { "action": "stage", "at": { "event": "explosion" } },
                               { "action": "cue", "name": "later", "at": { "event": "explosion", "delay": 1 } } ] }
                """).File;
            var director = new TakeDirector(file.DefaultTake, file);
            DirectorFrame terminal = director.Destroyed(5, ["impact"]);
            expect(terminal.NewEvents.Count == 1 && terminal.NewEvents[0].Name == "explosion"
                && terminal.NewEvents[0].TakeTime == 5, "the event is available for delivery before the end");
            expect(terminal.StartedActions.SequenceEqual(new[] { 0, 1 }), "only actions due at the end are resolved");
            expect(terminal.Pose == null && terminal.StartedShots.Count == 0, "no camera or world update is requested");
            expect(director.Destroyed(5, []).StartedActions.Count == 0, "no duplicate cue");
        });

        check("timeline: late measurements survive more than twenty early resets", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "cinecam-runs-" + Guid.NewGuid().ToString("N"));
            try
            {
                for (int i = 0; i < 26; i++)
                {
                    string folder = Path.Combine(root, $"20261117-120000-launch~{i + 1}");
                    Directory.CreateDirectory(folder);
                    using var log = EventLogCsv.Create(Path.Combine(folder, "events.csv"));
                    log.Write(1, 1, 0, 1, -1, "event", "ignition");
                    if (i == 0)
                    {
                        log.Write(2, 2, 1, 1, -1, "event", "liftoff");
                        log.Write(90, 90, 89, 1, -1, "event", "apoapsis");
                    }
                    log.Write(i == 0 ? 100 : 3, 100, 99, 1, -1, "take", "end");
                }
                var runs = TimelineLayout.ReadFinishedRuns(root, "launch").ToList();
                Dictionary<string, double> times = TimelineLayout.MergeRuns(runs.Select(r => r.Run));
                expect(runs.Count == 26 && times["apoapsis"] == 90, "the old late event remains measured");
                expect(!times.ContainsKey("liftoff"), "a stale event before a newer end stays out");
                expect(!TimelineLayout.ReadFinishedRuns(root, "other").Any(), "other takes stay separate");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }

    private static void ResetCapture()
    {
        OfflineHooks.Shutdown();
        StillCapture.Reset();
        FrameStats.Reset();
        GameSettings.Current = new();
        GameProgram.Instance = new();
        GameProgram.DrawUI = true;
        OfflineHooks.Available = true;
    }

    private static void PostRender(ScreenshotCapture capture)
    {
        OfflineHooks.PostRenderPrefix(capture);
        capture.OnPostRender();
        OfflineHooks.PostRenderFinalizer(null);
    }
}
