using System.Reflection;
using Brutal.Logging;
using CineCam.Core;
using CineCam.Director;
using CineCam.Features.Look;
using HarmonyLib;
using KSA;
using GameProgram = KSA.Program;

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
    private static readonly PictureCaptureOwnership Ownership = new();
    private static Action<int, string>? _screenshot;
    private static bool _expecting;
    private static bool _requesting;
    private static ScreenshotCapture? _cleanCapture;
    private static bool? _drawUiAfterGrab;

    public static bool Available { get; set; }

    /// <summary>True when the prefix of ScreenshotCapture.OnPostRender is in place, so a failed grab is known at once.</summary>
    public static bool Detects { get; set; }

    public static PostRenderResult Result { get; private set; }

    /// <summary>Counts the calls of ScreenshotCapture.OnPostRender. A frame that returns early never calls it.</summary>
    public static long PostRenderCount { get; private set; }

    private static bool NativeActive => GameProgram.Instance?.IsScreenshotCaptureActive == true;

    public static bool IsBusy => Ownership.IsBusy(NativeActive);

    public static bool CanCollect => !NativeActive && !Ownership.IsWriting;

    public static string BusyText => UiText.MessageText("capture.pending_picture", "A picture is being captured or saved. Try again in a moment.");

    public static bool TryAcquire(PictureOwner owner) =>
        GameProgram.Instance != null && Ownership.TryAcquire(owner, NativeActive);

    public static void Release(PictureOwner owner)
    {
        if (!Ownership.Owns(owner))
            return;
        try
        {
            AbortGrab();
        }
        finally
        {
            Ownership.Release(owner);
            Reset();
        }
    }

    public static bool DrawUi => _cleanCapture?.IsActive == true && _drawUiAfterGrab is bool shown
        ? shown : GameProgram.DrawUI;

    // The flag "ui" is the swapchain grab, which copies the image of this frame after the interface
    // is drawn, and scale 1 keeps the window size. Without the flag, ScreenshotCapture.Request at scale
    // 1 takes the CaptureFrame path: ScreenshotCapture.OnRenderGameCapture draws the scene of the main
    // view through the composite pass into its own 8 bit image, without the interface, the overlays and
    // the sync flash, and without a renderer rebuild. A timed-out write can keep this slot busy
    // after KSA accepts another request again.
    public static bool RequestPicture(PictureOwner owner, CaptureGrab grab, ScreenshotCollector collector)
    {
        if (!Ownership.CanRequest(owner, NativeActive))
            return false;
        // An earlier picture may have timed out and finished during the retry. Exclude it before
        // the next grab starts.
        collector.Snapshot();
        AfterGrab();
        Result = PostRenderResult.Unknown;
        _expecting = true;
        _cleanCapture = grab == CaptureGrab.Clean ? Capture() : null;
        _drawUiAfterGrab = grab == CaptureGrab.Clean ? GameProgram.DrawUI : null;
        Request(1, grab == CaptureGrab.Clean ? "" : "ui");
        return true;
    }

    public static void RequestStill(int scale, int warmFrames)
    {
        AfterGrab();
        _cleanCapture = Capture();
        _drawUiAfterGrab = GameProgram.DrawUI;
        Request(scale, FormattableString.Invariant($"warm={warmFrames}"));
    }

    private static void Request(int scale, string flags)
    {
        _screenshot ??= GameReflection.Program_Screenshot!.CreateDelegate<Action<int, string>>();
        _requesting = true;
        try
        {
            _screenshot(scale, flags);
        }
        finally
        {
            _requesting = false;
        }
    }

    // The stock key and console command both call ScreenshotCapture.Request.
    public static bool RequestPrefix()
    {
        if (_requesting || !Ownership.IsBusy(nativeActive: false))
            return true;
        DefaultCategory.Log.Warning($"[CineCam] {BusyText}");
        return false;
    }

    private static ScreenshotCapture? Capture() =>
        GameReflection.Program_ScreenshotCapture!.GetValue(GameProgram.Instance) as ScreenshotCapture;

    // A clean grab hides the interface in ScreenshotCapture.OnPrepareFrame and sets the value from
    // that moment back in ScreenshotCapture.OnPostRender, so a change of Program.DrawUI in between
    // would be undone. This sets it now and again after the grab.
    public static void SetDrawUi(bool shown)
    {
        GameProgram.DrawUI = shown;
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
        if (Ownership.Owner is { } owner)
            Release(owner);
        else
            _cleanCapture?.Abort();
        AfterGrab();
        Reset();
        _cleanCapture = null;
        _drawUiAfterGrab = null;
    }

    public static string GrabName(CaptureGrab grab) => grab == CaptureGrab.Clean ? "clean" : "swapchain";

    public static void AbortGrab()
    {
        try
        {
            if (NativeActive)
                Capture()?.Abort();
            AfterGrab();
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("offline-abort", $"[CineCam] The screenshot capture could not be aborted: {ex.Message}");
        }
    }

    // The clean grab can still own the interface when a caller resets detection.
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

    // OnPostRender sets the native state to Idle and then schedules SaveToPng. Keep the task so a
    // later request cannot race with that write, including screenshots started by the stock key.
    public static IEnumerable<CodeInstruction> TrackPngWrite(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo run = AccessTools.DeclaredMethod(typeof(Task), nameof(Task.Run), [typeof(Action)]);
        MethodInfo trackedRun = AccessTools.DeclaredMethod(typeof(OfflineHooks), nameof(RunPngWriter));
        var code = instructions.ToList();
        CodeInstruction[] calls = code.Where(instruction => instruction.Calls(run)).ToArray();
        if (calls.Length != 1)
            throw new InvalidOperationException("Expected one PNG task in ScreenshotCapture.OnPostRender.");
        calls[0].operand = trackedRun;
        return code;
    }

    private static Task RunPngWriter(Action save)
    {
        Task writer = Task.Run(save);
        Ownership.TrackWrite(writer);
        return writer;
    }
}
