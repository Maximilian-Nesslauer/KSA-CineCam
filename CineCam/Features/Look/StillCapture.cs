using System.Diagnostics;
using System.Globalization;
using Brutal.Logging;
using CineCam.Core;
using CineCam.Director;
using CineCam.Features.Replay;
using CineCam.Features.Takes;
using KSA;

namespace CineCam.Features.Look;

// One picture of the current view at a multiple of the window size. ScreenshotCapture.Request with a
// scale above 1 and no "ui" flag rebuilds the renderer at the larger size with MSAA off
// (ScreenshotCapture.OnPrepareFrame), draws the warm-up frames so the temporal effects settle, saves
// the composite of the main view and rebuilds the renderer again. The game hides the interface for
// that time. The picture is moved from the screenshot folder of the game into the stills folder.
internal static class StillCapture
{
    // The warm-up of the game for a scale above 1 (ScreenshotCapture.DEFAULT_WARMUP_FRAMES).
    private const int WarmFrames = 30;

    // The game writes the picture on a worker thread after the capture ends, and a PNG of 8 times
    // the window size takes a while.
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(180);

    // A request that waits for another capture of the game gives up after this many frames.
    private const int MaxWaitFrames = 600;

    private sealed class Pending(ScreenshotCollector collector, string target, int scale)
    {
        public ScreenshotCollector Collector { get; } = collector;
        public string Target { get; } = target;
        public int Scale { get; } = scale;
        public Stopwatch SinceCaptureEnd { get; } = new();
    }

    private static int? _requested;
    private static int _waitFrames;
    private static Pending? _pending;

    /// <summary>The result of the last still, for the window and the console.</summary>
    public static string Status { get; private set; } = "";

    public static bool IsBusy => _requested != null || _pending != null;

    /// <summary>Why a still cannot be taken now, or null.</summary>
    public static string? BlockReason =>
        !OfflineHooks.Available ? "This game build does not support the capture of pictures, see the log at the start of the game."
        : !Program.IsLoaded ? "Load a game first."
        : Program.Editor != null ? "The game takes no pictures in the vehicle editor."
        : IsBusy ? "A still is being taken."
        : TakeRunner.Current is { Offline: not null } ? "An offline capture runs."
        : ReplaySession.IsRendering ? "A render runs."
        : null;

    /// <summary>Queues a still at the given scale, or at the scale of the settings. Returns the message for the player.</summary>
    public static string Request(int? scale)
    {
        if (BlockReason is { } block)
            return "No still: " + block;
        int wanted = Math.Clamp(scale ?? CineCamSettings.StillScale, CineCamSettings.MinStillScale, CineCamSettings.MaxStillScale);
        _requested = wanted;
        _waitFrames = 0;
        Status = string.Create(CultureInfo.InvariantCulture, $"Taking a still at {wanted} times the window size.");
        return Status;
    }

    // Runs in the prefix of Program.OnFrame, before ScreenshotCapture.OnPrepareFrame of the same frame.
    public static void OnFrame()
    {
        try
        {
            if (_pending != null)
                Poll(_pending);
            else if (_requested is int scale)
                Begin(scale);
        }
        catch (Exception ex)
        {
            _requested = null;
            _pending = null;
            Status = $"The still failed: {ex.Message}";
            LogHelper.ErrorOnce("still", $"[CineCam] Error in the still capture: {ex}");
        }
    }

    private static void Begin(int scale)
    {
        if (Program.Instance?.IsScreenshotCaptureActive != false)
        {
            if (++_waitFrames > MaxWaitFrames)
            {
                _requested = null;
                Finish("No still: the game is busy with another picture.");
            }

            return;
        }

        _requested = null;
        // The warm-up frames must show the same moment, so a playing replay pauses.
        if (ReplaySession.IsPlaying)
            ReplaySession.SetPlaying(false);

        string folder = CineCamPaths.StillsFolder;
        var collector = new ScreenshotCollector(CineCamPaths.ScreenshotsFolder, folder);
        string name = TakeFolders.Unique(OfflineOutput.StillName(DateTime.Now, scale), n => File.Exists(Path.Combine(folder, n)));
        OfflineHooks.RequestStill(scale, WarmFrames);
        _pending = new Pending(collector, Path.Combine(folder, name), scale);
        DefaultCategory.Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"[CineCam] Still at {scale} times the window size requested, with {WarmFrames} warm-up frames."));
    }

    private static void Poll(Pending pending)
    {
        if (pending.Collector.TryCollectAs(pending.Target) == CollectResult.Done)
        {
            _pending = null;
            Finish($"Still saved: {pending.Target}");
            return;
        }

        if (Program.Instance?.IsScreenshotCaptureActive == true)
            return;
        if (!pending.SinceCaptureEnd.IsRunning)
            pending.SinceCaptureEnd.Start();
        if (pending.SinceCaptureEnd.Elapsed > SaveTimeout)
        {
            _pending = null;
            Finish("No still: the game saved no picture, see the log of the game (a scale that does not fit into the video memory is lowered or refused).");
        }
    }

    private static void Finish(string text)
    {
        Status = text;
        DefaultCategory.Log.Info($"[CineCam] {text}");
    }

    public static void Reset()
    {
        _requested = null;
        _pending = null;
        _waitFrames = 0;
        Status = "";
    }
}
