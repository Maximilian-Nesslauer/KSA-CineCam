using System.Globalization;
using Brutal.ImGuiApi;
using CineCam.Director;
using CineCam.Editing;
using CineCam.Features.Takes;
using KSA;

namespace CineCam.Features.Ui;

// A pose change goes into the running take and into the file of the window. A trigger or a speed
// key goes into the file of the window only, because the timeline of the running take is fixed
// when the take starts.
internal static class RehearsalPanel
{
    private static ActiveTake? _lastTake;
    private static int _lastPause = -1;
    private static double _speed = 1.0;
    private static double _ramp = 1.0;
    private static string _message = "";
    private static bool _messageIsError;

    public static void Draw(EditSession session, ActiveTake take)
    {
        if (!ReferenceEquals(_lastTake, take) || _lastPause != take.Clock.PauseCount)
            BeginPause(session, take);

        DirectorFrame? frame = take.LastFrame;
        int index = frame?.ShotIndex ?? -1;
        double now = take.Seconds;
        Take? docTake = session.Document?.FindTake(take.Take.Name);
        Shot? runShot = index >= 0 && index < take.Take.Shots.Count ? take.Take.Shots[index] : null;
        Shot? docShot = MatchingDocShot(take, docTake, index);

        Widgets.Colored(UiText.Format("text.be03f0101bd4", $"Take \"{take.Take.Name}\" is paused at {now:F1} s. The sim is stopped and the camera is free."), Widgets.WarnColor);
        DrawButtons(session, take);
        if (docShot == null && runShot != null)
            Widgets.Muted(docTake != null && docTake.Shots.Count != take.Take.Shots.Count
                ? UiText.MessageText("text.ca56d55afe03", "The take in the window has another number of shots than this run, because a shot was added or removed. The camera edits of this pause change this run only. Stop the take and start it again to edit the window file here.")
                : UiText.MessageText("text.70a3ed9a1e0e", "The window file has no matching take or shot, so edits change this run only."));

        Widgets.Section(UiText.MessageText("text.485cfcdcc0eb", "Current shot"));
        DrawShotInfo(take, frame, runShot, index);

        Widgets.Section(UiText.MessageText("text.03494b0d1f80", "Camera"));
        DrawCamera(session, take, frame, runShot, docShot);
        KeyPanel.Draw(session);

        Widgets.Section(UiText.MessageText("text.fc4e84255a41", "Timing"));
        DrawTiming(session, take, docShot, index, now);

        Widgets.Section(UiText.MessageText("text.c372fee9b456", "Speed"));
        DrawSpeed(session, take, docTake, now);

        Widgets.Section(UiText.MessageText("text.a8e7ac92a116", "Staging"));
        DrawStaging(session, take, docTake, now);

        if (_message.Length > 0)
            Widgets.Colored(_message, _messageIsError ? Widgets.ErrorColor : Widgets.MutedColor);
    }

    private static void BeginPause(EditSession session, ActiveTake take)
    {
        _lastTake = take;
        _lastPause = take.Clock.PauseCount;
        _speed = take.PausedSpeed;
        _ramp = 1.0;
        _message = "";
        _messageIsError = false;

        if (session.Document != null)
        {
            int takeIndex = session.Document.Takes.FindIndex(t => string.Equals(t.Name, take.Take.Name, StringComparison.OrdinalIgnoreCase));
            int shotIndex = take.LastFrame?.ShotIndex ?? -1;
            if (takeIndex >= 0)
            {
                session.SelectTake(takeIndex);
                if (shotIndex >= 0 && shotIndex < session.Take!.Shots.Count)
                    session.ShotIndex = shotIndex;
            }
        }
    }

    private static Shot? MatchingDocShot(ActiveTake take, Take? docTake, int index)
    {
        if (docTake == null || index < 0 || index >= take.Take.Shots.Count || docTake.Shots.Count != take.Take.Shots.Count)
            return null;
        Shot docShot = docTake.Shots[index];
        return docShot.Type == take.Take.Shots[index].Type ? docShot : null;
    }

    private static void DrawButtons(EditSession session, ActiveTake take)
    {
        if (Widgets.Button(UiText.MessageText("text.0747c96a23db", "RESUME"), true, UiText.MessageText("text.edd0e0f80dd4", "Gives the camera back to the shot and the sim speed back to what it was. The take clock goes on."), Widgets.Tone.Primary))
            TakeRunner.RequestResume();
        Widgets.SameLine();
        if (Widgets.Button(UiText.MessageText("text.8e40e9a1762e", "STOP TAKE"), true, UiText.MessageText("text.835e485df8f8", "Ends the take. The edits stay in the window."), Widgets.Tone.Danger))
            TakeRunner.RequestStop();
        Widgets.SameLine();
        if (Widgets.Button(UiText.MessageText("text.0dbb91e90790", "SAVE FILE"), session.Document != null && session.Dirty, UiText.MessageText("text.e694a1ef2dae", "Writes the shot file. The old file stays as a .bak copy.")))
        {
            if (session.Save())
                ShotFileStore.PathOverride = session.Path;
        }

        if (session.Dirty)
        {
            Widgets.SameLine();
            Widgets.Colored(UiText.MessageText("text.c4751a7ab84a", "unsaved changes"), Widgets.WarnColor);
        }
    }

    private static void DrawShotInfo(ActiveTake take, DirectorFrame? frame, Shot? shot, int index)
    {
        if (shot == null || frame == null)
        {
            Widgets.Muted(UiText.MessageText("text.a0babdc9ebc9", "No shot has started yet. The camera holds its pose from the take start."));
            return;
        }

        string part = shot.Type == ShotType.Part && shot.Part.Length > 0 ? UiText.Format("text.10323ba0a9e9", $", part {shot.Part}") : "";
        Widgets.Wrapped(UiText.Format("text.ba9ce45230b2", $"Shot {index + 1} of {take.Take.Shots.Count}: {ShotLabels.Type(shot.Type)}, frame {ShotLabels.Frame(ShotLabels.FrameOf(shot), shot.Type)}{part}"));
        Widgets.Wrapped(UiText.Format("text.2e71bb76cae5", $"Time in the shot {frame.LocalTime:F1} s of {shot.Duration:0.##} s ({frame.Progress * 100.0:F0} % after the ease)"));
        Widgets.Muted(UiText.Concat(UiText.Concat(UiText.MessageText("text.59f9dc89bc55", "Starts "), Widgets.DescribeTrigger(shot.At)), "."));
    }

    private static void DrawCamera(EditSession session, ActiveTake take, DirectorFrame? frame, Shot? runShot, Shot? docShot)
    {
        // Not FreeCamera.SetFov, because the paused take owns the camera and gives back the field of
        // view of before the take when it ends.
        float fov = FreeCamera.CurrentFovDegrees();
        if (Widgets.Slider(UiText.MessageText("text.8496c3e051ff", "Field of view (deg)##edit"), ref fov, 5f, 120f, fov.ToString("0.0", CultureInfo.InvariantCulture)))
            Program.MainViewport.BaseCamera.SetFieldOfView(fov);
        Widgets.Tip(UiText.MessageText("text.134f400107dd", "Changes the field of view of the free camera. Apply the camera to store it in the shot."));

        string tip = runShot == null ? UiText.MessageText("text.0bcc74c9e41f", "A shot must have started.")
            : runShot.Type switch
            {
                ShotType.Tripod => UiText.MessageText("text.4c0dc8a3ccf0", "Writes the camera position and look point (in the frame of the shot target) and the field of view."),
                ShotType.Track => UiText.MessageText("text.ad09e9f648ea", "Writes the camera position as a world point and the field of view. A track shot always looks at the vehicle."),
                ShotType.Chase or ShotType.Part => UiText.MessageText("text.c117e9a41f9a", "Writes the offset and the look point in the frame of the shot (the vehicle frame, or the local horizon frame) at the vehicle or the part at this moment, and the field of view."),
                ShotType.Path => UiText.MessageText("text.94fa5b7abd8b", "Adds the camera as a point of the path at this moment, or replaces the point that is here."),
                ShotType.Orbit => UiText.MessageText("text.3e40e03a19b2", "Writes height, radius and start angle so the orbit passes through the camera at this moment, plus the look point and the field of view. The turn speed stays."),
                _ => UiText.MessageText("text.39b264739930", "Writes height, radius and azimuth so the crane passes through the camera at this moment, plus the field of view. A crane always looks at its axis."),
            };
        if (Widgets.Button(UiText.MessageText("text.ee837aa255ad", "APPLY CAMERA TO THIS SHOT"), runShot != null, tip) && runShot != null && frame != null)
            ApplyCamera(session, take, frame, runShot, docShot, addPoint: false);

        bool canPoint = runShot != null && (runShot.Type == ShotType.Path || ShotCapture.CanConvertToPath(runShot.Type));
        string pointTip = runShot == null ? UiText.MessageText("text.0bcc74c9e41f", "A shot must have started.")
            : !canPoint ? UiText.MessageText("text.cb74b93e1dd7", "Only a path, tripod or chase shot takes a path point.")
            : runShot.Type == ShotType.Path
                ? UiText.MessageText("text.78c3386c8561", "Adds the pose of the camera as a point of the path at the progress of the shot (in the frame of the shot), or replaces the point that is there.")
                : UiText.MessageText("text.0e08d2758845", "Turns this shot into a path shot: its old pose becomes point 1 and the camera becomes point 2 at the progress of the shot (at the end when the shot has just begun).");
        if (Widgets.Button(UiText.MessageText("text.819ab07d74c7", "ADD PATH POINT HERE"), canPoint, pointTip) && runShot != null && frame != null)
            ApplyCamera(session, take, frame, runShot, docShot, addPoint: true);
    }

    private static void ApplyCamera(EditSession session, ActiveTake take, DirectorFrame frame, Shot runShot, Shot? docShot, bool addPoint)
    {
        take.Site.Refresh();
        take.Vehicle.RefreshExtent(0.0, force: true);
        CameraPose pose = CameraRig.CurrentPose(take.Site);
        V3 forward = (pose.LookAt - pose.Position).NormalizedOr(V3.UnitX);
        var camera = new CapturedCamera(pose.Position, forward, pose.Fov);
        Basis3 vehicle = take.Vehicle.Frame(take.Site);
        double height = take.Vehicle.Height;
        V3? part = runShot.Type == ShotType.Part && runShot.Part.Length > 0 ? take.Vehicle.FindPart(runShot.Part) : null;

        take.Vehicle.ReadHorizon(take.Site, out V3 bodyCentre, out V3 pole);
        bool ok = addPoint
            ? ShotCapture.AddPathPoint(runShot, camera, vehicle, height, frame.Progress, out string message, bodyCentre, pole, take.Director.Heading)
            : ShotCapture.ApplyAtProgress(runShot, camera, vehicle, height, part, frame.Progress, out message, bodyCentre, pole, take.Director.Heading);
        if (ok)
        {
            // A track shot measured its start distance from the previous camera position.
            take.Director.ForgetTrackReference();
            if (docShot != null)
            {
                if (addPoint)
                    ShotCapture.AddPathPoint(docShot, camera, vehicle, height, frame.Progress, out _, bodyCentre, pole, take.Director.Heading);
                else
                    ShotCapture.ApplyAtProgress(docShot, camera, vehicle, height, part, frame.Progress, out _, bodyCentre, pole, take.Director.Heading);
                session.Changed();
            }
            else
            {
                message = UiText.Concat(message, UiText.MessageText("text.bcb3d08d9472", " The window file was not changed."));
            }
        }

        SetMessage(message, !ok);
    }

    private static void DrawTiming(EditSession session, ActiveTake take, Shot? docShot, int index, double now)
    {
        IReadOnlyDictionary<string, string> aliases = session.Document?.Events ?? take.File.Events;
        Trigger here = RehearsalEdit.TriggerHere(take.Director.Events.Seen, aliases, now);
        Widgets.Muted(UiText.Concat(UiText.Concat(UiText.MessageText("text.1c34b8a106ef", "Now is "), Widgets.DescribeTrigger(here)), "."));
        string tip = docShot == null ? UiText.MessageText("text.a38d4c200f03", "The window file has no matching shot.")
            : UiText.MessageText("text.a03c77624013", "Sets the start of this shot in the window file to this moment. The next take uses it. This run is not changed.");
        if (Widgets.Button(UiText.MessageText("text.2e42f50f3af7", "THIS SHOT STARTS HERE"), docShot != null, tip) && docShot != null)
        {
            docShot.At = here;
            session.Changed();
            SetMessage(UiText.Format("text.7467b4f04002", $"Shot {index + 1} starts {Widgets.DescribeTrigger(here)} in the next take."), false);
        }
    }

    private static void DrawSpeed(EditSession session, ActiveTake take, Take? docTake, double now)
    {
        Widgets.Num(UiText.MessageText("text.46e741b80be5", "Target speed (x)##edit"), ref _speed, 0.05f, 0.0, 1000.0);
        Widgets.Tip(UiText.MessageText("text.9aefb212d854", "The sim speed of the new key. It starts as the speed before the pause."));
        Widgets.Num(UiText.MessageText("text.287be6ed3469", "Ramp (s)##edit"), ref _ramp, 0.1f, 0.0, 3600.0);
        Widgets.Tip(UiText.MessageText("text.47771cd4ee75", "Seconds to go from the speed at the start of the key to the target speed."));

        IReadOnlyDictionary<string, string> aliases = session.Document?.Events ?? take.File.Events;
        Trigger here = RehearsalEdit.TriggerHere(take.Director.Events.Seen, aliases, now);
        string noTake = UiText.MessageText("text.b2c34af2faed", "The window file has no take with this name.");
        if (Widgets.Button(UiText.MessageText("text.aeaca9e3fd53", "ADD SPEED KEY HERE"), docTake != null, docTake == null ? noTake
                : UiText.MessageText("text.9c2fa7c68ad9", "Adds a speed key at this moment to the window file. The next take uses it.")))
        {
            RehearsalEdit.AddSpeedKey(docTake!, here, _speed, _ramp);
            session.KeyIndex = docTake!.Speed.Count - 1;
            session.Changed();
            SetMessage(UiText.Format("text.a393bc9967af", $"Speed key {docTake.Speed.Count}: to {_speed:0.##}x {Widgets.DescribeTrigger(here)}."), false);
        }

        Widgets.SameLine();
        int key = session.KeyIndex;
        bool canMove = docTake != null && key >= 0 && key < docTake.Speed.Count;
        if (Widgets.Button(canMove ? UiText.Format("text.a555479519e6", $"Move key {key + 1} here") : UiText.MessageText("text.9a942296883f", "Move key here"), canMove,
                canMove ? UiText.MessageText("text.4fc35bd8f705", "Moves the selected speed key to this moment and gives it the target speed.")
                : UiText.MessageText("text.e125ffca7af9", "Select a speed key in the speed track first.")))
        {
            RehearsalEdit.MoveSpeedKey(docTake!, key, here, _speed);
            session.Changed();
            SetMessage(UiText.Format("text.a393bc9967af", $"Speed key {key + 1}: to {_speed:0.##}x {Widgets.DescribeTrigger(here)}."), false);
        }
    }

    private static void DrawStaging(EditSession session, ActiveTake take, Take? docTake, double now)
    {
        int next = take.Vehicle.Vehicle.IsDisposed ? -1 : take.Vehicle.Vehicle.Parts.SequenceList.GetNextSequenceNumber();
        Widgets.Muted(next > 0 ? UiText.Format("text.bc15934bdf7c", $"The next sequence of the vehicle is {next}.") : UiText.MessageText("text.5680452d257e", "The vehicle has no sequence left."));

        IReadOnlyDictionary<string, string> aliases = session.Document?.Events ?? take.File.Events;
        Trigger here = RehearsalEdit.TriggerHere(take.Director.Events.Seen, aliases, now);
        string tip = docTake == null ? UiText.MessageText("text.b2c34af2faed", "The window file has no take with this name.")
            : next < 1 ? UiText.MessageText("text.82b947be4451", "The vehicle has no sequence left to fire.")
            : UiText.Format("text.02fa222daabd", $"Adds a stage action for sequence {next} at this moment to the window file. The next take fires it. This run is not changed.");
        if (Widgets.Button(UiText.MessageText("text.d4d262e4c0b9", "STAGE HERE"), docTake != null && next > 0, tip))
        {
            RehearsalEdit.AddStageAction(docTake!, here, next);
            session.ActionIndex = docTake!.Actions.Count - 1;
            session.Changed();
            SetMessage(UiText.Format("text.4135e6680d06", $"Action {docTake.Actions.Count}: stage sequence {next} {Widgets.DescribeTrigger(here)}."), false);
        }
    }

    private static void SetMessage(string text, bool isError)
    {
        _message = text;
        _messageIsError = isError;
    }
}
