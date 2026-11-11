using System.Reflection;
using Brutal.Logging;
using CineCam.Core;
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

    public static bool Available { get; set; }

    /// <summary>True when the prefix of ScreenshotCapture.OnPostRender is in place, so a failed grab is known at once.</summary>
    public static bool Detects { get; set; }

    public static PostRenderResult Result { get; private set; }

    /// <summary>Counts the calls of ScreenshotCapture.OnPostRender. A frame that returns early never calls it.</summary>
    public static long PostRenderCount { get; private set; }

    // The flag "ui" is the swapchain grab, which copies the image of this frame after the interface
    // is drawn, and scale 1 keeps the window size. Returns false while another capture runs, because
    // the game refuses a second request then.
    public static bool RequestPicture()
    {
        _screenshot ??= GameReflection.Program_Screenshot!.CreateDelegate<Action<int, string>>();
        WaitForIdleCapture();
        if (Program.Instance?.IsScreenshotCaptureActive == true)
            return false;
        Result = PostRenderResult.Unknown;
        _expecting = true;
        _screenshot(1, "ui");
        return true;
    }

    private static void WaitForIdleCapture()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (Program.Instance?.IsScreenshotCaptureActive == true && watch.ElapsedMilliseconds < 3000)
            Thread.Sleep(5);
    }

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
        _expecting = false;
        try
        {
            string state = GameReflection.ScreenshotCapture_State!.GetValue(__instance)?.ToString() ?? "";
            bool recorded = GameReflection.ScreenshotCapture_CopyRecorded!.GetValue(__instance) is true;
            bool armed = state is "ArmSwapchainGrab" or "CaptureFrame";
            Result = armed && recorded ? PostRenderResult.Saving : PostRenderResult.NoPicture;
        }
        catch (Exception ex) when (ex is TargetException or InvalidCastException or FieldAccessException)
        {
            LogHelper.ErrorOnce("offline-postrender", $"[CineCam] The state of the screenshot capture could not be read: {ex.Message}");
        }
    }
}
