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
            Widgets.Muted(UiText.MessageText("text.e79737527b17", "Select a shot in the camera track."));
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
            Widgets.Muted(UiText.MessageText("text.838dc89e94d5", "A preview of another shot runs."));
            if (Widgets.Button(UiText.MessageText("text.780c8456aa74", "STOP PREVIEW")))
                TakeRunner.RequestStop();
            return;
        }

        if (preview == null)
        {
            string? unsupported = CineCamPatches.CameraBlock;
            bool canStart = unsupported == null && running == null && hasVehicle && !ReplaySession.IsPlayingBack;
            string tip = unsupported ?? (ReplaySession.IsPlayingBack ? UiText.MessageText("text.61a28ba965a6", "The replay plays the whole take. Seek it on the replay dock.")
                : running != null ? UiText.MessageText("text.d8f7f43563b9", "A take runs now.")
                : !hasVehicle ? UiText.MessageText("text.12b8e2685125", "A preview needs a controlled vehicle in flight.")
                : UiText.MessageText("text.7136d508fbc4", "Plays this shot on the vehicle with no change of the sim speed."));
            if (Widgets.Button(UiText.Format("text.e82fee77c8c4", $"PREVIEW SHOT {session.ShotIndex + 1}"), canStart, tip, Widgets.Tone.Primary))
            {
                ShotFileStore.PreviewSiteId = session.Take?.Site ?? session.Document?.Site;
                ShotFileStore.PreviewAnchor = session.Take?.Anchor;
                TakeRunner.RequestPreview(new PreviewState(shot, 0.0, true) { Loop = _loop });
            }

            return;
        }

        if (Widgets.Button(preview.Playing ? UiText.MessageText("text.ca6456daeccf", "PAUSE") : UiText.MessageText("text.f53a7ab988e2", "PLAY")))
        {
            if (!preview.Playing && preview.Raw >= 1.0)
                preview.Raw = 0.0;
            preview.Playing = !preview.Playing;
        }

        Widgets.SameLine();
        if (Widgets.Button(UiText.MessageText("text.0ce770318c43", "RESTART")))
        {
            preview.Raw = 0.0;
            preview.Playing = true;
        }

        Widgets.SameLine();
        if (Widgets.Button(UiText.MessageText("text.b26da3d73082", "STOP##preview"), true, null, Widgets.Tone.Danger))
            TakeRunner.RequestStop();
        Widgets.SameLine();
        bool loop = preview.Loop;
        if (Widgets.Switch(UiText.MessageText("text.0d2fe78e4ee5", "LOOP"), ref loop))
        {
            preview.Loop = loop;
            _loop = loop;
        }

        float progress = (float)preview.Raw;
        if (Widgets.Slider(UiText.MessageText("text.4664827f8e89", "Progress"), ref progress, 0f, 1f, progress.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)))
        {
            preview.Raw = progress;
            preview.Playing = false;
        }

        Widgets.Tip(UiText.MessageText("text.51376f14cce4", "The progress of the shot before the ease. Drag it to look at any moment of the shot."));

        if (preview.Warning != null)
            Widgets.Colored(preview.Warning, Widgets.WarnColor);
    }

    private static bool _loop;

    private static void DrawCapture(EditSession session, Shot shot)
    {
        bool canType = ShotCapture.CanCapture(shot.Type);
        string block = FreeCamera.Block;
        bool free = block.Length == 0;
        string tip = !canType ? UiText.MessageText("text.7b34cf1ea248", "Only tripod, track, chase and part shots have a camera position to set.")
            : !free ? block
            : UiText.MessageText("text.9108ad55d70e", "Reads the pose of the game camera and writes it into the position, the look point and the field of view of this shot.");

        if (Widgets.Button(UiText.MessageText("text.8d9b1c2a3a3f", "SET FROM CAMERA"), canType && free, tip))
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
            Widgets.Muted(UiText.MessageText("text.c53840f9276d", "Move the free camera with the mouse, then press the button to take its pose."));
    }
}
