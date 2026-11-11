using System.Globalization;
using Brutal.Logging;
using CineCam.Core;
using CineCam.Director;
using KSA;

namespace CineCam.Features.Takes;

// In the offline mode the game steps by 1 / fps for each picture, so the video speed does not
// depend on the render speed. A picture is requested in the prefix of Program.OnFrame, at the start
// of the frame that it shows, and collected in the prefix of the next frame.
internal static partial class TakeRunner
{
    private const int MaxFailuresInRow = 3;

    // The most frames that a picture may wait for the end of a frame that returned early.
    private const int MaxCarryFrames = 120;

    private const int ProgressEvery = 300;

    private static readonly TimeSpan CollectTimeout = TimeSpan.FromSeconds(30);

    private static OfflineCapture? BeginOffline(ShotFile file, Take take, string folder)
    {
        if (!OfflineHooks.Available)
        {
            DefaultCategory.Log.Error("[CineCam] The offline capture is not available, because a target in the game was not found. See the log at the start of the game.");
            return null;
        }

        if (file.Capture.Scale != 1)
            DefaultCategory.Log.Warning("[CineCam] The capture scale is ignored. The pictures have the size of the game window.");

        double fps = file.CaptureFps;
        string outputFolder = OfflineOutput.ResolveFolder(folder, file.Capture.Folder);
        string framesFolder = Path.Combine(outputFolder, OfflineOutput.FramesFolderName);
        try
        {
            if (Directory.Exists(framesFolder) && Directory.EnumerateFiles(framesFolder, "*.png").Any())
            {
                DefaultCategory.Log.Error($"[CineCam] The folder {framesFolder} has pictures already. Choose another output folder.");
                return null;
            }

            var collector = new ScreenshotCollector(CineCamPaths.ScreenshotsFolder, framesFolder);
            OfflineOutput.WriteHelpers(outputFolder, folder, take.Name, fps);
            ReportDiskSpace(outputFolder);
            if (1.0 / fps > 1.0 / GameSettings.Current.Simulation.MinTargetFrameRate)
                DefaultCategory.Log.Warning($"[CineCam] The picture rate {fps} is below the minimum frame rate of the game ({GameSettings.Current.Simulation.MinTargetFrameRate}), so the sim step of one picture is longer than the game normally allows.");
            return new OfflineCapture(fps, outputFolder, framesFolder, collector);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DefaultCategory.Log.Error($"[CineCam] The output folder {outputFolder} could not be prepared: {ex.Message}");
            return null;
        }
    }

    private static void ReportDiskSpace(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (root != null)
                DefaultCategory.Log.Info($"[CineCam] Free space for the pictures: {new DriveInfo(root).AvailableFreeSpace / (1024 * 1024 * 1024)} GB on {root}");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // The free space is only information.
        }
    }

    // Runs in the prefix of Program.OnFrame, after the requests of the player. Harmony binds the
    // parameters by name, so they must match those of Program.OnFrame.
    public static void OnOfflineFrame(ref double currentPlayerTime, ref double dtPlayer)
    {
        ActiveTake? take = _take;
        OfflineCapture? offline = take?.Offline;
        if (take == null || offline == null)
            return;

        try
        {
            if (offline.Pending && !offline.Rendered && ++offline.CarryFrames <= MaxCarryFrames)
            {
                // The last frame ended early, so its armed grab saves the picture in this frame,
                // which must not ask for a second picture.
                Advance(offline, ref currentPlayerTime, ref dtPlayer, OfflineCapture.RetryStepSeconds);
                return;
            }

            offline.CarryFrames = 0;
            if (offline.Pending && !CollectPending(take, offline))
                return;

            if (offline.EndAfterPicture != null)
            {
                End(offline.EndAfterPicture);
                return;
            }

            double step = offline.NextStep;
            offline.PostRenderMark = OfflineHooks.PostRenderCount;
            if (!OfflineHooks.RequestPicture())
            {
                // Another capture still runs, so this frame makes no picture and takes only a small step.
                Advance(offline, ref currentPlayerTime, ref dtPlayer, OfflineCapture.RetryStepSeconds);
                offline.Pending = false;
                return;
            }

            Advance(offline, ref currentPlayerTime, ref dtPlayer, step);
            offline.Pending = true;
            offline.Rendered = false;
        }
        catch (Exception ex)
        {
            Fail("the offline capture", ex);
        }
    }

    private static void Advance(OfflineCapture offline, ref double currentPlayerTime, ref double dtPlayer, double step)
    {
        currentPlayerTime = offline.Advance(step, currentPlayerTime);
        dtPlayer = step;
    }

    // Returns false when the take ended because too many pictures in a row failed.
    private static bool CollectPending(ActiveTake take, OfflineCapture offline)
    {
        int number = offline.FrameNumber;
        CollectResult result = offline.Collector.Wait(number, CollectTimeout, () => NoPictureComing(offline));
        offline.Pending = false;
        if (result == CollectResult.Done)
        {
            Saved(offline, number);
            return true;
        }

        bool giveUp = offline.RecordLost(MaxFailuresInRow);
        string reason = OfflineHooks.Result == PostRenderResult.NoPicture ? "the game made no picture of the frame" : "no finished file came in time";
        string text = $"Picture {number} was not saved ({reason}). The next frame makes it again.";
        DefaultCategory.Log.Warning($"[CineCam] {text}");
        WriteRow(take, "warning", "frameGrab", text);
        if (giveUp)
        {
            End($"the picture grab failed {offline.FailuresInRow} times in a row, see the log");
            return false;
        }

        return true;
    }

    // The end of the frame is known and the game did not record a copy of the swapchain image in it.
    private static bool NoPictureComing(OfflineCapture offline) =>
        OfflineHooks.Detects && offline.Rendered && OfflineHooks.Result != PostRenderResult.Saving;

    private static void Saved(OfflineCapture offline, int number)
    {
        offline.RecordSaved(number);
        try
        {
            offline.BytesWritten += new FileInfo(Path.Combine(offline.FramesFolder, OfflineOutput.FrameName(number))).Length;
        }
        catch (IOException)
        {
            // The size is only information.
        }

        if (number % ProgressEvery == 0)
            DefaultCategory.Log.Info(string.Create(CultureInfo.InvariantCulture,
                $"[CineCam] Offline capture: {number} pictures, {offline.BytesWritten / (1024.0 * 1024.0):F0} MB, {number / offline.Fps:F1} s of video."));
    }

    // When the vehicle solver is too slow for the step of a picture, the game simulates less time
    // than the picture rate says and the video runs slow.
    private static void CheckSimStep(ActiveTake take, OfflineCapture offline)
    {
        if (offline.WarnedSlowSim)
            return;
        double fraction = Universe.GetAchivedSpeedFraction();
        if (fraction >= 0.99)
            return;
        offline.WarnedSlowSim = true;
        string text = string.Create(CultureInfo.InvariantCulture,
            $"The game reached only {fraction:P0} of the commanded sim speed, because the vehicle solver is slow. The video may run slower than the speed track says.");
        DefaultCategory.Log.Warning($"[CineCam] {text}");
        WriteRow(take, "warning", "simStep", text);
    }

    // A picture of a frame that finished is still collected. One of a frame that did not finish
    // cannot come here, and the game saves it as a normal screenshot.
    private static void FinishOffline(ActiveTake take, string reason)
    {
        OfflineCapture? offline = take.Offline;
        if (offline == null)
            return;

        if (offline.Pending && offline.Rendered)
        {
            int number = offline.FrameNumber;
            if (offline.Collector.Wait(number, CollectTimeout, () => NoPictureComing(offline)) == CollectResult.Done)
                Saved(offline, number);
            else
                DefaultCategory.Log.Warning($"[CineCam] Picture {number} was not saved.");
        }

        offline.Pending = false;
        OfflineHooks.Reset();
        DefaultCategory.Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"[CineCam] Offline capture ended ({reason}): {offline.Count} pictures ({offline.Count / offline.Fps:F1} s of video, {offline.BytesWritten / (1024.0 * 1024.0):F0} MB), {offline.PicturesLost} lost and made again. Folder: {offline.OutputFolder}"));
    }
}
