using System.Runtime.CompilerServices;
using CineCam.Core;
using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Features.Look;
using CineCam.Features.Takes;
using HarmonyLib;
using KSA;
using GameProgram = KSA.Program;

namespace CineCam.Tests;

internal static class CaptureOwnershipTests
{
    public static void Run(Action<string, Action> check, Action<bool, string> expect)
    {
        check("frame statistics: a save sees the player setting on the first hidden frame", () =>
        {
            Reset();
            GameSettings settings = GameSettings.Current;
            try
            {
                OfflineHooks.SetDrawUi(false);
                expect(!settings.Interface.FrameStatistics, "the first hidden frame suppresses statistics");
                foreach (bool fail in new[] { false, true })
                {
                    settings.FailSave = fail;
                    Exception? error = null;
                    PictureGuard.SavePrefix(settings, out Action? resume);
                    try
                    {
                        expect(settings.Interface.FrameStatistics, "serialization sees the FPS setting of the player");
                        settings.Save();
                    }
                    catch (IOException ex) when (fail) { error = ex; }
                    finally
                    {
                        expect(PictureGuard.SaveFinalizer(error, resume) == error, "save errors propagate");
                    }
                    expect((error != null) == fail, "the save took the expected exit path");
                    expect(!settings.Interface.FrameStatistics, "suppression continues after the save");
                }

                // Program.DispatchKeyEvent toggles the field, including while the UI is hidden.
                settings.Interface.FrameStatistics = !settings.Interface.FrameStatistics;
                OfflineHooks.SetDrawUi(false);
                OfflineHooks.SetDrawUi(false);
                expect(settings.Interface.FrameStatistics, "the player's override survives later hidden frames");
                OfflineHooks.SetDrawUi(true);
                expect(settings.Interface.FrameStatistics, "the statistics remain as the player left them");
            }
            finally { Reset(); }
        });

        check("capture: an idle GPU and an unfinished native PNG keep the still's slot", () =>
        {
            Reset();
            CineCamPaths.Root = Path.Combine(Path.GetTempPath(), "cinecam-ownership-" + Guid.NewGuid().ToString("N"));
            string screenshot = Path.Combine(CineCamPaths.ScreenshotsFolder, "ksa_000.png");
            var harmony = new Harmony("cinecam.tests.png-task");
            using var fileWritten = new ManualResetEventSlim();
            using var finishWriter = new ManualResetEventSlim();
            Task? writer = null;
            try
            {
                harmony.Patch(AccessTools.DeclaredMethod(typeof(CaptureOwnershipTests), nameof(ScheduleNativeSave)),
                    transpiler: new HarmonyMethod(typeof(OfflineHooks), nameof(OfflineHooks.TrackPngWrite)));
                Directory.CreateDirectory(CineCamPaths.ScreenshotsFolder);
                StillCapture.Request(2);
                ExpectExclusive("queued");
                StillCapture.OnFrame();
                ScreenshotCapture native = GameProgram.Instance.Capture;
                native.Prepare();
                ExpectExclusive("GPU warm-up");
                byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x42];
                writer = ScheduleNativeSave(native.Abort, () =>
                {
                    File.WriteAllBytes(screenshot, png);
                    fileWritten.Set();
                    if (!finishWriter.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("The test never released the PNG writer.");
                });
                expect(fileWritten.Wait(TimeSpan.FromSeconds(5)), "the PNG file is present before the native task ends");
                expect(!GameProgram.Instance.IsScreenshotCaptureActive, "KSA reports idle while its PNG task runs");
                ExpectExclusive("PNG save");
                StillCapture.OnFrame();
                expect(File.Exists(screenshot), "collection waits for the whole PNG task");
                finishWriter.Set();
                expect(writer.Wait(TimeSpan.FromSeconds(5)), "the PNG task ends");
                ExpectExclusive("collection pending");
                StillCapture.OnFrame();
                string still = Directory.GetFiles(CineCamPaths.StillsFolder, "*.png").Single();
                expect(File.ReadAllBytes(still).SequenceEqual(png) && !File.Exists(screenshot), "only the still collects its picture");
                expect(OfflineHooks.TryAcquire(PictureOwner.ReplayRender), "a render can start after collection");
            }
            finally
            {
                finishWriter.Set();
                try { writer?.Wait(TimeSpan.FromSeconds(5)); }
                finally
                {
                    harmony.UnpatchAll(harmony.Id);
                    Reset();
                    if (Directory.Exists(CineCamPaths.Root))
                        Directory.Delete(CineCamPaths.Root, recursive: true);
                }
            }

            void ExpectExclusive(string phase)
            {
                expect(StillCapture.IsBusy && OfflineHooks.IsBusy, $"the still holds the slot during {phase}");
                expect(!OfflineHooks.RequestPrefix(), $"stock screenshots are refused during {phase}");
                foreach (PictureOwner owner in Enum.GetValues<PictureOwner>())
                    expect(!OfflineHooks.TryAcquire(owner), $"{owner} cannot start during {phase}");
            }
        });

        check("capture: cancellation retains exclusion while a late PNG is written", () =>
        {
            var slot = new PictureCaptureOwnership();
            var writer = new TaskCompletionSource();
            expect(!slot.TryAcquire(PictureOwner.Preview, nativeActive: true), "an armed stock screenshot blocks the preview");
            expect(slot.TryAcquire(PictureOwner.Preview, nativeActive: false), "the preview starts when the GPU is free");
            slot.TrackWrite(writer.Task);
            slot.Release(PictureOwner.Preview);
            bool busy = slot.IsBusy(nativeActive: false);
            expect(ControlRules.QuitBlock(new QuitState(false, busy, false, false, false, false))?.Code == "capture_busy",
                "local quit is refused after cancellation");
            foreach (PictureOwner owner in Enum.GetValues<PictureOwner>())
                expect(!slot.TryAcquire(owner, nativeActive: false), $"{owner} cannot collect the canceled preview");
            writer.SetResult();
            expect(slot.TryAcquire(PictureOwner.OfflineTake, nativeActive: false), "a new capture starts after the late write");
            expect(!slot.CanRequest(PictureOwner.ReplayRender, nativeActive: false), "only the owner can request a picture");
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ScheduleNativeSave(Action setIdle, Action save)
    {
        setIdle();
        return Task.Run(save);
    }

    private static void Reset()
    {
        StillCapture.Reset();
        OfflineHooks.Shutdown();
        FrameStats.Reset();
        PictureGuard.Reset();
        GameSettings.Current = new();
        GameProgram.Instance = new();
        GameProgram.DrawUI = true;
        OfflineHooks.Available = true;
    }
}
