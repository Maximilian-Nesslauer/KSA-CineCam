using System.Reflection;
using Brutal.Logging;
using CineCam.Core;
using CineCam.Director;
using CineCam.Features.Look;
using KSA;

namespace CineCam.Features.Takes;

internal enum PostRenderResult
{
    /// <summary>The game has not reached the end of the frame yet.</summary>
    Unknown,

    /// <summary>The frame has a picture that the game is going to save.</summary>
    Saving,

    /// <summary>The game refused the request or aborted the grab.</summary>
    NoPicture,
}

internal static class OfflineHooks
{
    private static Action<int, string>? _screenshot;
    private static bool _expecting;
    private static ScreenshotCapture? _cleanCapture;
    private static bool? _drawUiAfterGrab;

    public static bool Available { get; set; }

    /// <summary>True when the prefix of ScreenshotCapture.OnPostRender is in place, so a failed grab is known at once.</summary>
    public static bool Detects { get; set; }

    public static PostRenderResult Result { get; private set; }

    /// <summary>Counts the calls of ScreenshotCapture.OnPostRender. A frame that returns early never calls it.</summary>
    public static long PostRenderCount { get; private set; }

    public static bool DrawUi => _cleanCapture?.IsActive == true && _drawUiAfterGrab is bool shown
        ? shown : Program.DrawUI;

    // The flag "ui" is the swapchain grab, which copies the image of this frame after the interface
    // is drawn, and scale 1 keeps the window size. Without the flag, ScreenshotCapture.Request at scale
    // 1 takes the CaptureFrame path: ScreenshotCapture.OnRenderGameCapture draws the scene of the main
    // view through the composite pass into its own 8 bit image, without the interface, the overlays and
    // the sync flash, and without a renderer rebuild. Returns false while another capture runs, because
    // the game refuses a second request then.
    public static bool RequestPicture(CaptureGrab grab)
    {
        _screenshot ??= GameReflection.Program_Screenshot!.CreateDelegate<Action<int, string>>();
        WaitForIdleCapture();
        if (Program.Instance?.IsScreenshotCaptureActive == true)
            return false;
        AfterGrab();
        Result = PostRenderResult.Unknown;
        _expecting = true;
        _cleanCapture = grab == CaptureGrab.Clean ? Capture() : null;
        _drawUiAfterGrab = grab == CaptureGrab.Clean ? Program.DrawUI : null;
        _screenshot(1, grab == CaptureGrab.Clean ? "" : "ui");
        return true;
    }

    public static void RequestStill(int scale, int warmFrames)
    {
        AfterGrab();
        _screenshot ??= GameReflection.Program_Screenshot!.CreateDelegate<Action<int, string>>();
        _cleanCapture = Capture();
        _drawUiAfterGrab = Program.DrawUI;
        _screenshot(scale, FormattableString.Invariant($"warm={warmFrames}"));
    }

    private static ScreenshotCapture? Capture() =>
        GameReflection.Program_ScreenshotCapture!.GetValue(Program.Instance) as ScreenshotCapture;

    // A clean grab hides the interface in ScreenshotCapture.OnPrepareFrame and sets the value from
    // that moment back in ScreenshotCapture.OnPostRender, so a change of Program.DrawUI in between
    // would be undone. This sets it now and again after the grab.
    public static void SetDrawUi(bool shown)
    {
        Program.DrawUI = shown;
        FrameStats.Update(!shown);
        if (_cleanCapture?.IsActive == true)
            _drawUiAfterGrab = shown;
    }

    // The last interface owner wins, including changes during a multi-frame warm-up.
    public static void AfterGrab()
    {
        if (_cleanCapture?.IsActive == true)
            return;
        _cleanCapture = null;
        if (_drawUiAfterGrab is not bool shown)
            return;
        _drawUiAfterGrab = null;
        SetDrawUi(shown);
    }

    public static void Shutdown()
    {
        // ScreenshotCapture.Abort restores its saved interface value before the hooks are removed.
        _cleanCapture?.Abort();
        AfterGrab();
        Reset();
        _cleanCapture = null;
        _drawUiAfterGrab = null;
    }

    public static string GrabName(CaptureGrab grab) => grab == CaptureGrab.Clean ? "clean" : "swapchain";

    private static void WaitForIdleCapture()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (Program.Instance?.IsScreenshotCaptureActive == true && watch.ElapsedMilliseconds < 3000)
            Thread.Sleep(5);
    }

    // The end of a capture calls this while its last clean grab can still be in flight, so the
    // interface restore of SetDrawUi stays armed. Shutdown clears it too.
    public static void Reset()
    {
        _expecting = false;
        _screenshot = null;
        Result = PostRenderResult.Unknown;
    }

    // Prefix of ScreenshotCapture.OnPostRender, which saves a picture when the state is an armed
    // grab and the copy was recorded in this frame.
    public static void PostRenderPrefix(ScreenshotCapture __instance)
    {
        PostRenderCount++;
        if (!_expecting)
            return;
        try
        {
            string state = GameReflection.ScreenshotCapture_State!.GetValue(__instance)?.ToString() ?? "";
            if (state is "PendingHiRes" or "Warming")
                return;
            _expecting = false;
            bool recorded = GameReflection.ScreenshotCapture_CopyRecorded!.GetValue(__instance) is true;
            bool armed = state is "ArmSwapchainGrab" or "CaptureFrame";
            Result = armed && recorded ? PostRenderResult.Saving : PostRenderResult.NoPicture;
        }
        catch (Exception ex) when (ex is TargetException or InvalidCastException or FieldAccessException)
        {
            LogHelper.ErrorOnce("offline-postrender", $"[CineCam] The state of the screenshot capture could not be read: {ex.Message}");
        }
    }

    public static Exception? PostRenderFinalizer(Exception? __exception)
    {
        AfterGrab();
        return __exception;
    }
}
