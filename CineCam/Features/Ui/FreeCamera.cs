using Brutal.Logging;
using CineCam.Director;
using CineCam.Editing;
using CineCam.Features.Takes;
using KSA;

namespace CineCam.Features.Ui;

internal static class FreeCamera
{
    private static bool _fovChanged;
    private static float _fovBefore;

    // A take that waits for an edit gives the camera to the player.
    public static bool CanUse => Block.Length == 0;

    // Why the window cannot use the game camera now, or empty.
    public static string Block
    {
        get
        {
            if (Program.Editor != null)
                return "The camera does not work in the vehicle editor.";
            if (Quick.QuickCamera.IsActive)
                return "A quick camera holds the camera. Stop it on the Quick page first.";
            if (TakeRunner.Current is { IsPaused: false } take)
                return take.Preview != null
                    ? "The shot preview holds the camera. Stop the preview first."
                    : "The running take holds the camera. Pause the take for an edit, or stop it.";
            return "";
        }
    }

    // Camera.SetFieldOfView takes degrees, but Camera.GetFieldOfView returns radians.
    public static float CurrentFovDegrees() =>
        (float)(Program.MainViewport.BaseCamera.GetFieldOfView() * (180.0 / Math.PI));

    public static void SetFov(float degrees)
    {
        if (!_fovChanged)
            _fovBefore = CurrentFovDegrees();
        Program.MainViewport.BaseCamera.SetFieldOfView(Math.Clamp(degrees, 5f, 150f));
        _fovChanged = true;
    }

    public static void RestoreFov()
    {
        if (!_fovChanged)
            return;
        _fovChanged = false;
        try
        {
            if (!TakeRunner.IsRunning)
                Program.MainViewport.BaseCamera.SetFieldOfView(_fovBefore);
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Warning($"[CineCam] The field of view could not be given back: {ex.Message}");
        }
    }

    // Writes the pose of the game camera into the shot, in the frame of the shot.
    public static bool SetShotFromCamera(EditSession session, Shot shot, string? siteId, WorldAnchor? anchor, out string message, out WorldAnchor? groundAnchor) =>
        KeyCapture.SetShotFromCamera(session, shot, siteId, anchor, out message, out groundAnchor);
}
