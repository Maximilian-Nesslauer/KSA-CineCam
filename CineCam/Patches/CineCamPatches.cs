using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Logging;
using CineCam.Core;
using CineCam.Features;
using CineCam.Features.Autotest;
using CineCam.Features.Quick;
using CineCam.Features.Replay;
using CineCam.Features.Takes;
using CineCam.Features.Ui;
using CineCam.Features.TimeOfDay;
using HarmonyLib;
using KSA;

namespace CineCam.Patches;

// The patches are applied by hand, one group per feature, so a game update that breaks one
// target turns off only that feature.
internal static class CineCamPatches
{
    // The commands need the terminal, which may be created after the mods load.
    private const int CommandRetryFrames = 1800;
    private static int _commandFrames;
    internal static bool KeysAvailable { get; private set; }
    internal static bool FrameAvailable { get; private set; }
    internal static bool CameraAvailable { get; private set; }

    /// <summary>Why the requests that run in the frame hook cannot run on this game build, or null.</summary>
    internal static string? FrameBlock => FrameAvailable ? null
        : "This game build does not run the CineCam frame hook, see the log at the start of the game.";

    /// <summary>Why a take, a dry run, a shot preview or a quick camera cannot start on this game build, or null.</summary>
    internal static string? CameraBlock => FrameBlock ?? (CameraAvailable ? null
        : "This game build does not let CineCam move the camera, see the log at the start of the game.");

    public static void Apply(Harmony harmony)
    {
        FrameAvailable = Group(GameReflection.ValidateFrame(),
            "[CineCam] Takes, dry runs, shot previews, quick cameras, checkpoints, resets, the sun move, the offline capture, the replay preview pictures, the replay render and the self test are disabled. Replays still open and play",
            () => PatchFrame(harmony));
        if (FrameAvailable)
            PatchOffline(harmony);

        CameraAvailable = Group(GameReflection.ValidateCamera(),
            "[CineCam] Takes, dry runs, shot previews, quick cameras, the automatic start after a reset and the self test are disabled, because CineCam cannot move the camera. Replays, checkpoints and resets still work",
            () => harmony.Patch(GameReflection.FixedController_OnFrame,
                prefix: new HarmonyMethod(typeof(CineCamPatches), nameof(CameraPrefix))));

        Group(GameReflection.ValidateAlertSuppression(),
            "[CineCam] The yellow \"Fixed camera\" alert stays",
            () => harmony.Patch(GameReflection.FixedController_OnSwitchOn,
                prefix: new HarmonyMethod(typeof(CineCamPatches), nameof(SwitchOnPrefix))));

        SaveLoadWatch.Available = Group(GameReflection.ValidateLoadWatch(),
            "[CineCam] A reset finds the end of the load by the new vehicle only",
            () => harmony.Patch(GameReflection.UncompressedSave_Load,
                prefix: new HarmonyMethod(typeof(SaveLoadWatch), nameof(SaveLoadWatch.Prefix)),
                postfix: new HarmonyMethod(typeof(SaveLoadWatch), nameof(SaveLoadWatch.Postfix))));

        SequenceWatch.Available = Group(GameReflection.ValidateSequenceWatch(),
            "[CineCam] A sequence that loses all its parts at once may be missed by the events",
            () => harmony.Patch(GameReflection.SequenceList_ActivateNextSequence,
                prefix: new HarmonyMethod(typeof(SequenceWatch), nameof(SequenceWatch.Prefix)),
                postfix: new HarmonyMethod(typeof(SequenceWatch), nameof(SequenceWatch.Postfix))));

        KeysAvailable = Group(GameReflection.ValidateKeys(),
            "[CineCam] Keys disabled. Use the console commands",
            () => harmony.Patch(GameReflection.Program_OnKey,
                postfix: new HarmonyMethod(typeof(CineCamPatches), nameof(KeyPostfix))));

        Group(GameReflection.ValidateConsoleHide(),
            "[CineCam] Console lines may show in the pictures of a take",
            () => harmony.Patch(GameReflection.ConsoleWindow_Draw,
                prefix: new HarmonyMethod(typeof(CineCamPatches), nameof(ConsoleDrawPrefix))));

        Group(GameReflection.ValidateSyncFlash(),
            "[CineCam] The sync flash is disabled",
            () => harmony.Patch(GameReflection.Program_DrawFps,
                postfix: new HarmonyMethod(typeof(CineCamPatches), nameof(DrawFpsPostfix))));
    }

    // A patch that throws turns off only its own feature. Mod.Unload removes the patches that went in.
    private static bool Group(bool valid, string disabled, Action patch)
    {
        if (!valid)
        {
            DefaultCategory.Log.Warning($"{disabled} - reflection targets not found.");
            return false;
        }

        try
        {
            patch();
            return true;
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Warning($"{disabled} - the patch failed: {ex.Message}");
            return false;
        }
    }

    // The prefix that changes the frame time needs the parameter names of Program.OnFrame.
    // Without them the plain prefix runs and only the offline capture is off.
    private static void PatchFrame(Harmony harmony)
    {
        bool timed = GameReflection.ValidateFrameTime() && GameReflection.ValidateOffline();
        harmony.Patch(GameReflection.Program_OnFrame,
            prefix: new HarmonyMethod(typeof(CineCamPatches), timed ? nameof(FramePrefixTimed) : nameof(FramePrefix)),
            postfix: new HarmonyMethod(typeof(CineCamPatches), nameof(FramePostfix)));
        OfflineHooks.Available = timed;
    }

    private static void PatchOffline(Harmony harmony)
    {
        if (!OfflineHooks.Available)
        {
            DefaultCategory.Log.Warning("[CineCam] The offline capture, the replay preview pictures and the replay render are disabled - reflection targets not found.");
            return;
        }

        OfflineHooks.Detects = Group(GameReflection.ValidateOfflineDetection(),
            "[CineCam] A failed picture grab is found by a time out only",
            () => harmony.Patch(GameReflection.ScreenshotCapture_OnPostRender,
                prefix: new HarmonyMethod(typeof(OfflineHooks), nameof(OfflineHooks.PostRenderPrefix))));
    }

    public static void Reset()
    {
        _commandFrames = 0;
        KeysAvailable = false;
        FrameAvailable = false;
        CameraAvailable = false;
        SaveLoadWatch.Reset();
        SequenceWatch.Reset();
        OfflineHooks.Available = false;
        OfflineHooks.Detects = false;
        OfflineHooks.Reset();
    }

    private static void FramePrefix()
    {
        try
        {
            QuickCamera.OnFrameStart();
            TakeRunner.OnFrameStart();
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("frame-prefix", $"[CineCam] Error at the start of the frame: {ex}");
        }
    }

    // The names of the parameters are those of Program.OnFrame. The offline capture changes
    // both times by reference, after the requests of the player ran.
    private static void FramePrefixTimed(ref double currentPlayerTime, ref double dtPlayer)
    {
        FramePrefix();
        try
        {
            TakeRunner.OnOfflineFrame(ref currentPlayerTime, ref dtPlayer);
            ReplaySession.OnRenderFrame(ref currentPlayerTime, ref dtPlayer);
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("frame-prefix-offline", $"[CineCam] Error in the offline frame: {ex}");
        }
    }

    private static void FramePostfix()
    {
        try
        {
            TakeRunner.OnFrameEnd();
            QuickCamera.OnFrameEnd();
            TakeRunner.OnFrameLate();
            ReplaySession.UpdatePreview();
            ReplaySession.OnRenderFrameEnd();
            SunWarp.Tick();
            AutotestRunner.OnFrameEnd();
            if (_commandFrames < CommandRetryFrames && ++_commandFrames % 30 == 1)
            {
                if (CineCamCommands.TryRegister())
                    _commandFrames = CommandRetryFrames;
            }
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("frame-postfix", $"[CineCam] Error at the end of the frame: {ex}");
        }
    }

    // Only the controller of the main viewport belongs to the take, the other viewports keep the
    // stock behavior.
    private static bool CameraPrefix(FixedController __instance)
    {
        if (!TakeRunner.IsRunning && !QuickCamera.IsActive)
            return true;
        if (!ReferenceEquals(__instance, Program.MainViewport.FixedController))
            return true;
        return TakeRunner.IsRunning ? !TakeRunner.OnCameraFrame() : !QuickCamera.OnCameraFrame();
    }

    private static bool SwitchOnPrefix(FixedController __instance)
    {
        if (!TakeRunner.IsRunning && !QuickCamera.IsActive && !ReplaySession.IsPlayingBack)
            return true;
        return !ReferenceEquals(__instance, Program.MainViewport.FixedController);
    }

    // The same guard as the game uses for its own keys. The keys work while the game interface is
    // hidden, because Program.OnKey does not depend on it.
    private static void KeyPostfix(GlfwKey key, GlfwKeyAction action)
    {
        try
        {
            if (action != GlfwKeyAction.Press || !Program.IsLoaded)
                return;
            if (ImGui.GetIO().WantCaptureKeyboard || Program.ConsoleWindow.IsOpen)
                return;
            if (ReplaySession.IsRendering)
            {
                // Program.DispatchKeyEvent opens EscMenu with InputAction.OpenGameSettings (Escape
                // by default) before this postfix. A render does not start while a menu is open, so
                // a menu that is open now came from this key and closes before Program.UpdateMenuPause sees it.
                if (EscMenu.Open)
                    EscMenu.Open = false;
                if (key == GlfwKey.Escape || CineCamSettings.StartStopKey == key)
                    ReplaySession.StopRender();
                return;
            }

            if (ReplaySession.IsPlayingBack)
            {
                // An open game menu keeps the replay, because closing the replay under the menu would
                // hand the menu pause the replay speed. The window key still works then, because a
                // menu that opened while the interface was hidden is visible only after the key
                // shows the interface.
                if (CineCamSettings.StartStopKey == key)
                {
                    if (!ReplaySession.GameMenuOpen)
                        ReplaySession.Close();
                }
                else if (CineCamSettings.WindowKey == key)
                {
                    if (!Program.DrawUI)
                        ReplaySession.ToggleInterface();
                    else
                        CineCamWindow.RequestToggle();
                }
                else if (CineCamSettings.PlayPauseKey == key)
                {
                    if (!ReplaySession.GameMenuOpen)
                        ReplaySession.SetPlaying(!ReplaySession.IsPlaying);
                }

                return;
            }

            if (CineCamSettings.StartStopKey == key)
                TakeRunner.RequestToggle();
            else if (CineCamSettings.ReloadKey == key)
                TakeRunner.RequestReload();
            else if (CineCamSettings.ResetKey == key)
                TakeRunner.RequestReset(null);
            else if (CineCamSettings.EditKey == key)
                TakeRunner.RequestPauseToggle();
            else if (CineCamSettings.MarkKey == key)
                TakeRunner.RequestMark();
            else if (CineCamSettings.WindowKey == key)
                CineCamWindow.RequestToggle();
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("key", $"[CineCam] Error in the key handler: {ex}");
        }
    }

    private static bool ConsoleDrawPrefix()
    {
        return !TakeRunner.HidesInterface && (!ReplaySession.IsPlayingBack || Program.DrawUI);
    }

    private static void DrawFpsPostfix()
    {
        TakeRunner.OnDrawOverlay();
    }
}
