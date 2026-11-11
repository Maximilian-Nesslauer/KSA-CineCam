using System.Reflection;
using Brutal.GlfwApi;
using Brutal.Logging;
using HarmonyLib;
using KSA;

namespace CineCam.Core;

// The reflection targets in the game, resolved once at load. Each Validate method checks the
// targets of one feature, so a game update turns off only the feature that broke.
internal static class GameReflection
{
    #region Frame hooks

    // Program.OnFrame is protected and runs the whole frame. The prefix handles queued
    // requests before the game steps, and the postfix guards the take.
    public static readonly MethodInfo? Program_OnFrame =
        AccessTools.Method(typeof(Program), "OnFrame", new[] { typeof(double), typeof(double) });

    // The main camera is driven from this controller, after the vehicles have their new state
    // and before Camera.OnFrame builds the matrices.
    public static readonly MethodInfo? FixedController_OnFrame =
        AccessTools.Method(typeof(FixedController), "OnFrame", new[] { typeof(IViewport), typeof(double) });

    // FixedController.OnSwitchOn shows a "Fixed camera" alert.
    public static readonly MethodInfo? FixedController_OnSwitchOn =
        AccessTools.Method(typeof(FixedController), "OnSwitchOn", new[] { typeof(CameraMode) });

    #endregion

    #region Keys

    public static readonly MethodInfo? Program_OnKey =
        AccessTools.Method(typeof(Program), "OnKey");

    #endregion

    #region Sync flash

    // Private static, drawn every frame even when the game interface is hidden.
    public static readonly MethodInfo? Program_DrawFps =
        AccessTools.Method(typeof(Program), "DrawFps", Type.EmptyTypes);

    #endregion

    #region Offline capture

    // The terminal command "screenshot". The flag "ui" arms the swapchain grab, which records the
    // finished picture of the same frame, with the interface and the overlays of other mods.
    public static readonly MethodInfo? Program_Screenshot =
        AccessTools.Method(typeof(Program), "Screenshot", new[] { typeof(int), typeof(string) });

    // ScreenshotCapture.OnPostRender saves the picture of the frame. A prefix reads its state to
    // learn whether this frame has a picture, so a failed grab is known at once.
    public static readonly MethodInfo? ScreenshotCapture_OnPostRender =
        AccessTools.Method(typeof(ScreenshotCapture), "OnPostRender", Type.EmptyTypes);

    public static readonly FieldInfo? ScreenshotCapture_State =
        AccessTools.Field(typeof(ScreenshotCapture), "_state");

    public static readonly FieldInfo? ScreenshotCapture_CopyRecorded =
        AccessTools.Field(typeof(ScreenshotCapture), "_copyRecordedThisFrame");

    // The console draws its lines over every picture. A prefix skips the draw while a take hides the interface.
    public static readonly MethodInfo? ConsoleWindow_Draw =
        AccessTools.Method(typeof(Brutal.ImGuiApi.Abstractions.ConsoleWindow), "Draw",
            new[] { typeof(Brutal.Numerics.float2), typeof(Brutal.Numerics.float2), typeof(float), typeof(bool) });

    #endregion

    #region Save and load

    // The method of a finished load. A postfix compares UniverseData by reference to see whether the load happened.
    public static readonly MethodInfo? UncompressedSave_Load =
        AccessTools.Method(typeof(UncompressedSave), "Load", Type.EmptyTypes);

    public static readonly FieldInfo? GameSave_UniverseData =
        AccessTools.Field(typeof(GameSave), "UniverseData");

    #endregion

    #region Sequences

    // Every staging path ends here (the staging key, an autopilot). A prefix and postfix record which sequence fired.
    public static readonly MethodInfo? SequenceList_ActivateNextSequence =
        AccessTools.Method(typeof(SequenceList), "ActivateNextSequence", new[] { typeof(Vehicle) });

    #endregion

    #region Validation

    public static bool ValidateFrame()
    {
        var targets = new (string name, object? target)[]
        {
            ("Program.OnFrame(double, double)", Program_OnFrame),
        };
        return ValidateTargets("Frame", targets);
    }

    public static bool ValidateCamera()
    {
        var targets = new (string name, object? target)[]
        {
            ("FixedController.OnFrame(IViewport, double)", FixedController_OnFrame),
        };
        return ValidateTargets("Camera", targets);
    }

    // The offline prefix changes both times of Program.OnFrame by reference, so the parameters
    // must keep their names and the type double.
    public static bool ValidateFrameTime()
    {
        ParameterInfo[]? parameters = Program_OnFrame?.GetParameters();
        bool hasTime = parameters?.Any(p => p.Name == "currentPlayerTime" && p.ParameterType == typeof(double)) ?? false;
        bool hasDelta = parameters?.Any(p => p.Name == "dtPlayer" && p.ParameterType == typeof(double)) ?? false;
        var targets = new (string name, object? target)[]
        {
            ("Program.OnFrame parameter \"currentPlayerTime\" (double)", hasTime ? parameters : null),
            ("Program.OnFrame parameter \"dtPlayer\" (double)", hasDelta ? parameters : null),
        };
        return ValidateTargets("FrameTime", targets);
    }

    public static bool ValidateOffline()
    {
        var targets = new (string name, object? target)[]
        {
            ("Program.Screenshot(int, string)", Program_Screenshot),
        };
        return ValidateTargets("OfflineCapture", targets);
    }

    public static bool ValidateOfflineDetection()
    {
        var targets = new (string name, object? target)[]
        {
            ("ScreenshotCapture.OnPostRender()", ScreenshotCapture_OnPostRender),
            ("ScreenshotCapture._state", ScreenshotCapture_State),
            ("ScreenshotCapture._copyRecordedThisFrame", ScreenshotCapture_CopyRecorded),
        };
        return ValidateTargets("OfflineDetection", targets);
    }

    public static bool ValidateAlertSuppression()
    {
        var targets = new (string name, object? target)[]
        {
            ("FixedController.OnSwitchOn(CameraMode)", FixedController_OnSwitchOn),
        };
        return ValidateTargets("AlertSuppression", targets);
    }

    // The postfix reads the parameters "key" and "action" by name, so both must exist with
    // the types it expects.
    public static bool ValidateKeys()
    {
        ParameterInfo[]? parameters = Program_OnKey?.GetParameters();
        bool hasKey = parameters?.Any(p => p.Name == "key" && p.ParameterType == typeof(GlfwKey)) ?? false;
        bool hasAction = parameters?.Any(p => p.Name == "action" && p.ParameterType == typeof(GlfwKeyAction)) ?? false;
        var targets = new (string name, object? target)[]
        {
            ("Program.OnKey", Program_OnKey),
            ("Program.OnKey parameter \"key\" (GlfwKey)", hasKey ? parameters : null),
            ("Program.OnKey parameter \"action\" (GlfwKeyAction)", hasAction ? parameters : null),
        };
        return ValidateTargets("Keys", targets);
    }

    public static bool ValidateLoadWatch()
    {
        var targets = new (string name, object? target)[]
        {
            ("UncompressedSave.Load()", UncompressedSave_Load),
            ("GameSave.UniverseData", GameSave_UniverseData),
        };
        return ValidateTargets("LoadWatch", targets);
    }

    // The postfix reads the parameter "vehicle" by name.
    public static bool ValidateSequenceWatch()
    {
        bool hasVehicle = SequenceList_ActivateNextSequence?.GetParameters().Any(p => p.Name == "vehicle" && p.ParameterType == typeof(Vehicle)) ?? false;
        var targets = new (string name, object? target)[]
        {
            ("SequenceList.ActivateNextSequence(Vehicle)", SequenceList_ActivateNextSequence),
            ("SequenceList.ActivateNextSequence parameter \"vehicle\" (Vehicle)", hasVehicle ? SequenceList_ActivateNextSequence : null),
        };
        return ValidateTargets("SequenceWatch", targets);
    }

    public static bool ValidateConsoleHide()
    {
        var targets = new (string name, object? target)[]
        {
            ("ConsoleWindow.Draw(float2, float2, float, bool)", ConsoleWindow_Draw),
        };
        return ValidateTargets("ConsoleHide", targets);
    }

    public static bool ValidateSyncFlash()
    {
        var targets = new (string name, object? target)[]
        {
            ("Program.DrawFps()", Program_DrawFps),
        };
        return ValidateTargets("SyncFlash", targets);
    }

    private static bool ValidateTargets(string feature, (string name, object? target)[] targets)
    {
        bool allOk = true;
        foreach (var (name, target) in targets)
        {
            if (target == null)
            {
                DefaultCategory.Log.Error(
                    $"[CineCam] {feature}: {name} not found - game version may have changed.");
                allOk = false;
            }
        }
        return allOk;
    }

    #endregion
}
