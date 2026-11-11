using Brutal.ImGuiApi;
using CineCam.Director;
using CineCam.Editing;
using CineCam.Features.Replay;
using CineCam.Features.Takes;
using CineCam.Patches;
using KSA;

namespace CineCam.Features.Ui;

internal static class PreviewPanel
{
    private static string _captureMessage = "";
    private static bool _captureFailed;

    public static void Draw(EditSession session)
    {
        Shot? shot = session.SelectedShot;
        if (shot == null)
        {
            Widgets.Muted("Select a shot in the camera track.");
            return;
        }

        DrawPlayer(session, shot);
        ConsoleWidgets.Rule();
        DrawCapture(session, shot);
    }

    private static void DrawPlayer(EditSession session, Shot shot)
    {
        ActiveTake? running = TakeRunner.Current;
        PreviewState? preview = running?.Preview;
        bool mine = preview != null && ReferenceEquals(preview.Shot, shot);
        Vehicle? vehicle = Program.ControlledVehicle;
        bool hasVehicle = vehicle != null && !vehicle.IsDisposed && Program.Editor == null;

        if (preview != null && !mine)
        {
            Widgets.Muted("A preview of another shot runs.");
            if (Widgets.Button("STOP PREVIEW"))
                TakeRunner.RequestStop();
            return;
        }

        if (preview == null)
        {
            string? unsupported = CineCamPatches.CameraBlock;
            bool canStart = unsupported == null && running == null && hasVehicle && !ReplaySession.IsPlayingBack;
            string tip = unsupported ?? (ReplaySession.IsPlayingBack ? "The replay plays the whole take. Seek it on the replay dock."
                : running != null ? "A take runs now."
                : !hasVehicle ? "A preview needs a controlled vehicle in flight."
                : "Plays this shot on the vehicle with no change of the sim speed.");
            if (Widgets.Button($"PREVIEW SHOT {session.ShotIndex + 1}", canStart, tip, Widgets.Tone.Primary))
            {
                ShotFileStore.PreviewSiteId = session.Take?.Site ?? session.Document?.Site;
                ShotFileStore.PreviewAnchor = session.Take?.Anchor;
                TakeRunner.RequestPreview(new PreviewState(shot, 0.0, true) { Loop = _loop });
            }

            return;
        }

        if (Widgets.Button(preview.Playing ? "PAUSE" : "PLAY"))
        {
            if (!preview.Playing && preview.Raw >= 1.0)
                preview.Raw = 0.0;
            preview.Playing = !preview.Playing;
        }

        Widgets.SameLine();
        if (Widgets.Button("RESTART"))
        {
            preview.Raw = 0.0;
            preview.Playing = true;
        }

        Widgets.SameLine();
        if (Widgets.Button("STOP##preview", true, null, Widgets.Tone.Danger))
            TakeRunner.RequestStop();
        Widgets.SameLine();
        bool loop = preview.Loop;
        if (Widgets.Switch("LOOP", ref loop))
        {
            preview.Loop = loop;
            _loop = loop;
        }

        float progress = (float)preview.Raw;
        if (Widgets.Slider("Progress", ref progress, 0f, 1f, progress.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)))
        {
            preview.Raw = progress;
            preview.Playing = false;
        }

        Widgets.Tip("The progress of the shot before the ease. Drag it to look at any moment of the shot.");

        if (preview.Warning != null)
            Widgets.Colored(preview.Warning, Widgets.WarnColor);
    }

    private static bool _loop;

    private static void DrawCapture(EditSession session, Shot shot)
    {
        bool canType = ShotCapture.CanCapture(shot.Type);
        string block = FreeCamera.Block;
        bool free = block.Length == 0;
        string tip = !canType ? "Only tripod, track, chase and part shots have a camera position to set."
            : !free ? block
            : "Reads the pose of the game camera and writes it into the position, the look point and the field of view of this shot.";

        if (Widgets.Button("SET FROM CAMERA", canType && free, tip))
        {
            string? site = session.Take?.Site ?? session.Document?.Site;
            _captureFailed = !FreeCamera.SetShotFromCamera(session, shot, site, session.Take?.Anchor, out _captureMessage, out WorldAnchor? ground);
            if (!_captureFailed)
            {
                session.AdoptAnchor(ground);
                session.Changed();
            }
        }

        if (_captureMessage.Length > 0)
            Widgets.Colored(_captureMessage, _captureFailed ? Widgets.ErrorColor : Widgets.MutedColor);
        if (canType && _captureMessage.Length == 0)
            Widgets.Muted("Move the free camera with the mouse, then press the button to take its pose.");
    }
}
