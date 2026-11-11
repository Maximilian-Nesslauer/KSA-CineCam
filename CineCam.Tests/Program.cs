using System.Globalization;
using CineCam.Director;

int failures = 0;

void Check(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

void Expect(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

bool Near(double a, double b, double tolerance = 1e-9) => Math.Abs(a - b) <= tolerance;

bool Near3(V3 a, V3 b, double tolerance = 1e-9) => (a - b).Length <= tolerance;

ShotFileException ExpectInvalid(string json)
{
    try
    {
        ShotFileJson.Parse(json);
    }
    catch (ShotFileException ex)
    {
        return ex;
    }

    throw new InvalidOperationException("the file was accepted");
}

Dictionary<string, double> Events(params (string Name, double Time)[] seen) =>
    seen.ToDictionary(e => e.Name, e => e.Time);

Take ParseTake(string json, out ShotFile file)
{
    file = ShotFileJson.Parse(json).File;
    return file.DefaultTake;
}

Shot MakeShot(ShotType type, double duration = 10.0)
{
    var shot = new Shot { Type = type, Duration = duration, Target = Shot.DefaultTarget(type) };
    return shot;
}

#region Shot file

const string minimal = """
{
  // a comment
  "camera": [
    { "type": "tripod", "position": [1, 2, 3], "lookAt": [4, 5, 6], "duration": 5, },
  ],
}
""";

Check("The reader accepts comments and trailing commas and fills the defaults", () =>
{
    Take take = ParseTake(minimal, out ShotFile file);
    Expect(file.Fps == 60.0, "fps default");
    Expect(!file.UsesTakeList && take.Name == "default" && take.IsDefault, "single take form");
    Shot shot = take.Shots.Single();
    Expect(shot.Type == ShotType.Tripod && shot.Target == ShotTarget.Site, "type and default target");
    Expect(shot.Fov == 40.0 && shot.Ease == Ease.None && shot.At.Kind == TriggerKind.After, "defaults");
    Expect(Near3(shot.Position, new V3(1, 2, 3)) && Near3(shot.LookAt, new V3(4, 5, 6)), "vectors");
});

Check("The default shot file parses without a warning and has two takes", () =>
{
    ShotFileReadResult result = DefaultShotFile.Load();
    Expect(result.Warnings.Count == 0, "warnings: " + string.Join("; ", result.Warnings));
    Expect(result.File.Takes.Count == 2, "take count");
    Expect(result.File.DefaultTake.Name == "launch", "default take");
    Expect(result.File.FindTake("ASCENT") != null, "the second take");
    Expect(result.File.Events["fairing"] == "sequence:3", "alias");
    foreach (Take take in result.File.Takes)
    {
        var runner = new TimelineRunner(take, result.File.Events);
        Expect(runner.Update(0.0, Events()).ShotIndex == 0, take.Name + " starts with shot 0");
    }
});

Check("A file round trips through the writer with the same model and the same text", () =>
{
    ShotFile first = DefaultShotFile.Load().File;
    string text = ShotFileJson.Write(first);
    ShotFile second = ShotFileJson.Parse(text).File;
    string again = ShotFileJson.Write(second);
    Expect(text == again, "the second write differs from the first");
    Expect(second.Takes.Count == first.Takes.Count, "take count");
    for (int t = 0; t < first.Takes.Count; t++)
    {
        Expect(second.Takes[t].Shots.Count == first.Takes[t].Shots.Count, "shot count");
        for (int i = 0; i < first.Takes[t].Shots.Count; i++)
        {
            Shot a = first.Takes[t].Shots[i], b = second.Takes[t].Shots[i];
            Expect(a.Type == b.Type && a.Target == b.Target && a.Duration == b.Duration && a.Fov == b.Fov
                && a.Ease == b.Ease && a.Transition.Kind == b.Transition.Kind && a.Transition.Seconds == b.Transition.Seconds
                && a.At.Kind == b.At.Kind && a.At.Event == b.At.Event && a.At.Delay == b.At.Delay, $"shot {t}/{i} fields");
            Expect(a.Offset == b.Offset && a.LookAt == b.LookAt && a.Position == b.Position && a.Radius == b.Radius, $"shot {t}/{i} pose fields");
        }

        Expect(second.Takes[t].Speed.Count == first.Takes[t].Speed.Count, "speed count");
    }

    Expect(text.All(c => c < 128), "the written file is ASCII");
    Expect(text.Contains("\n      \"name\": \"launch\""), "indented, one field per line");
    Expect(text.IndexOf("\"type\"", StringComparison.Ordinal) < text.IndexOf("\"duration\"", StringComparison.Ordinal), "field order");
});

Check("A single take with top level tracks is written in the same form", () =>
{
    ShotFile file = ShotFileJson.Parse(minimal).File;
    string text = ShotFileJson.Write(file);
    Expect(!text.Contains("\"takes\""), "no take list");
    Expect(ShotFileJson.Parse(text).File.DefaultTake.Shots.Single().Position == new V3(1, 2, 3), "position");
});

Check("Unknown fields are kept through a round trip", () =>
{
    const string json = """
    { "note": {"a": [1, 2]}, "camera": [ { "type": "chase", "offset": [1, 2, 3], "duration": 4, "mood": "calm", "radius": 9 } ],
      "speed": [ { "to": 2, "hint": true } ] }
    """;
    ShotFile file = ShotFileJson.Parse(json).File;
    Expect(file.Extras.ContainsKey("note"), "file extra");
    Expect(file.DefaultTake.Shots[0].Extras.ContainsKey("mood"), "shot extra");
    Expect(file.DefaultTake.Shots[0].Extras.ContainsKey("radius"), "a field of another type is an extra");
    string text = ShotFileJson.Write(file);
    ShotFile back = ShotFileJson.Parse(text).File;
    Expect(back.Extras["note"].GetRawText() == "{\"a\": [1, 2]}" || back.Extras["note"].GetRawText().Contains("[1, 2]"), "file extra text");
    Expect(back.DefaultTake.Shots[0].Extras["mood"].GetString() == "calm", "shot extra value");
    Expect(back.DefaultTake.Speed[0].Extras["hint"].GetBoolean(), "speed key extra");
});

Check("Several takes: names, the default mark and lookup", () =>
{
    const string json = """
    { "takes": [
      { "name": "a", "camera": [ { "type": "tripod", "position": [0,0,0], "lookAt": [1,0,0], "duration": 1 } ] },
      { "name": "b", "default": true, "camera": [ { "type": "tripod", "position": [0,0,0], "lookAt": [1,0,0], "duration": 2 } ] } ] }
    """;
    ShotFile file = ShotFileJson.Parse(json).File;
    Expect(file.DefaultTake.Name == "b", "marked default");
    Expect(file.FindTake("A")!.Shots[0].Duration == 1, "lookup ignores case");
    Expect(ShotFileJson.Parse(ShotFileJson.Write(file)).File.DefaultTake.Name == "b", "default survives");

    ShotFile noMark = ShotFileJson.Parse(json.Replace("\"default\": true,", "")).File;
    Expect(noMark.DefaultTake.Name == "a", "first take is the default without a mark");
});

Check("Error paths name the bad field", () =>
{
    string Wrap(string shot) => "{ \"camera\": [ " + shot + " ] }";
    ShotFileException e;

    e = ExpectInvalid("{ not json");
    Expect(e.Message.Contains("not valid JSON"), "syntax: " + e.Message);

    e = ExpectInvalid("{}");
    Expect(e.FieldPath == "camera", "missing camera: " + e.Message);

    e = ExpectInvalid(Wrap("{ \"type\": \"orbit\", \"radius\": 10 }"));
    Expect(e.FieldPath == "camera[0].duration", "missing duration: " + e.FieldPath);

    e = ExpectInvalid(Wrap("{ \"type\": \"dolly\", \"duration\": 3 }"));
    Expect(e.FieldPath == "camera[0].type" && e.Message.Contains("known:"), "bad type: " + e.Message);

    e = ExpectInvalid(Wrap("{ \"type\": \"orbit\", \"radius\": 10, \"duration\": -1 }"));
    Expect(e.FieldPath == "camera[0].duration", "negative duration: " + e.FieldPath);

    e = ExpectInvalid(Wrap("{ \"type\": \"tripod\", \"position\": [1, 2], \"lookAt\": [0,0,0], \"duration\": 1 }"));
    Expect(e.FieldPath == "camera[0].position", "short vector: " + e.FieldPath);

    e = ExpectInvalid(Wrap("{ \"type\": \"tripod\", \"position\": [1, \"x\", 3], \"lookAt\": [0,0,0], \"duration\": 1 }"));
    Expect(e.FieldPath == "camera[0].position[1]", "vector element: " + e.FieldPath);

    e = ExpectInvalid(Wrap("{ \"type\": \"chase\", \"offset\": [1,2,3], \"duration\": 1, \"ease\": \"wobble\" }"));
    Expect(e.FieldPath == "camera[0].ease", "bad ease: " + e.FieldPath);

    e = ExpectInvalid(Wrap("{ \"type\": \"chase\", \"offset\": [1,2,3], \"duration\": 1, \"at\": { \"event\": \"warp\" } }"));
    Expect(e.FieldPath == "camera[0].at.event", "unknown event: " + e.FieldPath);

    e = ExpectInvalid(Wrap("{ \"type\": \"chase\", \"offset\": [1,2,3], \"duration\": 1, \"transition\": \"wipe\" }"));
    Expect(e.FieldPath == "camera[0].transition", "unknown transition: " + e.FieldPath);

    e = ExpectInvalid("{ \"takes\": [ { \"name\": \"a\", \"camera\": [] } ] }");
    Expect(e.FieldPath == "takes[0].camera", "empty track: " + e.FieldPath);

    e = ExpectInvalid("{ \"takes\": [ { \"camera\": [] } ] }");
    Expect(e.FieldPath == "takes[0].name", "take name: " + e.FieldPath);

    e = ExpectInvalid("{ \"events\": { \"x\": \"sequence:0\" }, \"camera\": [] }");
    Expect(e.FieldPath == "events.x", "alias target: " + e.FieldPath);

    e = ExpectInvalid("{ \"camera\": [ { \"type\": \"tripod\", \"position\": [0,0,0], \"lookAt\": [0,0,0], \"duration\": 1 } ], \"speed\": [ { \"to\": 1, \"ramp\": -2 } ] }");
    Expect(e.FieldPath == "speed[0].ramp", "negative ramp: " + e.FieldPath);

    e = ExpectInvalid("{ \"camera\": [ 5 ] }");
    Expect(e.FieldPath == "camera[0]", "shot is not an object: " + e.FieldPath);
});

Check("A negative delay on an event that cannot be predicted is set to 0 with a warning", () =>
{
    const string json = """
    { "camera": [
        { "type": "tripod", "position": [0,0,0], "lookAt": [1,0,0], "duration": 3, "at": { "event": "liftoff", "delay": -2 } },
        { "type": "tripod", "position": [0,0,0], "lookAt": [1,0,0], "duration": 3, "at": { "event": "altitude:100", "delay": -2 } } ] }
    """;
    ShotFileReadResult result = ShotFileJson.Parse(json);
    Expect(result.Warnings.Count == 1 && result.Warnings[0].StartsWith("camera[0].at.delay"), "one warning: " + string.Join("|", result.Warnings));
    Expect(result.File.DefaultTake.Shots[0].At.Delay == 0.0, "clamped");
    Expect(result.File.DefaultTake.Shots[1].At.Delay == -2.0, "altitude keeps the delay");
});

Check("Event names parse, print and resolve aliases", () =>
{
    Expect(EventName.TryParse("Sequence:3", out EventName s, out _) && s.Kind == EventKind.Sequence && s.Canonical == "sequence:3", "sequence");
    Expect(EventName.TryParse("altitude:15000", out EventName a, out _) && a.IsPredictable && a.Canonical == "altitude:15000", "altitude");
    Expect(EventName.TryParse("speed:1500.5", out EventName v, out _) && v.Canonical == "speed:1500.5", "speed");
    Expect(!EventName.TryParse("sequence:x", out _, out _), "bad number");
    Expect(!EventName.TryParse("liftoff:3", out _, out _), "number on a plain event");
    Expect(!EventName.TryParse("", out _, out _), "empty");
    var aliases = new Dictionary<string, string> { ["fairing"] = "sequence:3" };
    Expect(EventName.TryResolve("fairing", aliases, out EventName f, out _) && f.Canonical == "sequence:3", "alias");
    Expect(EventPrediction.SecondsUntil(1000, 3000, 100) == 20.0, "prediction");
    Expect(EventPrediction.SecondsUntil(1000, 3000, 0) is null, "no rate");
    Expect(EventPrediction.SecondsUntil(4000, 3000, 0) == 0.0, "already past");
});

#endregion

#region Timeline

Take TimedTake()
{
    var take = new Take();
    take.Shots.Add(MakeShot(ShotType.Tripod, 5.0));
    Shot second = MakeShot(ShotType.Tripod, 4.0);
    take.Shots.Add(second);
    return take;
}

Check("Shots with the after trigger follow each other and finish", () =>
{
    var runner = new TimelineRunner(TimedTake(), new Dictionary<string, string>());
    var none = Events();
    TimelineState s = runner.Update(0.0, none);
    Expect(s.ShotIndex == 0 && s.LocalTime == 0.0 && s.Progress == 0.0 && s.StartedShots.SequenceEqual(new[] { 0 }), "start");
    s = runner.Update(2.5, none);
    Expect(s.ShotIndex == 0 && Near(s.RawProgress, 0.5) && s.StartedShots.Count == 0, "middle");
    s = runner.Update(5.0, none);
    Expect(s.ShotIndex == 1 && s.StartedShots.SequenceEqual(new[] { 1 }) && !s.Finished, "second shot");
    s = runner.Update(8.9, none);
    Expect(!s.Finished, "not finished");
    s = runner.Update(9.0, none);
    Expect(s.Finished && s.ShotIndex == 1 && s.Progress == 1.0, "finished and holding");
});

Check("A time trigger starts a shot at its take time and the shot before holds", () =>
{
    Take take = TimedTake();
    take.Shots[1].At = Trigger.AtTime(20.0);
    var runner = new TimelineRunner(take, new Dictionary<string, string>());
    TimelineState s = runner.Update(12.0, Events());
    Expect(s.ShotIndex == 0 && s.Progress == 1.0 && s.LocalTime == 5.0, "first shot holds its last pose");
    s = runner.Update(21.0, Events());
    Expect(s.ShotIndex == 1 && Near(s.LocalTime, 1.0), "second shot");
});

Check("An event trigger waits for the event, adds the delay and stays fixed", () =>
{
    Take take = TimedTake();
    take.Shots[1].At = Trigger.OnEvent("liftoff", 2.0);
    var runner = new TimelineRunner(take, new Dictionary<string, string>());
    Expect(runner.Update(30.0, Events()).ShotIndex == 0, "no event, the first shot holds");
    Expect(runner.ShotStart(1) is null, "unresolved");
    TimelineState s = runner.Update(40.0, Events(("liftoff", 38.0)));
    Expect(s.ShotIndex == 1 && Near(s.LocalTime, 0.0), "start at event time plus delay");
    s = runner.Update(41.0, Events(("liftoff", 99.0)));
    Expect(Near(runner.ShotStart(1)!.Value, 40.0) && Near(s.LocalTime, 1.0), "the start does not move");
});

Check("An alias resolves to its event", () =>
{
    Take take = TimedTake();
    take.Shots[1].At = Trigger.OnEvent("fairing");
    var aliases = new Dictionary<string, string> { ["fairing"] = "sequence:3" };
    var runner = new TimelineRunner(take, aliases);
    Expect(runner.Update(6.0, Events(("sequence:2", 1.0))).ShotIndex == 0, "another sequence does not start it");
    Expect(runner.Update(7.0, Events(("sequence:3", 6.5))).ShotIndex == 1, "sequence 3 does");
});

Check("A negative delay on an altitude event starts the shot by prediction", () =>
{
    Take take = TimedTake();
    take.Shots[1].At = Trigger.OnEvent("altitude:15000", -2.0);
    var runner = new TimelineRunner(take, new Dictionary<string, string>());
    var predictions = new Dictionary<string, double> { ["altitude:15000"] = 5.0 };
    Expect(runner.Update(10.0, Events(), predictions).ShotIndex == 0, "5 s to go, 2 s early: not yet");
    predictions["altitude:15000"] = 2.5;
    Expect(runner.Update(12.5, Events(), predictions).ShotIndex == 0, "2.5 s to go: not yet");
    predictions["altitude:15000"] = 2.0;
    TimelineState s = runner.Update(13.0, Events(), predictions);
    Expect(s.ShotIndex == 1 && Near(runner.ShotStart(1)!.Value, 13.0), "2 s to go: start");
    predictions["altitude:15000"] = 9.0;
    s = runner.Update(14.0, Events(), predictions);
    Expect(s.ShotIndex == 1 && Near(s.LocalTime, 1.0), "a later prediction does not move the start");
});

Check("A negative delay starts at once when the event comes before the prediction", () =>
{
    Take take = TimedTake();
    take.Shots[1].At = Trigger.OnEvent("altitude:15000", -2.0);
    var runner = new TimelineRunner(take, new Dictionary<string, string>());
    TimelineState s = runner.Update(20.0, Events(("altitude:15000", 19.9)));
    Expect(s.ShotIndex == 1 && Near(runner.ShotStart(1)!.Value, 20.0), "start now");
});

Check("A blend gives a weight from 0 to 1 and keeps the previous shot", () =>
{
    Take take = TimedTake();
    take.Shots[1].Transition = new Transition { Kind = TransitionKind.Blend, Seconds = 2.0 };
    take.Shots[0].Ease = Ease.None;
    var runner = new TimelineRunner(take, new Dictionary<string, string>());
    runner.Update(0.0, Events());
    TimelineState s = runner.Update(5.0, Events());
    Expect(s.ShotIndex == 1 && s.PreviousShotIndex == 0 && Near(s.BlendWeight, 0.0), "weight 0 at the start");
    Expect(Near(s.PreviousProgress, 1.0), "the previous shot holds its end");
    s = runner.Update(6.0, Events());
    Expect(Near(s.BlendWeight, 0.5), "weight 0.5 in the middle");
    s = runner.Update(7.0, Events());
    Expect(s.PreviousShotIndex == -1 && Near(s.BlendWeight, 1.0), "weight 1 after the blend");
});

Check("A cut has weight 1 and no previous shot", () =>
{
    var runner = new TimelineRunner(TimedTake(), new Dictionary<string, string>());
    runner.Update(0.0, Events());
    TimelineState s = runner.Update(5.0, Events());
    Expect(s.PreviousShotIndex == -1 && s.BlendWeight == 1.0, "cut");
});

Check("Easing keeps the ends and the shot progress follows it", () =>
{
    foreach (Ease ease in Enum.GetValues<Ease>())
    {
        Expect(Easing.Apply(ease, 0.0) == 0.0 && Easing.Apply(ease, 1.0) == 1.0, ease + " ends");
        Expect(Easing.Apply(ease, -3.0) == 0.0 && Easing.Apply(ease, 3.0) == 1.0, ease + " clamps");
    }

    Expect(Near(Easing.Apply(Ease.In, 0.5), 0.25) && Near(Easing.Apply(Ease.Out, 0.5), 0.75) && Near(Easing.Apply(Ease.InOut, 0.5), 0.5), "midpoints");
    Take take = TimedTake();
    take.Shots[0].Ease = Ease.In;
    var runner = new TimelineRunner(take, new Dictionary<string, string>());
    Expect(Near(runner.Update(2.5, Events()).Progress, 0.25), "eased progress");
});

Check("The speed track ramps between keys and starts at 1", () =>
{
    Take take = TimedTake();
    take.Speed.Add(new SpeedKey { At = Trigger.AtTime(2.0), To = 0.25, Ramp = 2.0, Ease = Ease.None });
    take.Speed.Add(new SpeedKey { At = Trigger.OnEvent("liftoff", 1.0), To = 4.0, Ramp = 0.0 });
    var runner = new TimelineRunner(take, new Dictionary<string, string>());
    Expect(Near(runner.Update(1.0, Events()).TargetSpeed, 1.0), "before the first key");
    Expect(Near(runner.Update(3.0, Events()).TargetSpeed, 0.625), "halfway through the ramp");
    Expect(Near(runner.Update(4.5, Events()).TargetSpeed, 0.25), "end of the ramp");
    Expect(Near(runner.Update(9.0, Events(("liftoff", 8.0))).TargetSpeed, 4.0), "an immediate key");
});

Check("A speed key that starts during a ramp continues from the partial value", () =>
{
    Take take = TimedTake();
    take.Speed.Add(new SpeedKey { At = Trigger.AtTime(0.0), To = 3.0, Ramp = 4.0, Ease = Ease.None });
    take.Speed.Add(new SpeedKey { At = Trigger.AtTime(2.0), To = 1.0, Ramp = 2.0, Ease = Ease.None });
    var runner = new TimelineRunner(take, new Dictionary<string, string>());
    Expect(Near(runner.Update(2.0, Events()).TargetSpeed, 2.0), "at the second start the speed is 2");
    Expect(Near(runner.Update(3.0, Events()).TargetSpeed, 1.5), "halfway down");
    Expect(Near(runner.Update(5.0, Events()).TargetSpeed, 1.0), "done");
});

Check("A key with the after trigger starts when the key before has finished its ramp", () =>
{
    Take take = TimedTake();
    take.Speed.Add(new SpeedKey { At = Trigger.AtTime(1.0), To = 2.0, Ramp = 3.0 });
    take.Speed.Add(new SpeedKey { To = 1.0, Ramp = 1.0 });
    var runner = new TimelineRunner(take, new Dictionary<string, string>());
    runner.Update(0.5, Events());
    Expect(Near(runner.SpeedKeyStart(1)!.Value, 4.0), "key 1 follows the ramp of key 0");
    runner.Update(1.0, Events());
    Expect(Near(runner.SpeedKeyStart(1)!.Value, 4.0), "second key starts at 4");
});

Check("Before the first event shot no shot is active", () =>
{
    var take = new Take();
    Shot shot = MakeShot(ShotType.Tripod, 3.0);
    shot.At = Trigger.OnEvent("ignition");
    take.Shots.Add(shot);
    var runner = new TimelineRunner(take, new Dictionary<string, string>());
    Expect(runner.Update(5.0, Events()).ShotIndex == -1, "no shot");
    TimelineState s = runner.Update(8.0, Events(("ignition", 6.0)));
    Expect(s.ShotIndex == 0 && Near(s.LocalTime, 2.0), "running after the event");
    Expect(runner.Update(9.0, Events(("ignition", 6.0))).Finished, "finished");
});

#endregion

#region Pose math

Check("pose: roll turns the up direction about the view and survives a round trip", () =>
{
    var shot = MakeShot(ShotType.Tripod);
    shot.Position = new V3(0, -10, 0);
    shot.LookAt = new V3(0, 0, 0);
    shot.Roll = 90.0;
    CameraPose pose = PoseMath.Evaluate(shot, 0.5, new PoseInputs());
    V3 forward = new V3(0, 1, 0);
    Expect(Math.Abs(V3.Dot(pose.Up, forward)) < 1e-9, "the rolled up stays perpendicular to the view");
    Expect(Math.Abs(V3.Dot(pose.Up, V3.UnitX)) < 1e-9, "90 deg turns the up away from the level up");
    shot.Roll = 0.0;
    Expect(Near3(PoseMath.Evaluate(shot, 0.5, new PoseInputs()).Up, V3.UnitX), "no roll keeps the level up");
    const string json = "{ \"camera\": [ { \"type\": \"tripod\", \"position\": [5,0,0], \"lookAt\": [6,1,0], \"duration\": 2, \"roll\": -15 } ] }";
    ShotFile again = ShotFileJson.Parse(ShotFileJson.Write(ShotFileJson.Parse(json).File)).File;
    Expect(Near(again.DefaultTake.Shots[0].Roll, -15.0), "the roll is read and written");
});

Check("Orbit: angle sweep, height and radius", () =>
{
    Shot shot = MakeShot(ShotType.Orbit, 10.0);
    shot.Radius = 100; shot.Height = 20; shot.StartDeg = 0; shot.DegPerSec = 9; shot.LookAt = new V3(30, 0, 0);
    var inputs = new PoseInputs();
    CameraPose p0 = PoseMath.Evaluate(shot, 0.0, inputs);
    CameraPose p1 = PoseMath.Evaluate(shot, 1.0, inputs);
    Expect(p0.Frame == PoseFrame.Site, "frame");
    Expect(Near3(p0.Position, new V3(20, 0, 100)), "start at north: " + p0.Position);
    double a = 90.0 * Math.PI / 180.0;
    Expect(Near3(p1.Position, new V3(20, 100 * Math.Sin(a), 100 * Math.Cos(a)), 1e-9), "end after 90 degrees: " + p1.Position);
    Expect(Near3(p1.LookAt, new V3(30, 0, 0)) && Near(p0.Fov, 40.0), "look at and fov");
    shot.Target = ShotTarget.Vehicle;
    Expect(PoseMath.Evaluate(shot, 0.5, inputs).Frame == PoseFrame.Vehicle, "vehicle target gives a vehicle frame pose");
});

Check("Crane: height from to along the axis, the look point is at the camera height", () =>
{
    Shot shot = MakeShot(ShotType.Crane, 6.0);
    shot.Target = ShotTarget.Vehicle;
    shot.From = 0; shot.To = 38; shot.Radius = 14; shot.Azimuth = 0;
    var inputs = new PoseInputs();
    CameraPose p0 = PoseMath.Evaluate(shot, 0.0, inputs);
    CameraPose p1 = PoseMath.Evaluate(shot, 1.0, inputs);
    Expect(Near3(p0.Position, new V3(0, 0, 14)) && Near3(p0.LookAt, V3.Zero), "start");
    Expect(Near3(p1.Position, new V3(38, 0, 14)) && Near3(p1.LookAt, new V3(38, 0, 0)), "end");
    Expect(p1.Frame == PoseFrame.Vehicle, "frame");
});

Check("Tripod: fixed pose", () =>
{
    Shot shot = MakeShot(ShotType.Tripod);
    shot.Position = new V3(3, -60, -45); shot.LookAt = new V3(15, 0, 0); shot.Fov = 30;
    var inputs = new PoseInputs();
    CameraPose p0 = PoseMath.Evaluate(shot, 0.0, inputs), p1 = PoseMath.Evaluate(shot, 1.0, inputs);
    Expect(p0 == p1 && p0.Position == shot.Position && p0.LookAt == shot.LookAt && p0.Fov == 30, "same at both ends");
});

Check("Track: looks at the middle of the vehicle and can keep its size", () =>
{
    Shot shot = MakeShot(ShotType.Track);
    shot.Position = new V3(0, -100, 0); shot.Fov = 20;
    var inputs = new PoseInputs
    {
        Vehicle = new Basis3(new V3(0, 0, 0), V3.UnitX, V3.UnitY, V3.UnitZ),
        VehicleHeight = 40,
    };
    CameraPose p = PoseMath.Evaluate(shot, 0.0, inputs);
    Expect(p.Frame == PoseFrame.Site && Near3(p.LookAt, new V3(20, 0, 0)), "look at the middle");
    Expect(Near(p.Fov, 20.0), "no zoom without keepSize");

    shot.KeepSize = true;
    inputs.TrackReferenceDistance = (new V3(20, 0, 0) - shot.Position).Length;
    Expect(Near(PoseMath.Evaluate(shot, 0.0, inputs).Fov, 20.0, 1e-9), "same distance, same fov");
    inputs.Vehicle = new Basis3(new V3(1000, 0, 0), V3.UnitX, V3.UnitY, V3.UnitZ);
    CameraPose far = PoseMath.Evaluate(shot, 1.0, inputs);
    Expect(far.Fov < 20.0 && far.Fov > 0.5, "a farther vehicle narrows the fov: " + far.Fov);
});

Check("Chase and part: vehicle frame poses, part relative to the part position", () =>
{
    Shot chase = MakeShot(ShotType.Chase);
    chase.Offset = new V3(-25, 8, -12); chase.LookAt = new V3(10, 0, 0);
    var inputs = new PoseInputs { PartPosition = new V3(5, 1, 0) };
    CameraPose c0 = PoseMath.Evaluate(chase, 0.0, inputs), c1 = PoseMath.Evaluate(chase, 1.0, inputs);
    Expect(c0 == c1 && c0.Frame == PoseFrame.Vehicle && c0.Position == chase.Offset && c0.LookAt == chase.LookAt, "chase is constant");

    Shot part = MakeShot(ShotType.Part);
    part.Part = "EngineBay"; part.Offset = new V3(-6, 4, -4); part.LookAt = V3.Zero;
    CameraPose p = PoseMath.Evaluate(part, 0.0, inputs);
    Expect(Near3(p.Position, new V3(-1, 5, -4)) && Near3(p.LookAt, new V3(5, 1, 0)), "relative to the part");
    CameraPose none = PoseMath.Evaluate(part, 1.0, new PoseInputs());
    Expect(Near3(none.Position, part.Offset), "without the part the vehicle origin is used");
});

Check("A vehicle frame pose maps to the site frame", () =>
{
    // Nose along site +Y (east), vehicle +Y along site +X (up).
    var vehicle = new Basis3(new V3(10, 0, 0), V3.UnitY, V3.UnitX, new V3(0, 0, -1));
    var pose = new CameraPose(PoseFrame.Vehicle, new V3(1, 2, 3), new V3(0, 0, 0), V3.UnitX, 40);
    CameraPose site = PoseMath.ToSiteFrame(pose, vehicle);
    Expect(site.Frame == PoseFrame.Site && Near3(site.Position, new V3(12, 1, -3)), "position " + site.Position);
    Expect(Near3(site.LookAt, new V3(10, 0, 0)) && Near3(site.Up, V3.UnitY), "look at and up");
});

Check("Blend: the ends return the inputs and the middle turns the look direction", () =>
{
    var a = new CameraPose(PoseFrame.Site, new V3(0, 0, 0), new V3(0, 10, 0), V3.UnitX, 30);
    var b = new CameraPose(PoseFrame.Site, new V3(0, 10, 0), new V3(0, 10, 10), V3.UnitX, 50);
    CameraPose start = PoseMath.Blend(a, b, 0.0), end = PoseMath.Blend(a, b, 1.0), mid = PoseMath.Blend(a, b, 0.5);
    Expect(Near3(start.Position, a.Position) && Near3(start.LookAt, a.LookAt) && Near(start.Fov, 30), "weight 0");
    Expect(Near3(end.Position, b.Position) && Near3(end.LookAt, b.LookAt) && Near(end.Fov, 50), "weight 1");
    Expect(Near3(mid.Position, new V3(0, 5, 0)) && Near(mid.Fov, 40), "middle position and fov");
    V3 look = (mid.LookAt - mid.Position).NormalizedOr(V3.Zero);
    double s = Math.Sqrt(0.5);
    Expect(Near3(look, new V3(0, s, s), 1e-9), "middle look direction " + look);
    Expect(Near((mid.LookAt - mid.Position).Length, Math.Sqrt(50.0)), "look distance follows the blended look point");

    var opposite = new CameraPose(PoseFrame.Site, V3.Zero, new V3(0, -10, 0), V3.UnitX, 30);
    CameraPose turned = PoseMath.Blend(a, opposite, 0.5);
    Expect(!double.IsNaN(turned.LookAt.X) && Near((turned.LookAt - turned.Position).Length, 10.0), "opposite looks stay defined");

    bool threw = false;
    try { PoseMath.Blend(a, a with { Frame = PoseFrame.Vehicle }, 0.5); } catch (ArgumentException) { threw = true; }
    Expect(threw, "frames must match");
});

Check("The spring approaches the target without overshoot and the basis smoother follows a turn", () =>
{
    double velocity = 0.0, value = 0.0;
    double max = 0.0;
    for (int i = 0; i < 600; i++)
    {
        value = Spring.SmoothDamp(value, 1.0, ref velocity, 0.5, 1.0 / 60.0);
        max = Math.Max(max, value);
    }

    Expect(max <= 1.0 + 1e-9 && Near(value, 1.0, 1e-4), "no overshoot, converged: " + value);
    double v2 = 5.0;
    Expect(Spring.SmoothDamp(0.0, 3.0, ref v2, 0.0, 0.016) == 3.0 && v2 == 0.0, "smooth time 0 jumps");

    var smoother = new BasisSmoother();
    Basis3 first = smoother.Update(Basis3.Identity, 1.0, 0.016);
    Expect(first == Basis3.Identity, "the first call returns the target");
    var turned = new Basis3(new V3(5, 5, 5), V3.UnitY, -V3.UnitX, V3.UnitZ);
    Basis3 step = smoother.Update(turned, 1.0, 0.016);
    Expect(step.Origin == turned.Origin, "the origin is not smoothed");
    Expect(V3.Dot(step.X, V3.UnitX) > 0.9 && V3.Dot(step.X, V3.UnitY) > 0.0, "the axis turns a little");
    Expect(Near(step.X.Length, 1.0) && Near(V3.Dot(step.X, step.Y), 0.0, 1e-9) && Near3(step.Z, V3.Cross(step.X, step.Y)), "orthonormal");
    for (int i = 0; i < 600; i++)
        step = smoother.Update(turned, 1.0, 0.016);
    Expect(Near3(step.X, V3.UnitY, 1e-3), "converges");
});

#endregion

#region Event log

Check("CSV: header, invariant numbers, escaping and flush on each line", () =>
{
    CultureInfo old = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        using var inner = new StringWriter();
        var flushing = new FlushCounter(inner);
        using var log = new EventLogCsv(flushing);
        log.Write(1.5, 100.25, -3.0, 0.25, -1, "event", "liftoff", "plain");
        log.Write(2.0, 101.0, -2.0, 1.0, 42, "shot", "a,b", "say \"hi\"\nnext");
        string[] lines = inner.ToString().Split('\n');
        Expect(lines[0] == EventLogCsv.Header, "header");
        Expect(lines[1] == "1.500,100.250,-3.000,0.25,,event,liftoff,plain", "line 1: " + lines[1]);
        Expect(lines[2] == "2.000,101.000,-2.000,1,42,shot,\"a,b\",\"say \"\"hi\"\"", "line 2: " + lines[2]);
        Expect(flushing.Flushes >= 3, "flushed " + flushing.Flushes + " times");
    }
    finally
    {
        CultureInfo.CurrentCulture = old;
    }

    Expect(EventLogCsv.Escape("plain") == "plain" && EventLogCsv.Escape("a\"b") == "\"a\"\"b\"", "escape");
});

Check("CSV file log is readable while open and complete after a write", () =>
{
    string path = Path.Combine(Path.GetTempPath(), "cinecam-" + Guid.NewGuid().ToString("N") + ".csv");
    try
    {
        using (var log = EventLogCsv.Create(path))
        {
            log.Write(0, 0, 0, 1, -1, "take", "start");
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            string text = reader.ReadToEnd();
            Expect(text.Contains("take,start"), "the line is on disk before the log closes");
        }
    }
    finally
    {
        File.Delete(path);
    }
});

#endregion

#region Event detector

Check("json: hideUiAt takes an event or an alias and survives a round trip", () =>
{
    const string json = "{ \"hideUi\": true, \"hideUiAt\": \"go\", \"events\": { \"go\": \"ignition\" }, \"camera\": [ { \"type\": \"tripod\", \"position\": [5,0,0], \"lookAt\": [6,1,0], \"duration\": 2 } ] }";
    ShotFile file = ShotFileJson.Parse(json).File;
    Expect(file.HideUiAt == "go", "the alias is kept");
    ShotFile again = ShotFileJson.Parse(ShotFileJson.Write(file)).File;
    Expect(again.HideUiAt == "go" && again.HideUi, "round trip");
    const string perTake = "{ \"hideUi\": true, \"hideUiAt\": \"ignition\", \"takes\": [ { \"name\": \"pad\", \"hideUiAt\": \"start\", \"camera\": [ { \"type\": \"tripod\", \"position\": [5,0,0], \"lookAt\": [6,1,0], \"duration\": 2 } ] }, { \"name\": \"flight\", \"camera\": [ { \"type\": \"tripod\", \"position\": [5,0,0], \"lookAt\": [6,1,0], \"duration\": 2 } ] } ] }";
    ShotFile split = ShotFileJson.Parse(ShotFileJson.Write(ShotFileJson.Parse(perTake).File)).File;
    Expect(ShotFile.IsHideUiAtStart(split.HideUiAtFor(split.Takes[0])), "the pad take hides at its start");
    Expect(split.HideUiAtFor(split.Takes[1]) == "ignition", "the flight take uses the file value");
    ShotFileException bad = ExpectInvalid("{ \"hideUi\": true, \"hideUiAt\": \"nope\", \"camera\": [ { \"type\": \"tripod\", \"position\": [5,0,0], \"lookAt\": [6,1,0], \"duration\": 2 } ] }");
    Expect(bad.FieldPath == "hideUiAt", "the bad field is named: " + bad.FieldPath);
});

VehicleSample Sample(bool engine = false, bool contact = true, int sequence = 0, double altitude = 0, double speed = 0) =>
    new(engine, contact, sequence, altitude, speed);

Check("detector: ignition, liftoff and burnout come once and in order", () =>
{
    var detector = new EventDetector(Array.Empty<EventName>());
    Expect(detector.Update(0.0, Sample()).Count == 0, "no event on the pad");
    var first = detector.Update(1.0, Sample(engine: true));
    Expect(first.Count == 1 && first[0].Name == "ignition", "ignition");
    var second = detector.Update(2.0, Sample(engine: true, contact: false, altitude: 1.0));
    Expect(second.Count == 1 && second[0].Name == "liftoff", "liftoff");
    Expect(detector.Update(3.0, Sample(engine: true, contact: false, altitude: 5.0)).Count == 0, "nothing new");
    var end = detector.Update(4.0, Sample(engine: false, contact: false, altitude: 9.0));
    Expect(end.Count == 1 && end[0].Name == "burnout", "burnout");
    Expect(Near(detector.Seen["liftoff"], 2.0), "the time of the event is kept");
    var again = detector.Update(5.0, Sample(engine: true, contact: false));
    Expect(again.Count == 1 && again[0].Name == "ignition:2", "a second ignition is counted");
    Expect(detector.Seen.Count(p => p.Key.StartsWith("ignition", StringComparison.Ordinal)) == 2, "each ignition is kept once");
});

Check("detector: a contact flicker on the pad is not a liftoff", () =>
{
    var detector = new EventDetector(Array.Empty<EventName>());
    detector.Update(0.0, Sample(altitude: 26.5));
    Expect(detector.Update(0.1, Sample(contact: false, altitude: 26.52)).Count == 0, "a lost contact without a rise");
    detector.Update(0.2, Sample(altitude: 26.5));
    var up = detector.Update(1.0, Sample(engine: true, contact: false, altitude: 27.2));
    Expect(up.Any(e => e.Name == "liftoff"), "a rise of 0.7 m is a liftoff");
});

Check("detector: a short engine blip is not a burnout", () =>
{
    var detector = new EventDetector(Array.Empty<EventName>());
    detector.Update(0.0, Sample());
    detector.Update(1.0, Sample(engine: true));
    var off = detector.Update(1.4, Sample(engine: false));
    Expect(off.Count == 0, "no burnout after 0.4 s");
});

Check("detector: a take that starts in flight sees the past events at once", () =>
{
    var detector = new EventDetector(Array.Empty<EventName>());
    var found = detector.Update(0.0, Sample(engine: true, contact: false));
    Expect(found.Select(e => e.Name).OrderBy(n => n).SequenceEqual(new[] { "ignition", "liftoff" }), "both events at once");
    Expect(found.All(e => e.Detail.Length > 0), "the detail says why");
});

Check("detector: a skipped sequence number reports each number", () =>
{
    var detector = new EventDetector(Array.Empty<EventName>());
    detector.Update(0.0, Sample(sequence: 1));
    var found = detector.Update(1.0, Sample(sequence: 4));
    Expect(found.Select(e => e.Name).SequenceEqual(new[] { "sequence:2", "sequence:3", "sequence:4" }), "2, 3 and 4");
    Expect(detector.Update(2.0, Sample(sequence: 4)).Count == 0, "no repeat");
});

Check("detector: altitude and speed give a prediction and then the event", () =>
{
    var watched = new[] { new EventName(EventKind.Altitude, 1000), new EventName(EventKind.Speed, 500) };
    var detector = new EventDetector(watched);
    for (int i = 0; i <= 99; i++)
        detector.Update(i * 0.1, Sample(altitude: i * 10.0, speed: i * 3.0));

    Expect(detector.Predictions.TryGetValue("altitude:1000", out double seconds), "altitude prediction");
    Expect(seconds is > 0.0 and < 1.5, "close to the real 1.0 s left: " + seconds);
    Expect(!detector.Seen.ContainsKey("altitude:1000"), "not reached yet");
    var found = detector.Update(10.1, Sample(altitude: 1005, speed: 303));
    Expect(found.Any(e => e.Name == "altitude:1000"), "altitude reached");
    Expect(!detector.Predictions.ContainsKey("altitude:1000"), "the prediction is dropped");
    Expect(detector.Predictions.ContainsKey("speed:500"), "speed prediction stays");
});

Check("detector: no prediction when the value does not rise", () =>
{
    var detector = new EventDetector(new[] { new EventName(EventKind.Altitude, 1000) });
    for (int i = 0; i < 10; i++)
        detector.Update(i * 0.1, Sample(altitude: 5));
    Expect(!detector.Predictions.ContainsKey("altitude:1000"), "no prediction");
});

Check("detector: watched events come from the triggers and aliases", () =>
{
    Take take = ParseTake("""
    { "events": { "stage": "sequence:2" },
      "camera": [ { "type": "tripod", "position": [1,2,3], "lookAt": [0,0,0], "duration": 5, "at": { "event": "stage" } },
                  { "type": "tripod", "position": [1,2,3], "lookAt": [0,0,0], "duration": 5, "at": { "event": "altitude:900" } } ],
      "speed": [ { "at": { "event": "speed:300" }, "to": 2, "ramp": 1 } ] }
    """, out ShotFile file);
    var watched = EventDetector.Watched(take, file.Events).Select(n => n.Canonical).ToList();
    Expect(watched.SequenceEqual(new[] { "sequence:2", "altitude:900", "speed:300" }), string.Join(",", watched));
});

#endregion

#region Take director

WorldSample World(VehicleSample sample, double x = 0.0) => new()
{
    Vehicle = sample,
    VehicleFrame = new Basis3(new V3(x, 0, 0), V3.UnitX, V3.UnitY, V3.UnitZ),
    VehicleHeight = 40.0,
};

const string DirectorFile = """
{
  "hideUi": true,
  "syncFlash": true,
  "camera": [
    { "type": "tripod", "position": [3, -60, -45], "lookAt": [15, 0, 0], "duration": 5, "fov": 30 },
    { "type": "tripod", "position": [3, 60, 45], "lookAt": [15, 0, 0], "duration": 5, "fov": 50, "at": { "event": "ignition" } }
  ],
  "speed": [ { "at": { "event": "ignition" }, "to": 0.25, "ramp": 0.5 } ]
}
""";

Check("director: the first shot plays and the second waits for ignition", () =>
{
    ShotFile file = ShotFileJson.Parse(DirectorFile).File;
    Expect(file.HideUi && file.SyncFlash, "file flags are read");
    var director = new TakeDirector(file.DefaultTake, file);
    var f0 = director.Update(0.0, World(Sample()));
    Expect(f0.Pose.HasValue && f0.Pose.Value.Frame == PoseFrame.Site, "pose in the site frame");
    Expect(f0.ShotIndex == 0 && Near(f0.Pose!.Value.Fov, 30), "first shot");
    Expect(!f0.SpeedActive, "the speed is left alone before the first key");
    var f1 = director.Update(7.0, World(Sample()));
    Expect(f1.ShotIndex == 0 && !f1.Finished, "the first shot holds and the take waits");
    var f2 = director.Update(8.0, World(Sample(engine: true)));
    Expect(f2.ShotIndex == 1 && f2.NewEvents.Any(e => e.Name == "ignition"), "second shot on ignition");
    Expect(f2.SpeedActive && f2.StartedSpeedKeys.Contains(0), "speed key started");
    var f3 = director.Update(8.6, World(Sample(engine: true)));
    Expect(Near(f3.TargetSpeed, 0.25, 1e-6), "speed ramp done: " + f3.TargetSpeed);
    var f4 = director.Update(13.1, World(Sample(engine: true)));
    Expect(f4.Finished, "finished after the last shot");
});

Check("director: a chase shot follows the vehicle frame", () =>
{
    ShotFile file = ShotFileJson.Parse("""
    { "camera": [ { "type": "chase", "offset": [-10, 5, 0], "lookAt": [0, 0, 0], "duration": 5, "smoothing": 0 } ] }
    """).File;
    var director = new TakeDirector(file.DefaultTake, file);
    var pose = director.Update(0.0, World(Sample(), x: 100.0)).Pose!.Value;
    Expect(Near3(pose.Position, new V3(90, 5, 0)), "offset behind the base: " + pose.Position);
    Expect(Near3(pose.LookAt, new V3(100, 0, 0)), "look at the base: " + pose.LookAt);
    Expect(Near3(pose.Up, V3.UnitX), "up is the vehicle axis");
});

Check("director: a part shot warns once when the part is missing", () =>
{
    ShotFile file = ShotFileJson.Parse("""
    { "camera": [ { "type": "part", "part": "Engine", "offset": [1, 1, 1], "duration": 5 } ] }
    """).File;
    var director = new TakeDirector(file.DefaultTake, file);
    var world = World(Sample());
    Expect(director.Update(0.0, world).Warnings.Count == 1, "one warning");
    Expect(director.Update(0.1, world).Warnings.Count == 0, "not again");
    world.PartLookup = id => id == "Engine" ? new V3(5, 0, 0) : null;
    var pose = director.Update(0.2, world).Pose!.Value;
    Expect(Near3(pose.Position, new V3(6, 1, 1)), "relative to the part: " + pose.Position);
});

Check("director: a negative delay on an altitude event uses the prediction", () =>
{
    ShotFile file = ShotFileJson.Parse("""
    { "camera": [ { "type": "tripod", "position": [1,2,3], "lookAt": [0,0,0], "duration": 100, "fov": 30 },
                  { "type": "tripod", "position": [4,5,6], "lookAt": [0,0,0], "duration": 5, "fov": 50, "at": { "event": "altitude:1000", "delay": -2 } } ] }
    """).File;
    var director = new TakeDirector(file.DefaultTake, file);
    int shotAt = -1;
    for (int i = 0; i <= 100; i++)
    {
        var frame = director.Update(i * 0.1, World(Sample(altitude: i * 10.0)));
        if (frame.ShotIndex == 1 && shotAt < 0)
            shotAt = i;
    }

    Expect(shotAt > 70 && shotAt < 100, "the second shot started about 2 s before the altitude: frame " + shotAt);
});

Check("shot file: hideUi and syncFlash are written back", () =>
{
    ShotFile file = ShotFileJson.Parse(DirectorFile).File;
    ShotFile again = ShotFileJson.Parse(ShotFileJson.Write(file)).File;
    Expect(again.HideUi && again.SyncFlash, "both flags survive");
    Expect(!ShotFileJson.Parse(DefaultShotFile.Json).File.HideUi, "off by default");
});

#endregion


#region Autotest

Check("autotest take A parses and builds a director", () =>
{
    ShotFile file = ShotFileJson.Parse(AutotestPlan.TakeAJson(30.0, "Engine \"One\"")).File;
    Take take = file.FindTake(AutotestPlan.TakeAName)!;
    Expect(take.Shots.Count == AutotestPlan.ShotNames.Length, "six shots");
    for (int i = 0; i < take.Shots.Count; i++)
        Expect(string.Equals(take.Shots[i].Type.ToString(), AutotestPlan.ShotNames[i], StringComparison.OrdinalIgnoreCase), "shot type " + i);
    Expect(take.Shots[5].Part == "Engine \"One\"", "part id kept");
    Expect(take.Shots[4].Transition.Kind == TransitionKind.Blend, "one blend");
    Expect(take.Speed.Count == 2, "two speed keys");
    _ = new TakeDirector(take, file);
});

Check("autotest take B parses with offline capture", () =>
{
    ShotFile file = ShotFileJson.Parse(AutotestPlan.TakeBJson(30.0, "sequence:2", "F:/a b/takeB")).File;
    Take take = file.FindTake(AutotestPlan.TakeBName)!;
    Expect(file.Capture.Mode == CaptureMode.Offline && file.CaptureFps == 15.0, "offline 15 fps");
    Expect(file.Capture.Folder == "F:/a b/takeB", "folder " + file.Capture.Folder);
    Expect(file.HideUi, "hideUi");
    Expect(take.Shots.Count == 5 && take.Speed.Count == 3, "five shots, three keys");
    Expect(take.Shots[4].At.Event == "sequence:2", "hold event");
    _ = new TakeDirector(take, file);
});

Check("autotest csv reads quoted fields and judges a take", () =>
{
    string text = EventLogCsv.Header + "\n"
        + "0.000,10.000,0.000,1,,take,start,\"a, b\"\n"
        + "7.000,17.000,0.000,1,106,event,ignition,\n"
        + "9.000,18.000,2.000,0.25,136,event,liftoff,\n"
        + "7.000,17.000,0.000,1,106,shot,camera[1],type=Tripod\n"
        + "7.000,17.000,0.000,1,106,speed,speed[0],to=0.25; ramp=0.5\n"
        + "30.000,20.000,13.000,1,451,take,end,the last shot is done\n";
    List<CsvRow> rows = AutotestAnalysis.ParseCsv(text);
    Expect(rows.Count == 6 && rows[0].Detail == "a, b" && rows[1].Frame == 106, "rows");
    Expect(AutotestAnalysis.EndReason(rows) == "the last shot is done", "end reason");
    Expect(AutotestAnalysis.EventsSeen(rows, new[] { "ignition", "liftoff" }).Pass, "events");
    Expect(!AutotestAnalysis.EventsSeen(rows, new[] { "ignition", "sequence:2" }).Pass, "missing event");
    Expect(AutotestAnalysis.ShotsStarted(rows, new double?[] { null, 7.0 }, 0.25).Pass == false, "camera[0] is missing");
    Expect(AutotestAnalysis.ShotsStarted(rows, new double?[] { 7.0 }, 0.25).Pass == false, "name is camera[0]");
});

Check("autotest sim speed and frame steps", () =>
{
    var samples = new List<AutotestSample>();
    double game = 0.0;
    for (int i = 0; i < 100; i++)
    {
        double speed = i < 10 ? 1.0 : 0.25;
        game += (1.0 / 15.0) * (i < 11 ? 1.0 : 0.25);
        samples.Add(new AutotestSample(i / 15.0, game, speed, speed, i >= 5));
    }

    var rows = new List<CsvRow> { new(0.33, 0, 0, 1, 5, "speed", "speed[0]", "to=0.25; ramp=0.1") };
    Expect(AutotestAnalysis.SimSpeedFollowsTarget(samples, rows).Pass, "speed follows");
    Expect(AutotestAnalysis.FrameSteps(samples, 15.0).Pass, "steps");
    samples[50] = new AutotestSample(samples[50].TakeSeconds + 0.03, samples[50].GameSeconds, 0.25, 0.25, true);
    Expect(!AutotestAnalysis.FrameSteps(samples, 15.0).Pass, "a wrong take step fails");
    var slow = new List<AutotestSample>();
    double slowGame = 0.0;
    for (int i = 0; i < 100; i++)
    {
        slowGame += (1.0 / 15.0) * (i % 2 == 0 ? 0.3 : 1.0);
        slow.Add(new AutotestSample(i / 15.0, slowGame, 1.0, 1.0, true));
    }

    Expect(!AutotestAnalysis.FrameSteps(slow, 15.0).Pass, "frames that run behind fail when there is no repeated frame");
    Expect(AutotestAnalysis.FrameCount(300, 300, 20.0, 15.0, 150).Pass, "frame count");
    Expect(!AutotestAnalysis.FrameCount(299, 300, 20.0, 15.0, 150).Pass, "frame count mismatch");
});

Check("autotest restored values", () =>
{
    var a = new ViewState("Orbit", "Rocket", 50.0, 1.0, true);
    Expect(AutotestAnalysis.Restored(a, a with { FovDegrees = 50.01 }).Pass, "same");
    Expect(!AutotestAnalysis.Restored(a, a with { DrawUi = false }).Pass, "DrawUI");
    Expect(!AutotestAnalysis.Restored(a, a with { SimSpeed = 0.5 }).Pass, "speed");
});

#endregion


#region Camera features: keep-out, look point blend, fit, flight frame, counted events

KeepOutBox Box(double minX, double minY, double minZ, double maxX, double maxY, double maxZ, string name = "deck") =>
    new() { Name = name, Min = new V3(minX, minY, minZ), Max = new V3(maxX, maxY, maxZ) };

Check("keep-out: a point inside a box moves to the nearest face plus the margin", () =>
{
    KeepOutBox box = Box(-10, -20, -30, 10, 20, 30);
    // Nearest face of each point, in the order top, -Y, +Y, -Z, +Z.
    V3 top = KeepOutMath.Push(box, new V3(9, 0, 0), out string f1);
    Expect(f1 == "top" && Near3(top, new V3(11, 0, 0)), "top " + top);
    V3 minusY = KeepOutMath.Push(box, new V3(0, -19, 0), out string f2);
    Expect(f2 == "-Y" && Near3(minusY, new V3(0, -21, 0)), "-Y " + minusY);
    V3 plusY = KeepOutMath.Push(box, new V3(0, 18, 0), out string f3);
    Expect(f3 == "+Y" && Near3(plusY, new V3(0, 21, 0)), "+Y " + plusY);
    V3 minusZ = KeepOutMath.Push(box, new V3(0, 0, -28), out string f4);
    Expect(f4 == "-Z" && Near3(minusZ, new V3(0, 0, -31)), "-Z " + minusZ);
    V3 plusZ = KeepOutMath.Push(box, new V3(0, 0, 29), out string f5);
    Expect(f5 == "+Z" && Near3(plusZ, new V3(0, 0, 31)), "+Z " + plusZ);
});

Check("keep-out: the bottom face is never used, a point near it goes to another face", () =>
{
    KeepOutBox box = Box(-10, -20, -30, 10, 20, 30);
    V3 near = new(-9.5, 0, 0);
    V3 pushed = KeepOutMath.Push(box, near, out string face);
    Expect(face != "" && pushed.X >= box.Min.X, "the point did not leave through the bottom: " + pushed);
    Expect(!KeepOutMath.Contains(box, pushed) || face == "", "the point is outside after the push");
    V3 outside = new(50, 0, 0);
    Expect(KeepOutMath.Push(box, outside, out string none) == outside && none == "", "a point outside stays");
});

Check("keep-out: boxes are applied in turn, far cameras are left alone and a take list wins over the file", () =>
{
    var boxes = new List<KeepOutBox> { Box(0, 0, 0, 10, 10, 10, "a"), Box(0, 10.5, 0, 10, 20, 10, "b") };
    V3 moved = KeepOutMath.Apply(boxes, new V3(5, 9.6, 5), out KeepOutBox? first, out _);
    Expect(first?.Name == "a" && boxes.All(b => !KeepOutMath.Contains(b, moved)), "pushed out of both: " + moved);
    V3 far = new(5, 9.6 + 10000, 5);
    Expect(KeepOutMath.Apply(new[] { Box(-1e9, -1e9, -1e9, 1e9, 1e9, 1e9) }, far, out KeepOutBox? none, out _) == far && none == null, "far from the site");

    ShotFile file = ShotFileJson.Parse("""
    { "keepOut": [ { "min": [-5, -5, -5], "max": [0, 5, 5], "name": "deck" } ],
      "takes": [
        { "name": "a", "camera": [ { "type": "tripod", "position": [1,2,3], "lookAt": [0,0,0], "duration": 5 } ] },
        { "name": "b", "keepOut": [], "camera": [ { "type": "tripod", "position": [1,2,3], "lookAt": [0,0,0], "duration": 5 } ] },
        { "name": "c", "keepOut": [ { "min": [0,0,0], "max": [1,1,1] } ], "camera": [ { "type": "tripod", "position": [1,2,3], "lookAt": [0,0,0], "duration": 5 } ] } ] }
    """).File;
    Expect(file.KeepOutFor(file.FindTake("a")!).Count == 1 && file.KeepOutFor(file.FindTake("a")!)[0].Name == "deck", "file list");
    Expect(file.KeepOutFor(file.FindTake("b")!).Count == 0, "an empty take list turns the boxes off");
    Expect(file.KeepOutFor(file.FindTake("c")!).Count == 1 && file.KeepOutFor(file.FindTake("c")!)[0].Name == "", "take list wins");
    string text = ShotFileJson.Write(file);
    Expect(ShotFileJson.Write(ShotFileJson.Parse(text).File) == text, "keepOut round trips");
    Expect(ExpectInvalid("""{ "keepOut": [ { "min": [1,0,0], "max": [0,1,1] } ], "camera": [ { "type": "tripod", "position": [1,2,3], "lookAt": [0,0,0], "duration": 5 } ] }""").FieldPath == "keepOut[0].min", "min above max");
    Expect(ExpectInvalid("""{ "keepOut": [ { "min": [0,0,0] } ], "camera": [ { "type": "tripod", "position": [1,2,3], "lookAt": [0,0,0], "duration": 5 } ] }""").FieldPath == "keepOut[0].max", "max is required");
});

Check("keep-out: the director moves the camera, keeps the look point and reports once per shot", () =>
{
    ShotFile file = ShotFileJson.Parse("""
    { "keepOut": [ { "min": [-30, -30, -30], "max": [0, 30, 30], "name": "deck" } ],
      "camera": [ { "type": "tripod", "position": [-5, 0, 2], "lookAt": [20, 0, 0], "duration": 5 } ] }
    """).File;
    var director = new TakeDirector(file.DefaultTake, file);
    DirectorFrame f0 = director.Update(0.0, World(Sample()));
    Expect(Near3(f0.Pose!.Value.Position, new V3(1, 0, 2)), "moved to the top face: " + f0.Pose.Value.Position);
    Expect(Near3(f0.Pose.Value.LookAt, new V3(20, 0, 0)), "look point stays");
    Expect(f0.Notes.Count == 1 && f0.Notes[0].Contains("deck"), "one note");
    DirectorFrame f1 = director.Update(0.1, World(Sample()));
    Expect(f1.Notes.Count == 0, "reported once");
});

Check("blend: the look point is mixed, so a subject that both shots look at stays in the middle of the picture", () =>
{
    var a = new CameraPose(PoseFrame.Site, new V3(0, -100, 50), new V3(0, 0, 0), V3.UnitX, 30);
    var b = new CameraPose(PoseFrame.Site, new V3(0, 100, 50), new V3(0, 0, 0), V3.UnitX, 50);
    CameraPose mid = PoseMath.Blend(a, b, 0.5);
    Expect(Near3(mid.LookAt, V3.Zero), "the shared look point is kept: " + mid.LookAt);
    // The old direction turn would look sideways from a position in the middle.
    var c = new CameraPose(PoseFrame.Site, new V3(0, -100, 0), new V3(0, 0, 0), V3.UnitX, 30);
    var d = new CameraPose(PoseFrame.Site, new V3(0, -100, 1000), new V3(0, 0, 1000), V3.UnitX, 30);
    CameraPose m2 = PoseMath.Blend(c, d, 0.25, curved: false);
    Expect(Near3(m2.LookAt, new V3(0, 0, 250)) && Near3(m2.Position, new V3(0, -100, 250)), "point and position move with the same weight");
    CameraPose end = PoseMath.Blend(c, d, 1.0);
    Expect(Near3(end.LookAt, d.LookAt) && Near3(end.Position, d.Position), "weight 1 is the second pose");
});

Check("blend: a pose without a look point falls back to the direction turn", () =>
{
    var a = new CameraPose(PoseFrame.Site, new V3(0, 0, 0), new V3(0, 0, 0), V3.UnitX, 30);
    var b = new CameraPose(PoseFrame.Site, new V3(0, 10, 0), new V3(0, 10, 10), V3.UnitX, 30);
    CameraPose mid = PoseMath.Blend(a, b, 0.5);
    Expect(!double.IsNaN(mid.LookAt.X) && (mid.LookAt - mid.Position).Length > 0.0, "defined look: " + mid.LookAt);
    V3 dir = (mid.LookAt - mid.Position).NormalizedOr(V3.Zero);
    double h = Math.Sqrt(0.5);
    Expect(Near3(dir, new V3(h, 0, h)), "a turn from the default direction to the look direction of the second pose: " + dir);
});

Check("fit: the field of view follows the distance and is limited to 5 to 90 degrees", () =>
{
    // A vehicle of 40 m by 0 m that should fill half of the picture at 100 m: tan(fov/2) = 40 / (2 * 100 * 0.5) = 0.4.
    double fov = PoseMath.FitFov(40.0, 0.0, 100.0, 0.5);
    Expect(Near(fov, 2.0 * Math.Atan(0.4) * 180.0 / Math.PI, 1e-9), "fov " + fov);
    Expect(Near(PoseMath.FitFov(40.0, 0.0, 1e6, 0.7), PoseMath.FitMinFov), "far away is limited to 5");
    Expect(Near(PoseMath.FitFov(40.0, 0.0, 1.0, 0.1), PoseMath.FitMaxFov), "very near is limited to 90");
    Expect(PoseMath.FitFov(40.0, 5.0, 100.0, 0.5) > fov, "the width makes the frame wider");

    ShotFile file = ShotFileJson.Parse("""
    { "camera": [
        { "type": "chase", "offset": [-100, 0, 0], "lookAt": [20, 0, 0], "duration": 5, "fit": 0.5, "smoothing": 0 },
        { "type": "part", "offset": [-100, 0, 0], "duration": 5, "fit": "off" },
        { "type": "track", "position": [20, -100, 0], "duration": 5, "fit": 0.7 },
        { "type": "path", "duration": 5, "fit": 0.6, "points": [ { "t": 0, "position": [20, -100, 0], "lookAt": [20, 0, 0], "fov": 80 } ] } ] }
    """).File;
    Expect(file.DefaultTake.Shots[0].Fit == 0.5 && file.DefaultTake.Shots[1].Fit == null && file.DefaultTake.Shots[2].Fit == 0.7 && file.DefaultTake.Shots[3].Fit == 0.6, "fit is read");
    string text = ShotFileJson.Write(file);
    Expect(ShotFileJson.Write(ShotFileJson.Parse(text).File) == text && text.Contains("\"fit\": 0.5"), "fit round trips");
    Expect(ExpectInvalid("""{ "camera": [ { "type": "chase", "offset": [1,0,0], "duration": 5, "fit": 7 } ] }""").FieldPath == "camera[0].fit", "fit out of range");

    var director = new TakeDirector(file.DefaultTake, file);
    var world = World(Sample());
    world.VehicleHeight = 40.0;
    world.VehicleWidth = 4.0;
    CameraPose chase = director.Update(0.0, world).Pose!.Value;
    double distance = (new V3(20, 0, 0) - new V3(-100, 0, 0)).Length;
    Expect(Near(chase.Fov, PoseMath.FitFov(40.0, 4.0, distance, 0.5), 1e-6), "the chase shot fits the vehicle: " + chase.Fov);
});

Check("flight frame: +Z along the horizontal velocity, +X up, right handed, north below a few m/s", () =>
{
    // A body centre below the origin: up is +X. The pole is +Z, so north is +Z.
    V3 centre = new(-6.4e6, 0, 0);
    V3 pole = V3.UnitZ;
    V3 east = V3.UnitY;
    Basis3 f = LocalHorizon.FlightFrame(new V3(1000, 0, 0), centre, pole, east);
    Expect(Near3(f.X, V3.UnitX) && Near3(f.Z, east), "up and flight axis");
    Expect(Near3(V3.Cross(f.X, f.Y), f.Z, 1e-9) && Near(f.Y.Length, 1.0), "right handed: " + f.Y);
    Expect(Near3(f.Y, new V3(0, 0, -1)), "+Y is Z cross X: " + f.Y);

    // A heading with a vertical part is projected on the horizontal plane.
    Basis3 tilted = LocalHorizon.FlightFrame(new V3(1000, 0, 0), centre, pole, new V3(5, 3, 0));
    Expect(Near3(tilted.Z, V3.UnitY), "the vertical part is dropped: " + tilted.Z);

    // No heading falls back to the local frame (north).
    Basis3 none = LocalHorizon.FlightFrame(new V3(1000, 0, 0), centre, pole, null);
    Expect(Near3(none.Z, V3.UnitZ) && Near3(none.Y, LocalHorizon.Frame(new V3(1000, 0, 0), centre, pole).Y), "north without a heading");

    V3 up = V3.UnitX, north = V3.UnitZ;
    Expect(Near3(FlightHeading.Raw(up, north, new V3(300, 1.0, 0.5)), north), "below 2 m/s horizontal: north");
    Expect(Near3(FlightHeading.Raw(up, north, new V3(300, 100, 0)), V3.UnitY, 1e-9), "above 6 m/s: the direction of the velocity");
    V3 between = FlightHeading.Raw(up, north, new V3(0, 4, 0));
    Expect(Near(between.Length, 1.0) && between.Y > 0.0 && between.Z > 0.0 && Near(between.X, 0.0), "between: a mix " + between);
    Expect(Near3(FlightHeading.Raw(up, north, new V3(300, -50, 0)), -V3.UnitY, 1e-9), "a velocity against north-east axes works too");
});

Check("flight frame: the smoothed heading starts at the velocity and follows a turn without a jump", () =>
{
    var heading = new FlightHeading();
    V3 up = V3.UnitX, north = V3.UnitZ;
    V3 first = heading.Update(up, north, new V3(0, 50, 0), 0.0);
    Expect(Near3(first, V3.UnitY), "first frame " + first);
    V3 step = heading.Update(up, north, new V3(0, 0, 50), 1.0 / 60.0);
    Expect(step.Y > 0.9, "one frame after a sudden turn the heading has hardly moved: " + step);
    V3 later = V3.UnitY;
    for (int i = 0; i < 600; i++)
        later = heading.Update(up, north, new V3(0, 0, 50), 1.0 / 60.0);
    Expect(Near3(later, V3.UnitZ, 1e-3), "after ten seconds it has turned: " + later);
});

Check("flight frame: a chase shot with the flight frame puts +Z ahead along the velocity and keeps the Earth below", () =>
{
    ShotFile file = ShotFileJson.Parse("""
    { "camera": [ { "type": "chase", "frame": "flight", "offset": [10, 0, 80], "lookAt": [0, 0, 0], "duration": 5, "smoothing": 0 } ] }
    """).File;
    Shot shot = file.DefaultTake.Shots[0];
    Expect(shot.Frame == ShotFrame.Flight && shot.UsesFlightFrame && shot.EffectiveUp == ShotUp.Horizon, "flight frame is read with the horizon up");
    Expect(ShotFileJson.Write(file).Contains("\"frame\": \"flight\""), "written back");

    var director = new TakeDirector(file.DefaultTake, file);
    var world = World(Sample());
    world.BodyCentre = new V3(-6.4e6, 0, 0);
    world.Pole = V3.UnitZ;
    world.SurfaceVelocity = new V3(200, 300, 0);
    CameraPose pose = director.Update(0.0, world).Pose!.Value;
    // The vehicle base is at the origin, the horizontal velocity is east (+Y), so +Z of the frame is +Y of the site.
    Expect(Near3(pose.Position, new V3(10, 80, 0), 1e-6), "the camera is ahead of the vehicle along the flight: " + pose.Position);
    Expect(Near3(pose.Up, V3.UnitX, 1e-4), "the up is away from the body");
    Expect(Near3(director.Heading!.Value, V3.UnitY, 1e-6), "the heading is exposed");

    world.SurfaceVelocity = new V3(0, 0.5, 0);
    var slow = new TakeDirector(file.DefaultTake, file);
    world.BodyCentre = new V3(-6.4e6, 0, 0);
    CameraPose still = slow.Update(0.0, world).Pose!.Value;
    Expect(Near3(still.Position, new V3(10, 0, 80), 1e-6), "north without horizontal speed: " + still.Position);
});

Check("counted events: names parse and print, the first one has no number", () =>
{
    Expect(EventName.TryParse("ignition:2", out EventName second, out _) && second.Canonical == "ignition:2" && second.Kind == EventKind.Ignition, "ignition:2");
    Expect(EventName.TryParse("ignition:1", out EventName one, out _) && one.Canonical == "ignition", "ignition:1 is ignition");
    Expect(EventName.TryParse("Burnout:3", out EventName third, out _) && third.Canonical == "burnout:3", "burnout:3");
    Expect(EventName.TryParse("ignition", out EventName plain, out _) && plain.Canonical == "ignition", "plain ignition");
    Expect(!EventName.TryParse("ignition:0", out _, out _) && !EventName.TryParse("burnout:x", out _, out _) && !EventName.TryParse("ignition:1.5", out _, out _), "bad counts");
    Expect(!EventName.TryParse("liftoff:2", out _, out string error) && error.Contains("liftoff"), "liftoff takes no number");
});

Check("counted events: hot staging gives ignition:2 when the upper stage lights while the lower stage burns", () =>
{
    var detector = new EventDetector(Array.Empty<EventName>());
    VehicleSample S(bool engine, int fresh = 0) => new(engine, false, 0, 100, 10, null, fresh);
    detector.Update(0.0, S(false));
    var first = detector.Update(1.0, S(true, fresh: 1));
    Expect(first.Count == 1 && first[0].Name == "ignition", "ignition");
    Expect(detector.Update(1.4, S(true, fresh: 2)).Count == 0, "engines of the same stage that light a moment later belong to ignition 1");
    Expect(detector.Update(10.0, S(true)).Count == 0, "nothing while the engines burn");
    var hot = detector.Update(45.0, S(true, fresh: 1));
    Expect(hot.Count == 1 && hot[0].Name == "ignition:2", "the upper stage lights: " + string.Join(",", hot.Select(e => e.Name)));
    Expect(detector.Update(46.0, S(true)).Count == 0, "no repeat");
    var end = detector.Update(90.0, S(false));
    Expect(end.Count == 1 && end[0].Name == "burnout", "the first time the engines go off");
    var relight = detector.Update(95.0, S(true, fresh: 1));
    Expect(relight.Count == 1 && relight[0].Name == "ignition:3", "a third ignition");
    var off2 = detector.Update(99.0, S(false));
    Expect(off2.Count == 1 && off2[0].Name == "burnout:2", "the second burnout");
});

Check("counted events: the triggers and the watched list know the counted names", () =>
{
    Take take = ParseTake("""
    { "events": { "upper": "ignition:2" },
      "camera": [ { "type": "tripod", "position": [1,2,3], "lookAt": [0,0,0], "duration": 5, "at": { "event": "upper", "delay": 1 } },
                  { "type": "tripod", "position": [1,2,3], "lookAt": [0,0,0], "duration": 5, "at": { "event": "burnout:2" } } ] }
    """, out ShotFile file);
    var timeline = new TimelineRunner(take, file.Events);
    TimelineState state = timeline.Update(5.0, Events(("ignition", 1.0)));
    Expect(state.ShotIndex == -1 && timeline.ShotStart(1) == null, "no shot starts before ignition:2: " + state.ShotIndex);
    Expect(timeline.ShotStart(0) == null, "not started before the event");
    state = timeline.Update(50.0, Events(("ignition", 1.0), ("ignition:2", 40.0)));
    Expect(timeline.ShotStart(0) is double start && Near(start, 41.0), "starts one second after ignition:2");
});

#endregion

Console.WriteLine(failures == 0 ? "All tests passed." : $"{failures} test(s) failed.");
return failures == 0 ? 0 : 1;

sealed class FlushCounter : StringWriterBase
{
    public FlushCounter(StringWriter inner) : base(inner) { }
}

abstract class StringWriterBase : TextWriter
{
    private readonly StringWriter _inner;

    protected StringWriterBase(StringWriter inner) => _inner = inner;

    public int Flushes { get; private set; }

    public override System.Text.Encoding Encoding => _inner.Encoding;

    public override void Write(char value) => _inner.Write(value);

    public override void Write(string? value) => _inner.Write(value);

    public override void Flush()
    {
        Flushes++;
        _inner.Flush();
    }
}
