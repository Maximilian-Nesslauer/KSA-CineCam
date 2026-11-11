using System.Globalization;
using System.Runtime.CompilerServices;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using CineCam.Director;
using CineCam.Editing;
using CineCam.Features.Takes;

namespace CineCam.Features.Ui;

internal static class TrackPanel
{
    private static ShotType _newShotType = ShotType.Chase;

    // The numbers of the options that the player turned off, so that on again gives them back.
    // They live only in this session, the file keeps null.
    private sealed class OffValues
    {
        public double AvoidSun = 30.0;
        public double Fit = 0.7;
    }

    private static readonly ConditionalWeakTable<Shot, OffValues> Off = new();

    public static void Reset() => Off.Clear();

    private static readonly ShotType[] ShotTypes = System.Enum.GetValues<ShotType>();

    private static readonly string[] ShotHeaders = { "#", "Type", "Starts", "Length" };
    private static readonly string[] KeyHeaders = { "#", "Speed", "Starts", "Ramp" };
    private static readonly string[] ActionHeaders = { "#", "Action", "Fires", "Number" };

    public static void DrawShots(EditSession session)
    {
        if (session.Take is not { } take)
        {
            Widgets.Muted("No take is loaded.");
            return;
        }

        KeyPanel.Draw(session);
        Widgets.Section("Camera track");
        DrawPreRoll(session, take);
        int active = TakeRunner.Current is { Preview: null } run ? run.LastFrame?.ShotIndex ?? -1 : -1;
        var rows = take.Shots.Select((s, i) => ViewText.ShotRow(i, s) with { Name = ShotLabels.Type(s.Type), Starts = Widgets.TriggerText(s.At, i) }).ToList();
        bool changed = DrawList("shots", ShotHeaders, rows, session.ShotIndex, active, i => session.ShotIndex = i,
            () => session.SelectedShot is { } shot && DrawShotFields(session, shot));
        DrawShotButtons(session, take);

        if (session.SelectedShot != null)
        {
            Widgets.Section("Preview and camera");
            PreviewPanel.Draw(session);
        }

        if (changed)
            session.Changed();
    }

    // The pre-roll loop repeats the shots before the first shot that waits for the event.
    private static void DrawPreRoll(EditSession session, Take take)
    {
        bool changed = false;
        bool on = take.PreRoll != null;
        if (Widgets.Check("Repeat the opening shots until an event", ref on,
                "The opening shots are the shots before the first shot that starts on the event below. They play again and again, one after the other, until the event comes, for example while the vehicle waits on the pad. Then the shot that waits for the event starts through its own transition."))
        {
            take.PreRoll = on ? new PreRoll { Until = take.Shots.FirstOrDefault(s => s.At.Kind == TriggerKind.Event)?.At.Event ?? "ignition" } : null;
            changed = true;
        }

        if (take.PreRoll is { } preRoll)
        {
            if (Widgets.Text("Until the event", "preroll-" + session.TakeIndex, preRoll.Until, out string until, 512,
                    "The event that ends the loop, for example ignition or liftoff. A shot of the take must start on it."))
            {
                preRoll.Until = until.Trim();
                changed = true;
            }

            bool loop = preRoll.Loop;
            if (Widgets.Check("Loop the opening shots", ref loop, "On plays the opening shots again and again until the event. Off plays them once, and the camera then holds the last pose until the event."))
            {
                preRoll.Loop = loop;
                changed = true;
            }
        }

        if (changed)
            session.Changed();
    }

    public static void DrawSpeed(EditSession session)
    {
        if (session.Take is not { } take)
        {
            Widgets.Muted("No take is loaded.");
            return;
        }

        Widgets.Section("Speed track");
        Widgets.Muted("Each key ramps the sim speed from its value at the start of the key to the target. The game locks the throttle and the engines above 30x.");
        var rows = take.Speed.Select((k, i) => ViewText.KeyRow(i, k) with { Starts = Widgets.TriggerText(k.At, i) }).ToList();
        bool changed = DrawList("keys", KeyHeaders, rows, session.KeyIndex, -1, i => session.KeyIndex = i,
            () => session.SelectedKey is { } key && DrawKeyFields(session, key));
        DrawListButtons("key", session.KeyIndex, take.Speed.Count, session.AddKey, session.DuplicateKey, session.RemoveKey, session.MoveKey,
            "Adds a speed key.");
        if (changed)
            session.Changed();
    }

    public static void DrawActions(EditSession session)
    {
        if (session.Take is not { } take)
        {
            Widgets.Muted("No take is loaded.");
            return;
        }

        Widgets.Section("Action track");
        Widgets.Muted("An action changes the vehicle once per run when its trigger is reached. A stage action presses the staging key.");
        var rows = take.Actions.Select((a, i) => ViewText.ActionRow(i, a) with { Starts = Widgets.TriggerText(a.At, i) }).ToList();
        bool changed = DrawList("actions", ActionHeaders, rows, session.ActionIndex, -1, i => session.ActionIndex = i,
            () => session.SelectedAction is { } action && DrawActionFields(session, action));
        DrawListButtons("action", session.ActionIndex, take.Actions.Count, session.AddAction, session.DuplicateAction, session.RemoveAction, session.MoveAction,
            "Adds a stage action. It fires the next sequence of the vehicle once per run.");
        if (changed)
            session.Changed();
    }

    #region Lists

    // Returns true when the fields of the selected row changed something.
    private static bool DrawList(string id, string[] headers, IReadOnlyList<TrackRow> rows, int selected, int active,
        Action<int> select, Func<bool> drawFields)
    {
        float size = ImGui.GetFontSize();
        // The last column keeps room for its longest text, so the Starts column gives way in a narrow window.
        float last = Math.Min(size * 17.0f, ImGui.GetContentRegionAvail().X - size * 5.5f);
        float starts = Math.Clamp(last - size * 5.0f, size * 4.6f, size * 8.2f);
        float[] columns = { size * 0.5f, size * 2.6f, starts, Math.Max(last, starts + size * 2.0f) };
        Widgets.ListHeader(headers, columns);

        if (rows.Count == 0)
            Widgets.Muted("Empty. Use Add below.");

        bool changed = false;
        for (int i = 0; i < rows.Count; i++)
        {
            TrackRow row = rows[i];
            string[] cells = { (i == active ? ">" : "") + row.Number, row.Name, row.Starts, row.Extra };
            if (Widgets.ListRow(id + "-" + i, cells, columns, i == selected, i == active, mutedColumn: 3))
                select(i == selected ? -1 : i);

            if (i != selected)
                continue;
            ImGui.Indent(size * 0.8f);
            changed |= drawFields();
            ImGui.Unindent(size * 0.8f);
            ImGui.Spacing();
        }

        return changed;
    }

    private static void DrawShotButtons(EditSession session, Take take)
    {
        string block = ViewText.FileEditBlock(TakeRunner.Current != null);
        bool free = block.Length == 0;
        Widgets.Choice("Type of a new shot##newshot", ref _newShotType, ShotTypes, ShotLabels.Type, ShotLabels.TypeHelp);
        if (Widgets.Button("ADD", free, free ? "Adds a shot of the chosen type with example numbers. Add key here takes the numbers from the camera." : block))
            session.AddShot(_newShotType);
        Widgets.SameLine();
        ListButtons("shot", free, block, session.ShotIndex, take.Shots.Count, session.DuplicateShot, session.RemoveShot, session.MoveShot);
        Widgets.Reason(free ? "" : block);
    }

    private static void DrawListButtons(string name, int selected, int count, Action add, Action duplicate, Action remove,
        Action<int> move, string addTip)
    {
        string block = ViewText.FileEditBlock(TakeRunner.Current != null);
        bool free = block.Length == 0;
        if (Widgets.Button("ADD##" + name, free, free ? addTip : block))
            add();
        Widgets.SameLine();
        ListButtons(name, free, block, selected, count, duplicate, remove, move);
        Widgets.Reason(free ? "" : block);
    }

    // These buttons change the lists through the session, which marks the file as changed itself.
    private static void ListButtons(string name, bool free, string block, int selected, int count, Action duplicate,
        Action remove, Action<int> move)
    {
        bool has = selected >= 0 && selected < count;
        string noItem = free ? "Select an item first." : block;
        if (Widgets.Button("DUPLICATE##" + name, free && has, noItem))
            duplicate();
        Widgets.SameLine();
        if (Widgets.Button("REMOVE##" + name, free && has, noItem, Widgets.Tone.Danger))
            remove();
        Widgets.SameLine();
        if (Widgets.Button("UP##" + name, free && selected > 0, free ? "Moves the item one place up." : block))
            move(-1);
        Widgets.SameLine();
        if (Widgets.Button("DOWN##" + name, free && has && selected < count - 1, free ? "Moves the item one place down." : block))
            move(1);
    }

    #endregion

    #region Fields

    private static bool DrawShotFields(EditSession session, Shot shot)
    {
        bool changed = false;
        string id = $"shot-{session.TakeIndex}-{session.ShotIndex}";
        ImGui.PushID(id.AsSpan());

        ShotType type = shot.Type;
        if (Widgets.Choice("Type", ref type, ShotTypes, ShotLabels.Type, ShotLabels.TypeHelp, ShotLabels.TypeHelp(shot.Type)))
        {
            ShotEdit.ChangeType(shot, type);
            changed = true;
        }

        IReadOnlyList<FrameChoice> frames = ShotLabels.Choices(shot.Type);
        if (frames.Count > 0)
        {
            FrameChoice frame = ShotLabels.FrameOf(shot);
            ShotType current = shot.Type;
            if (Widgets.Choice("Frame", ref frame, frames, f => ShotLabels.Frame(f, current), ShotLabels.FrameHelp, ShotLabels.FrameHelp(frame)))
                changed |= ShotLabels.SetFrame(shot, frame);
        }
        else
        {
            Widgets.Muted("Frame: " + ShotLabels.Frame(FrameChoice.World, shot.Type) + ". " + ShotLabels.FrameHelp(FrameChoice.World));
        }

        double duration = shot.Duration;
        if (Widgets.Num("Duration (s)", ref duration, 0.1f, 0.01, 3600.0, "%.2f", "How long the shot lasts. The next shot may start sooner or later through its own trigger."))
        {
            shot.Duration = duration;
            changed = true;
        }

        Ease ease = shot.Ease;
        if (Widgets.Enum("Ease", ref ease, null, "The shape of the motion over the duration of the shot."))
        {
            shot.Ease = ease;
            changed = true;
        }

        double fov = shot.Fov;
        if (Widgets.Num("Field of view (deg)", ref fov, 0.2f, 1.0, 150.0, "%.1f", "Vertical field of view. A smaller value zooms in."))
        {
            shot.Fov = fov;
            changed = true;
        }

        double roll = shot.Roll;
        if (Widgets.Num("Roll (deg)", ref roll, 0.5f, -180.0, 180.0, "%.1f", "Turns the camera about its view direction. 0 keeps the horizon level; a positive value turns the picture counterclockwise."))
        {
            shot.Roll = roll;
            changed = true;
        }

        ShotUp up = shot.EffectiveUp;
        if (Widgets.Enum("Up", ref up, null, ViewText.FieldTip(ShotField.Up)))
        {
            shot.Up = up;
            changed = true;
        }

        changed |= DrawTypeFields(id, shot);
        changed |= DrawTransition(shot);

        Widgets.Section("Starts");
        changed |= DrawTrigger(id + "-at", shot.At, session.Document);

        ImGui.PopID();
        return changed;
    }

    private static bool DrawTypeFields(string id, Shot shot)
    {
        bool changed = false;
        ShotType type = shot.Type;

        if (ShotFields.Uses(type, ShotField.Part))
        {
            if (Widgets.Text("Part", id + "-part", shot.Part, out string part, 512, ViewText.FieldTip(ShotField.Part)))
            {
                shot.Part = part.Trim();
                changed = true;
            }

        }

        if (ShotFields.Uses(type, ShotField.Center))
            changed |= Vec("Center (m)", shot.Center, v => shot.Center = v, ShotField.Center);
        if (ShotFields.Uses(type, ShotField.Radius))
            changed |= Number("Radius (m)", shot.Radius, 0.2f, type == ShotType.Orbit ? 0.01 : 0.0, 1e7, v => shot.Radius = v, ShotField.Radius);
        if (ShotFields.Uses(type, ShotField.Height))
            changed |= Number("Height (m)", shot.Height, 0.2f, -1e7, 1e7, v => shot.Height = v, ShotField.Height);
        if (ShotFields.Uses(type, ShotField.StartDeg))
            changed |= Number("Start angle (deg)", shot.StartDeg, 0.5f, -1e5, 1e5, v => shot.StartDeg = v, ShotField.StartDeg);
        if (ShotFields.Uses(type, ShotField.DegPerSec))
            changed |= Number("Turn (deg/s)", shot.DegPerSec, 0.1f, -1e5, 1e5, v => shot.DegPerSec = v, ShotField.DegPerSec);
        if (ShotFields.Uses(type, ShotField.From))
            changed |= Number("From height (m)", shot.From, 0.2f, -1e7, 1e7, v => shot.From = v, ShotField.From);
        if (ShotFields.Uses(type, ShotField.To))
            changed |= Number("To height (m)", shot.To, 0.2f, -1e7, 1e7, v => shot.To = v, ShotField.To);
        if (ShotFields.Uses(type, ShotField.Azimuth))
            changed |= Number("Azimuth (deg)", shot.Azimuth, 0.5f, -1e5, 1e5, v => shot.Azimuth = v, ShotField.Azimuth);
        if (ShotFields.Uses(type, ShotField.Position))
            changed |= Vec("Position (m)", shot.Position, v => shot.Position = v, ShotField.Position);
        if (ShotFields.Uses(type, ShotField.Offset))
            changed |= Vec("Offset (m)", shot.Offset, v => shot.Offset = v, ShotField.Offset);
        if (ShotFields.Uses(type, ShotField.LookAt))
            changed |= Vec("Look at (m)", shot.LookAt, v => shot.LookAt = v, ShotField.LookAt);
        if (ShotFields.Uses(type, ShotField.Smoothing))
            changed |= Number("Smoothing (s)", shot.Smoothing, 0.02f, 0.0, 60.0, v => shot.Smoothing = v, ShotField.Smoothing);

        if (ShotFields.Uses(type, ShotField.Points))
            changed |= DrawPoints(id, shot);

        if (ShotFields.Uses(type, ShotField.Subject))
        {
            ShotSubject subject = shot.Subject;
            if (Widgets.Enum("Follows", ref subject, null, ViewText.FieldTip(ShotField.Subject)))
            {
                shot.Subject = subject;
                if (subject == ShotSubject.Separated)
                    shot.Target = ShotTarget.Vehicle;
                changed = true;
            }

            if (shot.Subject == ShotSubject.Separated && ShotFields.Uses(type, ShotField.Pick))
            {
                SeparatedPick pick = shot.Pick;
                if (Widgets.Enum("Pick", ref pick, null, ViewText.FieldTip(ShotField.Pick)))
                {
                    shot.Pick = pick;
                    changed = true;
                }
            }
        }

        if (ShotFields.Uses(type, ShotField.Aim))
        {
            ShotAim aim = shot.Aim;
            if (Widgets.Enum("Aim", ref aim, null, ViewText.FieldTip(ShotField.Aim)))
            {
                shot.Aim = aim;
                changed = true;
            }

            if (shot.Aim == ShotAim.Vehicle)
                changed |= Vec("Aim offset (m)", shot.AimOffset, v => shot.AimOffset = v, ShotField.AimOffset);
        }

        changed |= DrawAvoidSun(shot);

        if (ShotFields.Uses(type, ShotField.Fit))
            changed |= DrawFit(shot);

        if (ShotFields.Uses(type, ShotField.KeepSize))
        {
            bool keep = shot.KeepSize;
            if (Widgets.Check("Keep size", ref keep, ViewText.FieldTip(ShotField.KeepSize)))
            {
                shot.KeepSize = keep;
                changed = true;
            }

        }

        return changed;
    }

    // The sun limit is an angle in degrees, or null when it is off.
    private static bool DrawAvoidSun(Shot shot)
    {
        bool changed = false;
        bool on = shot.AvoidSun.HasValue;
        if (Widgets.Check("Keep away from the sun", ref on, ViewText.FieldTip(ShotField.AvoidSun)))
        {
            OffValues off = Off.GetOrCreateValue(shot);
            if (shot.AvoidSun is double last)
                off.AvoidSun = last;
            shot.AvoidSun = on ? off.AvoidSun : null;
            changed = true;
        }

        if (shot.AvoidSun is double angle)
        {
            if (Widgets.Num("Smallest angle to the sun (deg)", ref angle, 0.5f, 1.0, 90.0, "%.1f", ViewText.FieldTip(ShotField.AvoidSun)))
            {
                shot.AvoidSun = angle;
                changed = true;
            }
        }

        return changed;
    }

    // The fit is a share of the picture height, or null when it is off.
    private static bool DrawFit(Shot shot)
    {
        bool changed = false;
        bool on = shot.Fit.HasValue;
        if (Widgets.Check("Fit the vehicle", ref on, ViewText.FieldTip(ShotField.Fit)))
        {
            OffValues off = Off.GetOrCreateValue(shot);
            if (shot.Fit is double last)
                off.Fit = last;
            shot.Fit = on ? off.Fit : null;
            changed = true;
        }

        if (shot.Fit is double fit)
        {
            if (Widgets.Num("Share of the picture height", ref fit, 0.01f, 0.05, 1.5, "%.2f", ViewText.FieldTip(ShotField.Fit)))
            {
                shot.Fit = fit;
                changed = true;
            }
        }

        return changed;
    }

    private static string _pointOwner = "";
    private static int _pointIndex;

    private static bool DrawPoints(string id, Shot shot)
    {
        bool changed = false;
        if (_pointOwner != id)
        {
            _pointOwner = id;
            _pointIndex = 0;
        }

        List<PathPoint> points = shot.Points;
        _pointIndex = Math.Clamp(_pointIndex, 0, Math.Max(points.Count - 1, 0));
        Widgets.Section("Path points");
        Widgets.Tip(ViewText.FieldTip(ShotField.Points));
        float size = ImGui.GetFontSize();
        float[] columns = { size * 0.5f };
        for (int i = 0; i < points.Count; i++)
        {
            string label = string.Create(CultureInfo.InvariantCulture, $"{i + 1}  at {points[i].T:0.###}");
            if (Widgets.ListRow(id + "-point-" + i, [label], columns, i == _pointIndex))
                _pointIndex = i;
        }

        if (Widgets.Button("ADD POINT##path", true, "Adds a point after the selected one, on the curve between it and the next one."))
        {
            _pointIndex = ShotEdit.AddPoint(shot, _pointIndex);
            changed = true;
        }

        Widgets.SameLine();
        if (Widgets.Button("REMOVE POINT##path", points.Count > 1, points.Count > 1 ? "Removes the selected point." : "A path keeps at least one point.", Widgets.Tone.Danger))
        {
            _pointIndex = ShotEdit.RemovePoint(shot, _pointIndex);
            changed = true;
        }

        Widgets.SameLine();
        if (Widgets.Button("UP##path", _pointIndex > 0, "Moves the selected point one place earlier. The times stay with their places."))
        {
            _pointIndex = ShotEdit.MovePoint(shot, _pointIndex, -1);
            changed = true;
        }

        Widgets.SameLine();
        if (Widgets.Button("DOWN##path", _pointIndex < points.Count - 1, "Moves the selected point one place later. The times stay with their places."))
        {
            _pointIndex = ShotEdit.MovePoint(shot, _pointIndex, 1);
            changed = true;
        }

        if (_pointIndex < 0 || _pointIndex >= points.Count)
            return changed;

        PathPoint point = points[_pointIndex];
        ImGui.PushID($"point{_pointIndex}".AsSpan());
        double low = _pointIndex > 0 ? points[_pointIndex - 1].T + 0.001 : 0.0;
        double high = _pointIndex < points.Count - 1 ? points[_pointIndex + 1].T - 0.001 : 1.0;
        double t = point.T;
        if (Widgets.Num("T (0 to 1)", ref t, 0.005f, low, Math.Max(low, high), "%.3f", "The share of the path at which the camera is at this point."))
        {
            point.T = t;
            changed = true;
        }

        changed |= Vec("Position (m)", point.Position, v => point.Position = v, ShotField.Position);
        changed |= Vec("Look at (m)", point.LookAt, v => point.LookAt = v, ShotField.LookAt);

        bool ownFov = point.Fov.HasValue;
        if (Widgets.Check("Own field of view", ref ownFov, "Off: the point uses the field of view of the shot."))
        {
            point.Fov = ownFov ? shot.Fov : null;
            changed = true;
        }

        if (point.Fov.HasValue)
        {
            double fov = point.Fov.Value;
            if (Widgets.Num("Point field of view (deg)", ref fov, 0.2f, 1.0, 150.0, "%.1f"))
            {
                point.Fov = fov;
                changed = true;
            }
        }

        bool ownRoll = point.Roll.HasValue;
        if (Widgets.Check("Own roll", ref ownRoll, "Off: the point has no roll of its own. The roll of the shot is added in every case."))
        {
            point.Roll = ownRoll ? 0.0 : null;
            changed = true;
        }

        if (point.Roll.HasValue)
        {
            double roll = point.Roll.Value;
            if (Widgets.Num("Point roll (deg)", ref roll, 0.5f, -180.0, 180.0, "%.1f"))
            {
                point.Roll = roll;
                changed = true;
            }
        }

        ImGui.PopID();
        return changed;
    }

    private static bool Vec(string label, V3 current, Action<V3> set, ShotField field)
    {
        if (!Widgets.Vec(label, ref current, 0.5f, ViewText.FieldTip(field)))
            return false;
        set(current);
        return true;
    }

    private static bool Number(string label, double current, float speed, double min, double max, Action<double> set, ShotField field)
    {
        if (!Widgets.Num(label, ref current, speed, min, max, "%.2f", ViewText.FieldTip(field)))
            return false;
        set(current);
        return true;
    }

    private static bool DrawTransition(Shot shot)
    {
        bool changed = false;
        TransitionKind kind = shot.Transition.Kind;
        if (Widgets.Enum("Transition", ref kind, null, "Cut: the camera jumps to the new shot. Blend: it moves over from the last pose."))
        {
            shot.Transition.Kind = kind;
            shot.Transition.Seconds = kind == TransitionKind.Blend
                ? (shot.Transition.Seconds > 0.0 ? shot.Transition.Seconds : Transition.DefaultSeconds)
                : 0.0;
            changed = true;
        }

        if (shot.Transition.Kind == TransitionKind.Blend)
        {
            double seconds = shot.Transition.Seconds;
            if (Widgets.Num("Blend (s)", ref seconds, 0.05f, 0.01, 3600.0, "%.2f", "Seconds that the camera needs to move over to the new shot."))
            {
                shot.Transition.Seconds = seconds;
                changed = true;
            }
        }

        return changed;
    }

    private static bool DrawKeyFields(EditSession session, SpeedKey key)
    {
        bool changed = false;
        string id = $"key-{session.TakeIndex}-{session.KeyIndex}";
        ImGui.PushID(id.AsSpan());

        double to = key.To;
        if (Widgets.Num("Target speed (x)", ref to, 0.01f, 0.0, 1000.0, "%.2f", "The sim speed that the key ramps to. 1 is real time."))
        {
            key.To = to;
            changed = true;
        }


        double ramp = key.Ramp;
        if (Widgets.Num("Ramp (s)", ref ramp, 0.05f, 0.0, 3600.0, "%.2f", "Seconds to go from the speed at the start of the key to the target. 0 jumps."))
        {
            key.Ramp = ramp;
            changed = true;
        }

        Ease ease = key.Ease;
        if (Widgets.Enum("Ease", ref ease, null, "The shape of the speed change over the ramp."))
        {
            key.Ease = ease;
            changed = true;
        }

        Widgets.Section("Starts");
        changed |= DrawTrigger(id + "-at", key.At, session.Document);
        ImGui.PopID();
        return changed;
    }

    private static bool DrawActionFields(EditSession session, TakeAction action)
    {
        bool changed = false;
        string id = $"action-{session.TakeIndex}-{session.ActionIndex}";
        ImGui.PushID(id.AsSpan());

        ActionKind kind = action.Kind;
        if (Widgets.Enum("Action", ref kind))
        {
            action.Kind = kind;
            changed = true;
        }

        if (action.Kind == ActionKind.Stage)
        {
            bool anyNumber = !action.Sequence.HasValue;
            if (Widgets.Check("Any sequence", ref anyNumber))
            {
                action.Sequence = anyNumber ? null : 3;
                changed = true;
            }

            Widgets.Tip("The staging key fires the next sequence in order and cannot pick a number. With a number, the action fires only when that sequence is the next one, and it is skipped (and logged) otherwise.");
            if (action.Sequence.HasValue)
            {
                double number = action.Sequence.Value;
                if (Widgets.Num("Sequence number", ref number, 0.1f, 1.0, 1e6, "%.0f"))
                {
                    action.Sequence = (int)Math.Round(number);
                    changed = true;
                }
            }
        }

        Widgets.Section("Fires");
        changed |= DrawTrigger(id + "-at", action.At, session.Document);
        ImGui.PopID();
        return changed;
    }

    private static bool DrawTrigger(string id, Trigger trigger, ShotFile? file)
    {
        bool changed = false;
        TriggerKind kind = trigger.Kind;
        if (Widgets.Enum("Trigger", ref kind, null, "After: starts when the item before ends. Time: a take time. Event: an event of the vehicle."))
        {
            ShotEdit.ChangeTriggerKind(trigger, kind);
            changed = true;
        }

        switch (trigger.Kind)
        {
            case TriggerKind.Time:
                double time = trigger.Time;
                if (Widgets.Num("Take time (s)", ref time, 0.1f, 0.0, 1e7, "%.2f", "Seconds since the take started. A pause does not count."))
                {
                    trigger.Time = time;
                    changed = true;
                }

                break;
            case TriggerKind.Event:
                if (trigger.Event.Length > 0)
                    Widgets.Muted("Event: " + Widgets.EventName(trigger.Event));
                if (Widgets.Text("Event name", id + "-event", trigger.Event, out string name, 512,
                        "The name of the event as the file stores it. Pick one below, or type for example altitude:5000."))
                {
                    trigger.Event = name.Trim();
                    changed = true;
                }

                changed |= DrawEventPicker(trigger, file);
                double delay = trigger.Delay;
                if (Widgets.Num("Delay (s)", ref delay, 0.05f, -3600.0, 3600.0, "%.2f", "Seconds after the event. A delay below 0 works only for altitude and speed events."))
                {
                    trigger.Delay = delay;
                    changed = true;
                }

                Widgets.Tip("A delay below 0 works only for altitude and speed events.");
                break;
        }

        return changed;
    }

    private static bool DrawEventPicker(Trigger trigger, ShotFile? file)
    {
        bool changed = false;
        if (Widgets.BeginComboRow("Pick event", ""))
        {
            try
            {
                var names = new List<string>(EventNames);
                if (file != null)
                    names.AddRange(file.Events.Keys);
                for (int i = 0; i < names.Count; i++)
                {
                    string name = names[i];
                    ImGui.PushID(i);
                    bool picked = ImGui.Selectable(Widgets.EventName(name).AsSpan(), name == trigger.Event);
                    Widgets.Tip(name);
                    ImGui.PopID();
                    if (!picked)
                        continue;
                    trigger.Event = name;
                    changed = true;
                }
            }
            finally
            {
                Widgets.EndComboRow();
            }
        }

        Widgets.Tip("Type ignition:N, burnout:N, sequence:N, altitude:METERS or speed:METERS_PER_SECOND in the field above for the others. ignition:2 is the second time the engines come on, for example the upper stage in a hot staging.");
        return changed;
    }

    private static readonly string[] EventNames = { "ignition", "ignition:2", "liftoff", "burnout", "burnout:2", "sequence:1", "sequence:2", "sequence:3", "landed", "soi", "apoapsis", "periapsis", "docked", "undocked", "mark" };

    #endregion
}
