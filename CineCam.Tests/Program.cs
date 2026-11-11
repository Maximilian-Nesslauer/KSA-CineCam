using System.Globalization;
using CineCam.Director;
using CineCam.Director.Quick;
using CineCam.Editing;

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


#region Editing

string TempFile(string name)
{
    string folder = Path.Combine(Path.GetTempPath(), "cinecam-tests-" + Environment.ProcessId);
    Directory.CreateDirectory(folder);
    return Path.Combine(folder, name);
}

Check("editing: the fields of each shot type match what the writer saves", () =>
{
    var keys = new Dictionary<ShotField, string>
    {
        [ShotField.Center] = "center", [ShotField.Radius] = "radius",
        [ShotField.Height] = "height", [ShotField.StartDeg] = "startDeg", [ShotField.DegPerSec] = "degPerSec",
        [ShotField.LookAt] = "lookAt", [ShotField.From] = "from", [ShotField.To] = "to",
        [ShotField.Azimuth] = "azimuth", [ShotField.Position] = "position", [ShotField.Offset] = "offset",
        [ShotField.Smoothing] = "smoothing", [ShotField.KeepSize] = "keepSize", [ShotField.Part] = "part",
    };
    foreach (ShotType type in Enum.GetValues<ShotType>())
    {
        var file = new ShotFile();
        var take = new Take { Name = "a", IsDefault = true };
        Shot shot = ShotEdit.NewShot(type);
        shot.Part = "p";
        shot.KeepSize = true;
        take.Shots.Add(shot);
        file.Takes.Add(take);
        string json = ShotFileJson.Write(file);
        foreach (var pair in keys)
        {
            if (type == ShotType.Path && pair.Key is ShotField.LookAt or ShotField.Position)
                continue;
            bool written = json.Contains("\"" + pair.Value + "\"");
            Expect(written == ShotFields.Uses(type, pair.Key), $"{type} {pair.Key}: written={written}");
        }
    }
});

Check("editing: a new shot of every type is valid", () =>
{
    foreach (ShotType type in Enum.GetValues<ShotType>())
    {
        var file = new ShotFile();
        var take = new Take { Name = "a", IsDefault = true };
        take.Shots.Add(ShotEdit.NewShot(type));
        file.Takes.Add(take);
        ValidationResult result = ShotFileValidator.Validate(file);
        Expect(result.IsValid, type + ": " + string.Join("; ", result.Errors));
    }
});

Check("editing: a type change keeps the shared fields and gives the new type a valid radius", () =>
{
    Shot shot = ShotEdit.NewShot(ShotType.Chase);
    shot.Fov = 22.0;
    shot.Duration = 3.0;
    ShotEdit.ChangeType(shot, ShotType.Orbit);
    Expect(shot.Type == ShotType.Orbit && shot.Fov == 22.0 && shot.Duration == 3.0, "shared fields");
    Expect(shot.Radius > 0.0 && shot.Target == ShotTarget.Site, "radius and target of an orbit");
    ShotEdit.ChangeType(shot, ShotType.Track);
    Expect(shot.Target == Shot.DefaultTarget(ShotType.Track), "track target");
    shot.Target = ShotTarget.Vehicle;
    ShotEdit.ChangeType(shot, ShotType.Crane);
    Expect(shot.Target == Shot.DefaultTarget(ShotType.Crane), "a type that takes a target gets the default of its type");
});

Check("editing: clones share nothing and keep unknown fields", () =>
{
    ShotFile file = ShotFileJson.Parse("""
    { "camera": [ { "type": "tripod", "position": [1,2,3], "lookAt": [0,0,0], "duration": 5, "note": "keep", "transition": { "type": "blend", "seconds": 2 }, "at": { "event": "ignition", "delay": 1 } } ],
      "speed": [ { "at": 3, "to": 0.5, "ramp": 1, "extra": 7 } ] }
    """).File;
    Take copy = ShotEdit.Clone(file.DefaultTake, "copy");
    copy.Shots[0].Position = new V3(9, 9, 9);
    copy.Shots[0].At.Delay = 5.0;
    copy.Shots[0].Transition.Seconds = 9.0;
    copy.Speed[0].To = 3.0;
    Shot original = file.DefaultTake.Shots[0];
    Expect(original.Position == new V3(1, 2, 3) && original.At.Delay == 1.0 && original.Transition.Seconds == 2.0, "shot untouched");
    Expect(file.DefaultTake.Speed[0].To == 0.5, "speed key untouched");
    Expect(copy.Shots[0].Extras.ContainsKey("note") && copy.Speed[0].Extras.ContainsKey("extra"), "extras copied");
    Expect(copy.Name == "copy" && !copy.IsDefault, "name and default flag");
});

Check("editing: move, unique names and descriptions", () =>
{
    var list = new List<int> { 1, 2, 3 };
    Expect(ShotEdit.Move(list, 0, 1) == 1 && list.SequenceEqual(new[] { 2, 1, 3 }), "move down");
    Expect(ShotEdit.Move(list, 0, -1) == 0 && list.SequenceEqual(new[] { 2, 1, 3 }), "no move above the top");
    Expect(ShotEdit.Move(list, 2, 1) == 2, "no move below the end");
    var takes = new[] { new Take { Name = "a" }, new Take { Name = "a-2" } };
    Expect(ShotEdit.UniqueTakeName(takes, "a") == "a-3" && ShotEdit.UniqueTakeName(takes, "b") == "b", "unique name");
    Shot shot = ShotEdit.NewShot(ShotType.Chase);
    shot.At = Trigger.OnEvent("liftoff", 4);
    Expect(ShotEdit.Describe(1, shot) == "2  chase  8 s  on liftoff +4 s", ShotEdit.Describe(1, shot));
    Expect(ShotEdit.Describe(Trigger.AtTime(2.5)) == "at 2.5 s" && ShotEdit.Describe(Trigger.After()) == "after the one before", "triggers");
});

Check("editing: the validator names the bad field and the bad event", () =>
{
    ShotFile file = DefaultShotFile.Load().File;
    Expect(ShotFileValidator.Validate(file).IsValid, "the default file is valid");
    file.DefaultTake.Shots[0].Duration = 0.0;
    ValidationResult zero = ShotFileValidator.Validate(file);
    Expect(!zero.IsValid && zero.Errors[0].Contains("duration"), "duration: " + string.Join("; ", zero.Errors));
    file.DefaultTake.Shots[0].Duration = 5.0;
    file.DefaultTake.Shots[1].At = Trigger.OnEvent("not-an-event");
    ValidationResult bad = ShotFileValidator.Validate(file);
    Expect(!bad.IsValid && bad.Errors[0].Contains("event"), "event: " + string.Join("; ", bad.Errors));
    file.DefaultTake.Shots[1].At = Trigger.OnEvent("ignition", -2.0);
    ValidationResult warned = ShotFileValidator.Validate(file);
    Expect(warned.IsValid && warned.Warnings.Count == 1, "a negative delay on ignition is a warning");
    file.DefaultTake.Shots.Clear();
    Expect(!ShotFileValidator.Validate(file).IsValid, "a take without a shot");
});

Check("editing: the copy for a run shares nothing with the document", () =>
{
    ShotFile file = DefaultShotFile.Load().File;
    ShotFile copy = ShotFileValidator.Copy(file);
    copy.DefaultTake.Shots[0].Duration = 99.0;
    Expect(file.DefaultTake.Shots[0].Duration != 99.0, "the document is untouched");
    Expect(copy.Takes.Count == file.Takes.Count, "same takes");
});

Check("editing: the session saves, keeps a backup and refuses a file with errors", () =>
{
    string path = TempFile("session.json");
    File.WriteAllText(path, DefaultShotFile.Json);
    var session = new EditSession();
    Expect(session.Load(path) && !session.Dirty && session.Take != null, "loaded");
    int shots = session.Take!.Shots.Count;
    session.AddShot(ShotType.Tripod);
    Expect(session.Dirty && session.Take!.Shots.Count == shots + 1 && session.ShotIndex == 1, "added after the selection");
    session.MoveShot(-1);
    Expect(session.ShotIndex == 0 && session.Take!.Shots[0].Type == ShotType.Tripod, "moved up");
    session.DuplicateShot();
    session.RemoveShot();
    Expect(session.Take!.Shots.Count == shots + 1, "duplicate and remove");
    Expect(session.Save() && !session.Dirty, "saved: " + session.Message);
    Expect(File.Exists(path + ".bak"), "backup of the old file");
    ShotFile again = ShotFileJson.Load(path).File;
    Expect(again.Takes.Count == session.Document!.Takes.Count && again.Takes[0].Shots.Count == shots + 1, "the file reads again");

    session.Take!.Shots[0].Duration = 0.0;
    session.Changed();
    Expect(!session.Validation.IsValid, "invalid");
    Expect(!session.Save(), "not saved");
    Expect(ShotFileJson.Load(path).File.Takes[0].Shots[0].Duration > 0.0, "the file on disk is untouched");
    Expect(session.MessageIsError, "an error message");
});

Check("editing: the session handles takes", () =>
{
    var session = new EditSession();
    session.NewFromDefault(TempFile("takes.json"));
    Expect(session.Dirty && session.Document!.Takes.Count == 2, "default file");
    session.AddTake();
    Expect(session.Document!.Takes.Count == 3 && session.TakeIndex == 2 && session.Validation.IsValid, "added");
    session.RenameTake("third");
    session.SetTakeSite("PadA");
    session.MakeDefault();
    Expect(session.Document!.Takes.Count(t => t.IsDefault) == 1 && session.Take!.IsDefault, "one default");
    session.DuplicateTake();
    Expect(session.Take!.Name == "third-copy" && !session.Take.IsDefault && session.Take.Site == "PadA", "duplicate");
    session.RemoveTake();
    session.RemoveTake();
    session.RemoveTake();
    Expect(session.Document!.Takes.Count == 1, "the last take stays");
    Expect(session.Document.Takes.Count(t => t.IsDefault) == 1, "a default remains");
    ShotFile copy = session.CopyForRun();
    Expect(copy.Takes.Count == 1, "copy for a run");
});

Check("editing: a renamed single take keeps its name in the file", () =>
{
    string path = TempFile("single.json");
    File.WriteAllText(path, minimal);
    var session = new EditSession();
    Expect(session.Load(path), "loaded");
    session.RenameTake("opening");
    Expect(session.Save(), "saved");
    ShotFile again = ShotFileJson.Load(path).File;
    Expect(again.Takes[0].Name == "opening", "the name survives: " + again.Takes[0].Name);
});

Check("editing: a failed load keeps the old document and says why", () =>
{
    var session = new EditSession();
    session.NewFromDefault(TempFile("keep.json"));
    string bad = TempFile("bad.json");
    File.WriteAllText(bad, "{ \"camera\": [ { \"type\": \"tripod\" } ] }");
    Expect(!session.Load(bad), "bad file");
    Expect(session.Document != null && session.MessageIsError && session.Message.Contains("camera[0]"), session.Message);
    Expect(!session.Load(TempFile("missing.json")) && session.MessageIsError, "missing file");
});

Basis3 TiltedVehicle() => Basis3.FromXAndHint(new V3(2, 5, -3), new V3(1, 0.3, 0.2), new V3(0, 1, 0));

CapturedCamera CameraOf(CameraPose pose) =>
    new(pose.Position, (pose.LookAt - pose.Position).NormalizedOr(V3.UnitX), pose.Fov);

Check("capture: a chase shot written from its own camera gives the same camera", () =>
{
    Basis3 vehicle = TiltedVehicle();
    Shot shot = ShotEdit.NewShot(ShotType.Chase);
    var inputs = new PoseInputs { Vehicle = vehicle, VehicleHeight = 30 };
    CameraPose pose = PoseMath.ToSiteFrame(PoseMath.Evaluate(shot, 0, inputs), vehicle);
    Shot other = ShotEdit.NewShot(ShotType.Chase);
    other.Offset = new V3(1, 1, 1);
    other.LookAt = new V3(5, 5, 5);
    other.Fov = 70;
    Expect(ShotCapture.Apply(other, CameraOf(pose), vehicle, 30, null, out _), "applied");
    CameraPose again = PoseMath.ToSiteFrame(PoseMath.Evaluate(other, 0, inputs), vehicle);
    Expect(Near3(again.Position, pose.Position, 0.02), "position " + again.Position + " vs " + pose.Position);
    V3 d1 = (again.LookAt - again.Position).NormalizedOr(V3.UnitX);
    V3 d2 = (pose.LookAt - pose.Position).NormalizedOr(V3.UnitX);
    Expect(Near3(d1, d2, 1e-3), "direction " + d1 + " vs " + d2);
    Expect(Near(other.Fov, shot.Fov, 0.06), "fov");
});

Check("capture: tripod in the site frame and in the vehicle frame", () =>
{
    Basis3 vehicle = TiltedVehicle();
    var camera = new CapturedCamera(new V3(4, -50, -30), new V3(0.1, 0.8, 0.6).NormalizedOr(V3.UnitY), 33.3);
    Shot site = ShotEdit.NewShot(ShotType.Tripod);
    Expect(ShotCapture.Apply(site, camera, vehicle, 30, null, out _), "site");
    Expect(Near3(site.Position, camera.Position, 0.01) && site.Fov == 33.3, "site position");
    V3 look = (site.LookAt - site.Position).NormalizedOr(V3.UnitX);
    Expect(Near3(look, camera.Forward, 1e-3), "site direction");

    Shot onVehicle = ShotEdit.NewShot(ShotType.Tripod);
    onVehicle.Target = ShotTarget.Vehicle;
    Expect(ShotCapture.Apply(onVehicle, camera, vehicle, 30, null, out _), "vehicle");
    Expect(Near3(vehicle.ToParent(onVehicle.Position), camera.Position, 0.02), "vehicle position maps back");
    Expect(Near3(vehicle.ToParent(onVehicle.LookAt), site.LookAt, 0.03), "look point maps back");
});

Check("capture: track takes the position only, part uses the part position", () =>
{
    Basis3 vehicle = TiltedVehicle();
    var camera = new CapturedCamera(new V3(10, -40, 20), new V3(0, 1, -0.5).NormalizedOr(V3.UnitY), 25);
    Shot track = ShotEdit.NewShot(ShotType.Track);
    V3 offsetBefore = track.Offset;
    Expect(ShotCapture.Apply(track, camera, vehicle, 30, null, out string message), "track");
    Expect(Near3(track.Position, camera.Position, 0.01) && track.Offset == offsetBefore && message.Contains("looks at the vehicle"), "track fields");

    Shot part = ShotEdit.NewShot(ShotType.Part);
    part.Part = "engine";
    Expect(!ShotCapture.Apply(part, camera, vehicle, 30, null, out string missing) && missing.Contains("engine"), "missing part");
    V3 partAt = new V3(3, 0.5, 0.5);
    Expect(ShotCapture.Apply(part, camera, vehicle, 30, partAt, out _), "part");
    CameraPose pose = PoseMath.ToSiteFrame(PoseMath.Evaluate(part, 0, new PoseInputs { Vehicle = vehicle, PartPosition = partAt }), vehicle);
    Expect(Near3(pose.Position, camera.Position, 0.03), "part position " + pose.Position);

    Shot orbit = ShotEdit.NewShot(ShotType.Orbit);
    Expect(!ShotCapture.Apply(orbit, camera, vehicle, 30, null, out _) && !ShotCapture.CanCapture(ShotType.Crane), "orbit and crane");
});

Check("capture: the look point is at the vehicle depth when it is ahead, else at the fallback distance", () =>
{
    var vehicle = new Basis3(new V3(0, 0, 0), V3.UnitX, V3.UnitY, V3.UnitZ);
    var camera = new CapturedCamera(new V3(10, -100, 0), V3.UnitY, 30);
    Shot tripod = ShotEdit.NewShot(ShotType.Tripod);
    ShotCapture.Apply(tripod, camera, vehicle, 20, null, out _);
    Expect(Near(tripod.LookAt.Y, 0.0, 0.01), "the look point is at the depth of the vehicle: " + tripod.LookAt);
    var behind = new CapturedCamera(new V3(10, 100, 0), V3.UnitY, 30);
    ShotCapture.Apply(tripod, behind, vehicle, 20, null, out _);
    Expect(Near(tripod.LookAt.Y, 150.0, 0.01), "a vehicle behind the camera gives the fallback distance: " + tripod.LookAt);
});

Check("preview: one shot gives its pose with no shot list", () =>
{
    var file = new ShotFile();
    var empty = new Take { Name = "preview" };
    var director = new TakeDirector(empty, file);
    Basis3 vehicle = TiltedVehicle();
    Shot shot = ShotEdit.NewShot(ShotType.Chase);
    var world = new WorldSample { VehicleFrame = vehicle, VehicleHeight = 30 };
    var warnings = new List<string>();
    Basis3 smoothed = director.SmoothVehicle(world.VehicleFrame, shot.Smoothing, 0.0);
    CameraPose pose = director.ShotPoseInSite(shot, 0, 0.5, world, smoothed, warnings);
    Expect(Near3(pose.Position, vehicle.ToParent(shot.Offset), 1e-6), "chase position in the site frame");
    Shot track = ShotEdit.NewShot(ShotType.Track);
    track.KeepSize = true;
    CameraPose first = director.ShotPoseInSite(track, 1, 0.0, world, smoothed, warnings);
    Expect(Near(first.Fov, track.Fov, 1e-6), "first track pose has the shot field of view");
    track.Position = new V3(3, -400, -300);
    CameraPose far = director.ShotPoseInSite(track, 1, 0.0, world, smoothed, warnings);
    Expect(far.Fov < track.Fov, "the field of view narrows with the distance");
    director.ForgetTrackReference();
    CameraPose forgot = director.ShotPoseInSite(track, 1, 0.0, world, smoothed, warnings);
    Expect(Near(forgot.Fov, track.Fov, 1e-6), "after the reset the distance is measured again");
});

#endregion

#region Offline capture

string CaptureFile(string capture) =>
    "{ \"fps\": 30, \"capture\": " + capture + ", \"camera\": [ { \"type\": \"tripod\", \"duration\": 5, \"position\": [1, 2, 3], \"lookAt\": [0, 0, 0] } ] }";

Check("The capture options are read, written and read again", () =>
{
    ShotFile file = ShotFileJson.Parse(CaptureFile("{ \"mode\": \"offline\", \"fps\": 24, \"folder\": \"out\", \"scale\": 2, \"later\": 1 }")).File;
    Expect(file.Capture.Mode == CaptureMode.Offline, "mode");
    Expect(file.CaptureFps == 24.0, "capture fps wins over the file fps");
    Expect(file.Capture.Folder == "out" && file.Capture.Scale == 2, "folder and scale");
    string text = ShotFileJson.Write(file);
    ShotFile back = ShotFileJson.Parse(text).File;
    Expect(back.Capture.Mode == CaptureMode.Offline && back.Capture.Fps == 24.0 && back.Capture.Folder == "out" && back.Capture.Scale == 2, "round trip");
    Expect(back.Capture.Extras.ContainsKey("later"), "unknown capture field kept");
    Expect(text.All(c => c < 128), "ASCII");
});

Check("Without a capture field the file stays in the real time mode and writes none", () =>
{
    ShotFile file = ShotFileJson.Parse(CaptureFile("{ }")).File;
    Expect(file.Capture.Mode == CaptureMode.Realtime && file.CaptureFps == 30.0, "defaults");
    Expect(!ShotFileJson.Write(file).Contains("\"capture\""), "default capture is left out");
});

Check("A bad capture mode or scale names the field", () =>
{
    Expect(ExpectInvalid(CaptureFile("{ \"mode\": \"slow\" }")).FieldPath == "capture.mode", "mode path");
    Expect(ExpectInvalid(CaptureFile("{ \"scale\": 1.5 }")).FieldPath == "capture.scale", "scale path");
    Expect(ExpectInvalid(CaptureFile("{ \"fps\": 0 }")).FieldPath == "capture.fps", "fps path");
});

Check("The editor copy keeps the capture options", () =>
{
    ShotFile file = ShotFileJson.Parse(CaptureFile("{ \"mode\": \"offline\" }")).File;
    ShotFile copy = ShotFileValidator.Copy(file);
    Expect(copy.Capture.Mode == CaptureMode.Offline, "mode kept");
});

Check("Frame names have six digits and the output folder is resolved", () =>
{
    Expect(OfflineOutput.FrameName(1) == "000001.png" && OfflineOutput.FrameName(123456) == "123456.png", "names");
    string take = Path.Combine(Path.GetTempPath(), "take");
    Expect(OfflineOutput.ResolveFolder(take, null) == take, "default");
    Expect(OfflineOutput.ResolveFolder(take, "  ") == take, "blank");
    Expect(OfflineOutput.ResolveFolder(take, "sub") == Path.Combine(take, "sub"), "relative");
    string rooted = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "elsewhere"));
    Expect(OfflineOutput.ResolveFolder(take, rooted) == rooted, "rooted");
});

Check("The video scripts carry the ffmpeg line", () =>
{
    string cmd = OfflineOutput.MakeVideoCmd(29.97);
    string sh = OfflineOutput.MakeVideoSh(60);
    Expect(cmd.Contains("-framerate 29.97 ") && cmd.Contains("frames\\%%06d.png") && cmd.Contains("libx264") && cmd.Contains("yuv420p") && cmd.Contains("-crf 16"), "cmd line");
    Expect(sh.Contains("-framerate 60 ") && sh.Contains("frames/%06d.png") && !sh.Contains("\r"), "sh line");
    Expect(cmd.All(c => c < 128) && sh.All(c => c < 128), "ASCII");
});

Check("The helpers and the readme are written", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "cinecam-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        string output = Path.Combine(root, "out");
        OfflineOutput.WriteHelpers(output, root, "launch", 60);
        Expect(File.Exists(Path.Combine(output, "make-video.cmd")) && File.Exists(Path.Combine(output, "make-video.sh")), "scripts");
        Expect(File.ReadAllText(Path.Combine(root, "README.txt")).Contains("60 pictures per second"), "readme");
    }
    finally
    {
        Directory.Delete(root, true);
    }
});

byte[] Png(int extra = 0)
{
    var bytes = new byte[64 + extra];
    new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
    return bytes;
}

Check("The collector takes a finished picture, in order, and ignores the old ones", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "cinecam-test-" + Guid.NewGuid().ToString("N"));
    string source = Path.Combine(root, "shots");
    string frames = Path.Combine(root, "frames");
    Directory.CreateDirectory(source);
    try
    {
        File.WriteAllBytes(Path.Combine(source, "ksa_old_1920x1080.png"), Png());
        var collector = new ScreenshotCollector(source, frames);
        Expect(collector.TryCollect(1, out _) == CollectResult.Waiting, "the old picture is not taken");

        File.WriteAllBytes(Path.Combine(source, "ksa_new_1920x1080.png"), Png());
        Expect(collector.TryCollect(1, out string? moved) == CollectResult.Done, "the new picture is taken");
        Expect(moved == Path.Combine(frames, "000001.png") && File.Exists(moved), "moved to the first frame");
        Expect(!File.Exists(Path.Combine(source, "ksa_new_1920x1080.png")), "source is gone");

        // The next picture may carry the same name, because the game names by the second.
        File.WriteAllBytes(Path.Combine(source, "ksa_new_1920x1080.png"), Png(5));
        Expect(collector.TryCollect(2, out _) == CollectResult.Done, "same name again");
        Expect(new FileInfo(Path.Combine(frames, "000002.png")).Length == 69, "second frame has the second content");
    }
    finally
    {
        Directory.Delete(root, true);
    }
});

Check("The collector waits while the game still holds the file", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "cinecam-test-" + Guid.NewGuid().ToString("N"));
    string source = Path.Combine(root, "shots");
    Directory.CreateDirectory(source);
    try
    {
        var collector = new ScreenshotCollector(source, Path.Combine(root, "frames"));
        string path = Path.Combine(source, "ksa_busy.png");
        using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            writer.Write(Png());
            writer.Flush();
            Expect(collector.TryCollect(1, out _) == CollectResult.Waiting, "an open file is not finished");
            Expect(collector.Wait(1, TimeSpan.FromMilliseconds(30)) == CollectResult.Waiting, "the wait times out");
        }

        Expect(collector.Wait(1, TimeSpan.FromSeconds(2)) == CollectResult.Done, "taken after the writer closed");
    }
    finally
    {
        Directory.Delete(root, true);
    }
});

Check("The collector gives up when told, and skips a file that is no picture", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "cinecam-test-" + Guid.NewGuid().ToString("N"));
    string source = Path.Combine(root, "shots");
    Directory.CreateDirectory(source);
    try
    {
        var collector = new ScreenshotCollector(source, Path.Combine(root, "frames"));
        Expect(collector.Wait(1, TimeSpan.FromSeconds(5), () => true) == CollectResult.Waiting, "give up at once");
        File.WriteAllText(Path.Combine(source, "ksa_text.png"), "not a png");
        Expect(collector.TryCollect(1, out _) == CollectResult.Waiting, "wrong content is not a picture");
    }
    finally
    {
        Directory.Delete(root, true);
    }
});

#endregion

#region Take and camera rules

double AngleBetween(V3 a, V3 b) => Math.Acos(Math.Clamp(V3.Dot(a.NormalizedOr(V3.UnitX), b.NormalizedOr(V3.UnitX)), -1.0, 1.0));

Check("up: the result is a unit vector perpendicular to the view, also along the up axis", () =>
{
    var stabilizer = new UpStabilizer();
    foreach (V3 view in new[] { V3.UnitX, -V3.UnitX, new V3(1, 0.001, 0), new V3(0.3, 0.4, -0.2) })
    {
        V3 up = stabilizer.Stabilize(view, V3.UnitX);
        Expect(Near(up.Length, 1.0, 1e-9), "unit length");
        Expect(Math.Abs(V3.Dot(up, view.NormalizedOr(V3.UnitX))) < 1e-9, "perpendicular");
        Expect(!double.IsNaN(up.X + up.Y + up.Z), "no NaN");
    }
});

Check("up: a sweep through the pole turns the roll in small steps", () =>
{
    var stabilizer = new UpStabilizer();
    V3 last = stabilizer.Stabilize(new V3(1.0, -1.0, 0.02), V3.UnitX);
    double largest = 0.0;
    for (int i = -2000; i <= 2000; i++)
    {
        // The view passes the +X axis on a line that misses it by 0.02 rad.
        double t = i * 0.0005;
        V3 view = new V3(1.0, t, 0.02);
        V3 up = stabilizer.Stabilize(view, V3.UnitX);
        largest = Math.Max(largest, AngleBetween(up, last));
        last = up;
    }

    Expect(largest < 0.2, $"largest step {largest} rad");
});

Check("up: away from the pole the hint is kept exactly", () =>
{
    var stabilizer = new UpStabilizer();
    V3 view = new V3(0.0, 1.0, 0.0);
    V3 up = stabilizer.Stabilize(view, V3.UnitX);
    Expect(Near3(up, V3.UnitX), "up stays +X");
});

Check("burnout: an engine that ran just over the minimum time counts", () =>
{
    var detector = new EventDetector(Array.Empty<EventName>());
    detector.Update(0.0, Sample());
    detector.Update(1.0, Sample(engine: true));
    var off = detector.Update(2.05, Sample(engine: false));
    Expect(off.Count == 1 && off[0].Name == "burnout", "burnout after 1.05 s");
});

Check("collector: a new picture with the name of an old one is taken", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "cinecam-test-" + Guid.NewGuid().ToString("N"));
    string source = Path.Combine(root, "shots");
    Directory.CreateDirectory(source);
    try
    {
        string playerShot = Path.Combine(source, "ksa_same_1920x1080.png");
        var collector = new ScreenshotCollector(source, Path.Combine(root, "frames"));
        File.WriteAllBytes(playerShot, Png());
        File.WriteAllBytes(Path.Combine(source, "ksa_other_1920x1080.png"), Png(3));
        File.SetLastWriteTimeUtc(playerShot, DateTime.UtcNow.AddSeconds(-5));
        Expect(collector.TryCollect(1, out _) == CollectResult.Done, "the newest file is taken");
        Expect(File.Exists(playerShot), "the other file stays");

        File.WriteAllBytes(playerShot, Png(9));
        Expect(collector.TryCollect(2, out _) == CollectResult.Done, "the same name with new content counts as new");
        Expect(new FileInfo(Path.Combine(root, "frames", "000002.png")).Length == 73, "content of the second picture");
    }
    finally
    {
        Directory.Delete(root, true);
    }
});

Check("speed policy: waits for an automatic warp and for a menu pause, writes otherwise", () =>
{
    Expect(SpeedPolicy.Decide(true, 100, null, 1).Action == SpeedAction.Wait, "auto warp");
    Expect(SpeedPolicy.Decide(true, 100, null, 1).ReportAutoWarp, "auto warp is reported");
    Expect(SpeedPolicy.Decide(false, 0, 4, 2).Action == SpeedAction.Wait, "menu pause after a write");
    Expect(SpeedPolicy.Decide(false, 1, null, 4).Action == SpeedAction.Write, "first write");
    Expect(SpeedPolicy.Decide(false, 4, 4, 4).Action == SpeedAction.Keep, "already there");
    SpeedDecision foreign = SpeedPolicy.Decide(false, 8, 4, 4);
    Expect(foreign.Action == SpeedAction.Write && foreign.ReportOverride, "a foreign change is written over and reported");
    Expect(SpeedPolicy.Decide(false, 0, 0, 1).Action == SpeedAction.Write, "a written zero is not a pause");
});

Check("offline: the step follows the pictures, a lost picture retries with the small step", () =>
{
    var capture = new OfflineCapture(50.0, "out", "out/frames", new ScreenshotCollector(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "cinecam-unused-" + Guid.NewGuid().ToString("N"))));
    try
    {
        Expect(Near(capture.NextStep, 0.02) && capture.FrameNumber == 1, "first step");
        double time = capture.Advance(capture.NextStep, 100.0);
        Expect(Near(time, 100.02), "the game time moves by one step");
        capture.RecordSaved(1);
        Expect(Near(capture.TakeSeconds, 0.02) && capture.FrameNumber == 2, "take time follows the pictures");

        Expect(!capture.RecordLost(3), "one loss does not end the take");
        Expect(capture.Retry && Near(capture.NextStep, OfflineCapture.RetryStepSeconds), "retry step");
        time = capture.Advance(capture.NextStep, 999.0);
        Expect(Near(time, 100.02 + OfflineCapture.RetryStepSeconds), "the base time is kept");
        Expect(Near(capture.TakeSeconds, 0.02) && capture.FrameNumber == 2, "the take time did not move");
        capture.RecordSaved(2);
        Expect(!capture.Retry && capture.FailuresInRow == 0 && capture.PicturesLost == 1, "recovered");

        capture.RecordLost(3);
        capture.RecordLost(3);
        Expect(capture.RecordLost(3), "three losses in a row end the take");
    }
    finally
    {
        foreach (string folder in Directory.GetDirectories(Path.GetTempPath(), "cinecam-unused-*"))
            Directory.Delete(folder, true);
    }
});

Check("director: a blend keeps the smoothing of the outgoing shot", () =>
{
    ShotFile file = ShotFileJson.Parse("""
{ "takes": [ { "name": "t", "camera": [
  { "type": "chase", "target": "vehicle", "offset": [-10, 0, 0], "lookAt": [0, 0, 0], "smoothing": 5, "duration": 2 },
  { "type": "tripod", "target": "vehicle", "position": [-10, 0, 0], "lookAt": [0, 0, 0], "duration": 5, "transition": { "type": "blend", "seconds": 1 } }
] } ] }
""").File;
    var director = new TakeDirector(file.DefaultTake, file);
    Basis3 Frame(double angle) => Basis3.FromXAndHint(V3.Zero, new V3(Math.Cos(angle), Math.Sin(angle), 0.0), V3.UnitZ);
    var sample = new WorldSample { VehicleFrame = Frame(0.0), VehicleHeight = 10.0 };
    for (double t = 0.0; t < 1.9; t += 0.1)
        director.Update(t, sample);
    sample.VehicleFrame = Frame(0.5);
    DirectorFrame a = director.Update(1.9, sample);
    DirectorFrame b = director.Update(2.0, sample);
    Expect(a.Pose.HasValue && b.Pose.HasValue, "poses exist");
    double jump = (a.Pose!.Value.Position - b.Pose!.Value.Position).Length;
    Expect(jump < 3.0, $"the outgoing shot does not jump when the blend starts ({jump} m)");
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


#region Checkpoint and reset

Check("checkpoint names follow the save name rules of the game", () =>
{
    Expect(CheckpointName.Sanitize("a  b/c:d ") == "a bcd", "invalid characters go and spaces collapse");
    Expect(CheckpointName.Sanitize("  x") == "x", "no leading space");
    Expect(CheckpointName.Sanitize("CON") == "CON_", "device name");
    Expect(CheckpointName.Sanitize(new string('a', 100)).Length == 64, "length");
    Expect(CheckpointName.Sanitize("") == "" && CheckpointName.Sanitize("///") == "", "empty result");
    Expect(CheckpointName.IsValid("CineCam Launch-1_b") && !CheckpointName.IsValid("a/b") && !CheckpointName.IsValid("a ") && !CheckpointName.IsValid(""), "IsValid");
    Expect(CheckpointName.ForTake("Launch pad", Array.Empty<string?>()) == "CineCam Launch pad", "take name");
    Expect(CheckpointName.ForTake("???", Array.Empty<string?>()) == "CineCam", "a take name without usable characters");
});

Check("two takes never share a checkpoint name", () =>
{
    string first = CheckpointName.ForTake("a.b", Array.Empty<string?>());
    string second = CheckpointName.ForTake("a:b", new string?[] { first, null });
    Expect(first == "CineCam ab" && second == "CineCam ab 2", $"second is {second}");
    string longName = CheckpointName.ForTake(new string('x', 80), Array.Empty<string?>());
    string longSecond = CheckpointName.ForTake(new string('x', 80), new string?[] { longName });
    Expect(longName.Length == 64 && longSecond.Length <= 64 && longSecond != longName && CheckpointName.IsValid(longSecond), "long names");
    Expect(CheckpointName.ForTake("A", new string?[] { "cinecam a" }) == "CineCam A 2", "case is ignored");
});

Check("the checkpoint fields are read, written and kept", () =>
{
    const string json = """
    { "takes": [ { "name": "t", "checkpoint": "CineCam t", "autoStart": true, "autoStartDelay": 5.5,
      "camera": [ { "type": "orbit", "radius": 50, "duration": 3 } ] },
      { "name": "u", "camera": [ { "type": "orbit", "radius": 50, "duration": 3 } ] } ] }
    """;
    ShotFile file = ShotFileJson.Parse(json).File;
    Take t = file.Takes[0];
    Take u = file.Takes[1];
    Expect(t.Checkpoint == "CineCam t" && t.AutoStart && t.AutoStartDelay == 5.5, "read");
    Expect(u.Checkpoint == null && !u.AutoStart && u.AutoStartDelay == 2.0, "defaults");
    string text = ShotFileJson.Write(file);
    ShotFile again = ShotFileJson.Parse(text).File;
    Expect(again.Takes[0].Checkpoint == "CineCam t" && again.Takes[0].AutoStart && again.Takes[0].AutoStartDelay == 5.5, "round trip");
    Expect(text.Split("\"autoStart\"").Length == 2 && text.Split("\"checkpoint\"").Length == 2, "the second take writes none of the fields");
});

Check("a single take form keeps the checkpoint fields", () =>
{
    const string json = """{ "checkpoint": "CineCam default", "autoStart": true, "camera": [ { "type": "orbit", "radius": 50, "duration": 3 } ] }""";
    ShotFile file = ShotFileJson.Parse(json).File;
    Expect(file.Takes[0].Checkpoint == "CineCam default" && file.Takes[0].AutoStart, "read");
    ShotFile again = ShotFileJson.Parse(ShotFileJson.Write(file)).File;
    Expect(!again.UsesTakeList && again.Takes[0].Checkpoint == "CineCam default" && again.Takes[0].AutoStart, "written back in the same form");
});

Check("a bad checkpoint name or delay is refused with the field path", () =>
{
    ShotFileException name = ExpectInvalid("""{ "takes": [ { "name": "t", "checkpoint": "a/b", "camera": [ { "type": "orbit", "radius": 50, "duration": 3 } ] } ] }""");
    Expect(name.FieldPath == "takes[0].checkpoint", name.FieldPath);
    ShotFileException delay = ExpectInvalid("""{ "takes": [ { "name": "t", "autoStartDelay": -1, "camera": [ { "type": "orbit", "radius": 50, "duration": 3 } ] } ] }""");
    Expect(delay.FieldPath == "takes[0].autoStartDelay", delay.FieldPath);
});

Check("a duplicated take has no checkpoint and keeps the start options", () =>
{
    var take = new Take { Name = "a", Checkpoint = "CineCam a", AutoStart = true, AutoStartDelay = 4.0, HideUiAt = "ignition" };
    Take copy = ShotEdit.Clone(take, "b");
    Expect(copy.Checkpoint == null && copy.AutoStart && copy.AutoStartDelay == 4.0 && copy.HideUiAt == "ignition", "copy");
});

Check("the checkpoint is recorded in the editor session and on disk", () =>
{
    var session = new EditSession();
    session.NewFromDefault("unused.json");
    string take = session.Document!.DefaultTake.Name;
    Expect(session.RecordCheckpoint(take, "CineCam x") == CheckpointRecord.Stored && session.Take!.Checkpoint == "CineCam x" && session.Dirty, "unsaved session keeps it as a change");
    Expect(session.RecordCheckpoint("missing", "CineCam y") == CheckpointRecord.NoTake, "unknown take");

    string folder = Path.Combine(Path.GetTempPath(), "cinecam-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        string path = Path.Combine(folder, "shots.json");
        File.WriteAllText(path, DefaultShotFile.Json);
        Expect(CheckpointFile.Record(path, take, "CineCam disk", out string error), error);
        Expect(ShotFileJson.Load(path).File.DefaultTake.Checkpoint == "CineCam disk" && File.Exists(path + ".bak"), "file and backup");
        Expect(!CheckpointFile.Record(path, "missing", "CineCam z", out _), "unknown take on disk");

        var clean = new EditSession();
        Expect(clean.Load(path), "load");
        Expect(clean.RecordCheckpoint(take, "CineCam saved") == CheckpointRecord.Stored && !clean.Dirty, "a clean session writes the file at once");
        Expect(ShotFileJson.Load(path).File.DefaultTake.Checkpoint == "CineCam saved", "written");
    }
    finally
    {
        Directory.Delete(folder, recursive: true);
    }
});

Check("the run counter counts earlier folders of the same take", () =>
{
    var folders = new[] { "20261107-101010-Launch_pad", "20261107-111111-Launch_pad~2", "20261107-121212-Other", "20261107-131313-x-Launch_pad", "notes" };
    Expect(TakeFolders.NextRun(folders, "Launch pad") == 3, "two earlier runs");
    Expect(TakeFolders.NextRun(folders, "Other") == 2 && TakeFolders.NextRun(folders, "New") == 1, "other takes");
    Expect(TakeFolders.FolderName(new DateTime(2026, 11, 7, 8, 9, 10), "a b") == "20261107-080910-a_b", "folder name");
    Expect(TakeFolders.Unique("x", n => n == "x" || n == "x~2") == "x~3", "unique");
    Expect(TakeFolders.Unique("x", _ => false) == "x", "free name");
});

ResetStep Run(ResetFlow flow, double now, bool take, bool ready) => flow.Step(new ResetInput(now, take, ready));

Check("a reset without a running take loads at once, waits for the vehicle and then waits for the player", () =>
{
    var flow = new ResetFlow();
    Expect(!flow.IsActive && !flow.HoldsSim, "idle");
    flow.Begin("t", "CineCam t", autoStart: false, delaySeconds: 2.0, now: 0.0);
    Expect(flow.IsActive && !flow.HoldsSim, "active");
    Expect(Run(flow, 0.1, false, false).Action == ResetAction.LoadSave, "load");
    Expect(Run(flow, 0.2, false, false).Action == ResetAction.None && flow.Phase == ResetPhase.Loading, "waits for the report of the load");
    Expect(flow.LoadReturned(true, 0.3).Length > 0 && flow.Phase == ResetPhase.WaitingVehicle, "load reported");
    Expect(Run(flow, 0.4, false, true).Action == ResetAction.None && flow.Phase == ResetPhase.WaitingVehicle, "frame 1");
    Run(flow, 0.5, false, true);
    ResetStep ready = Run(flow, 0.6, false, true);
    Expect(flow.Phase == ResetPhase.Done && ready.Action == ResetAction.None && !flow.IsActive, "ready after three frames");
    Expect(Run(flow, 9.0, false, true).Action == ResetAction.None, "nothing more happens");
});

Check("a running take ends before the load", () =>
{
    var flow = new ResetFlow();
    flow.Begin("t", "s", false, 2.0, 0.0);
    Expect(Run(flow, 0.0, true, true).Action == ResetAction.EndTake && flow.Phase == ResetPhase.EndingTake, "end");
    Expect(Run(flow, 0.1, true, true).Action == ResetAction.None, "still running");
    Expect(Run(flow, 0.2, false, true).Action == ResetAction.LoadSave, "load after the end");
});

Check("a take that does not end fails the reset", () =>
{
    var flow = new ResetFlow();
    flow.Begin("t", "s", false, 2.0, 0.0);
    Run(flow, 0.0, true, false);
    Run(flow, ResetFlow.EndTimeoutSeconds + 1.0, true, false);
    Expect(flow.Phase == ResetPhase.Failed && flow.Outcome.Contains("did not end"), flow.Outcome);
});

Check("a refused load fails the reset", () =>
{
    var flow = new ResetFlow();
    flow.Begin("t", "s", false, 2.0, 0.0);
    Run(flow, 0.0, false, false);
    string message = flow.LoadReturned(false, 0.1);
    Expect(flow.Phase == ResetPhase.Failed && message.Contains("did not load"), message);
    Expect(flow.LoadReturned(true, 0.2).Length == 0 && flow.Phase == ResetPhase.Failed, "a late report changes nothing");
});

Check("a vehicle that is not ready fails the reset after the time out and a gap restarts the frame count", () =>
{
    var flow = new ResetFlow();
    flow.Begin("t", "s", false, 2.0, 0.0);
    Run(flow, 0.0, false, false);
    flow.LoadReturned(true, 0.0);
    Run(flow, 0.1, false, true);
    Run(flow, 0.2, false, true);
    Run(flow, 0.3, false, false);
    Run(flow, 0.4, false, true);
    Expect(flow.Phase == ResetPhase.WaitingVehicle, "a gap restarts the count");
    Run(flow, ResetFlow.VehicleTimeoutSeconds + 1.0, false, false);
    Expect(flow.Phase == ResetPhase.Failed, "time out");
});

Check("an automatic start waits for the delay and holds the sim", () =>
{
    var flow = new ResetFlow();
    flow.Begin("t", "s", autoStart: true, delaySeconds: 2.0, now: 0.0);
    Expect(flow.HoldsSim, "holds");
    Run(flow, 0.0, false, false);
    flow.LoadReturned(true, 1.0);
    for (int i = 0; i < ResetFlow.ReadyFrames; i++)
        Run(flow, 1.1 + i * 0.1, false, true);
    Expect(flow.IsCountingDown && flow.HoldsSim, "counting");
    double? left = flow.SecondsToStart(1.5);
    Expect(left is > 1.5 and < 2.0, $"seconds to start {left}");
    Expect(Run(flow, 3.0, false, true).Action == ResetAction.None, "before the end of the delay");
    ResetStep start = Run(flow, 3.4, false, true);
    Expect(start.Action == ResetAction.StartTake && flow.Phase == ResetPhase.Done && !flow.HoldsSim, "start");
});

Check("a failed automatic start turns the outcome into a failure", () =>
{
    var flow = new ResetFlow();
    flow.Begin("t", "s", true, 0.0, 0.0);
    Run(flow, 0.0, false, false);
    flow.LoadReturned(true, 0.0);
    for (int i = 0; i < ResetFlow.ReadyFrames; i++)
        Run(flow, 0.1, false, true);
    Expect(Run(flow, 0.1, false, true).Action == ResetAction.StartTake, "start asked");
    Expect(flow.StartFailed("no site").Length > 0 && flow.Phase == ResetPhase.Failed, "failed");
    Expect(flow.Outcome.Contains("did not start"), flow.Outcome);
    Expect(new ResetFlow().StartFailed("x").Length == 0, "idle flow ignores it");
});

Check("a delay of zero starts at the next step and a changed vehicle cancels the countdown", () =>
{
    var zero = new ResetFlow();
    zero.Begin("t", "s", true, 0.0, 0.0);
    Run(zero, 0.0, false, false);
    zero.LoadReturned(true, 0.0);
    for (int i = 0; i < ResetFlow.ReadyFrames; i++)
        Run(zero, 0.1, false, true);
    Expect(Run(zero, 0.1, false, true).Action == ResetAction.StartTake, "zero delay");

    var gone = new ResetFlow();
    gone.Begin("t", "s", true, 5.0, 0.0);
    Run(gone, 0.0, false, false);
    gone.LoadReturned(true, 0.0);
    for (int i = 0; i < ResetFlow.ReadyFrames; i++)
        Run(gone, 0.1, false, true);
    Run(gone, 0.2, false, false);
    Expect(gone.Phase == ResetPhase.Failed, "vehicle gone");
});

Check("a cancel ends an active reset only", () =>
{
    var flow = new ResetFlow();
    Expect(flow.Cancel("x").Length == 0 && flow.Phase == ResetPhase.Idle, "nothing to cancel");
    flow.Begin("t", "s", true, 5.0, 0.0);
    Expect(flow.Cancel("stopped by the player").Contains("cancelled") && flow.Phase == ResetPhase.Cancelled && !flow.IsActive && !flow.HoldsSim, "cancelled");
    Expect(Run(flow, 1.0, false, true).Action == ResetAction.None, "no more actions");
    flow.Begin("t", "s", false, 5.0, 2.0);
    Expect(flow.Phase == ResetPhase.Requested && flow.Outcome.Length == 0, "a new reset starts clean");
});

#endregion

#region Rehearsal edit

Check("clock: the take time stops during a pause and goes on after it", () =>
{
    var clock = new TakeClock(100.0);
    Expect(Near(clock.Seconds(103.0), 3.0), "runs before the pause");
    Expect(clock.Pause(103.0) && clock.IsPaused, "pauses");
    Expect(!clock.Pause(105.0), "a second pause is refused");
    Expect(Near(clock.Seconds(103.0), 3.0) && Near(clock.Seconds(160.0), 3.0), "constant during the pause");
    Expect(Near(clock.PausedSeconds(113.0), 10.0), "the running pause counts");
    Expect(Near(clock.Resume(113.0), 10.0) && !clock.IsPaused, "the real length of the pause");
    Expect(Near(clock.Seconds(113.0), 3.0), "no gap after the resume");
    Expect(Near(clock.Seconds(115.0), 5.0), "goes on from the frozen time");
    Expect(Near(clock.Resume(120.0), 0.0), "a resume without a pause does nothing");
});

Check("clock: several pauses add up and the time never goes back", () =>
{
    var clock = new TakeClock(0.0);
    clock.Pause(10.0);
    clock.Resume(30.0);
    clock.Pause(40.0);
    clock.Resume(45.0);
    Expect(clock.PauseCount == 2 && Near(clock.PausedSeconds(50.0), 25.0), "two pauses, 25 s");
    Expect(Near(clock.LastPauseSeconds, 5.0), "last pause");
    Expect(Near(clock.Seconds(50.0), 25.0), "50 - 25");
    Expect(clock.Seconds(-5.0) == 0.0, "never below 0");
});

Check("director: a pause freezes the take, and an event seen in it keeps the frozen time", () =>
{
    ShotFile file = ShotFileJson.Parse("""
    { "camera": [ { "type": "tripod", "position": [1,2,3], "lookAt": [0,0,0], "duration": 4, "fov": 30 },
                  { "type": "tripod", "position": [4,5,6], "lookAt": [0,0,0], "duration": 4, "fov": 50, "at": { "event": "ignition", "delay": 1 } } ],
      "speed": [ { "at": { "event": "ignition" }, "to": 0.5, "ramp": 2 } ] }
    """).File;
    var director = new TakeDirector(file.DefaultTake, file);
    var clock = new TakeClock(1000.0);
    director.Update(clock.Seconds(1001.0), World(Sample()));
    clock.Pause(1002.0);
    double frozen = clock.Seconds(1002.0);
    var before = director.Update(frozen, World(Sample()));
    var during = director.Update(clock.Seconds(1050.0), World(Sample(engine: true)));
    Expect(Near(frozen, 2.0), "frozen time");
    Expect(during.NewEvents.Single().Name == "ignition" && Near(during.NewEvents[0].TakeTime, 2.0), "the event has the frozen time");
    Expect(Near(director.Events.Seen["ignition"], 2.0), "seen time");
    Expect(before.ShotIndex == 0 && during.ShotIndex == 0 && Near(during.LocalTime, 2.0), "the shot does not advance");
    Expect(Near(during.TargetSpeed, 1.0), "the speed ramp has not moved");
    clock.Resume(1060.0);
    var after = director.Update(clock.Seconds(1062.0), World(Sample(engine: true)));
    Expect(Near(clock.Seconds(1062.0), 4.0), "the take time went on from 2 s");
    Expect(after.ShotIndex == 1 && Near(director.Timeline.ShotStart(1)!.Value, 3.0), "the shot with a delay of 1 s started at 3 s of take time");
    Expect(Near(director.Timeline.SpeedKeyStart(0)!.Value, 2.0), "the speed key starts at the frozen event time");
});

Check("director: the frame holds the progress of the shot", () =>
{
    ShotFile file = ShotFileJson.Parse("""
    { "camera": [ { "type": "orbit", "radius": 50, "duration": 10, "degPerSec": 10 } ] }
    """).File;
    var director = new TakeDirector(file.DefaultTake, file);
    var frame = director.Update(4.0, World(Sample()));
    Expect(Near(frame.LocalTime, 4.0) && Near(frame.Progress, 0.4), "local time and progress");
});

Check("rehearsal: the speed to remember skips a stopped sim", () =>
{
    Expect(Near(RehearsalEdit.SpeedToRemember(0.25, 4.0, 1.0), 0.25), "the current speed");
    Expect(Near(RehearsalEdit.SpeedToRemember(0.0, 4.0, 1.0), 4.0), "the last written speed");
    Expect(Near(RehearsalEdit.SpeedToRemember(0.0, null, 2.0), 2.0), "the speed before the take");
    Expect(Near(RehearsalEdit.SpeedToRemember(0.0, 0.0, 0.0), 1.0), "1x as the last resort");
});

Check("rehearsal: the trigger is the latest seen event plus the seconds since it", () =>
{
    var none = new Dictionary<string, string>();
    var seen = Events(("ignition", 38.1), ("liftoff", 39.8), ("sequence:2", 210.3));
    Trigger t = RehearsalEdit.TriggerHere(seen, none, 214.55);
    Expect(t.Kind == TriggerKind.Event && t.Event == "sequence:2" && Near(t.Delay, 4.2), "sequence:2 +4.2: " + t.Event + " " + t.Delay);
    Trigger early = RehearsalEdit.TriggerHere(seen, none, 39.0);
    Expect(early.Event == "ignition" && Near(early.Delay, 0.9), "an event in the future is ignored");
    Trigger noEvent = RehearsalEdit.TriggerHere(Events(), none, 12.34);
    Expect(noEvent.Kind == TriggerKind.Time && Near(noEvent.Time, 12.3), "a take time without an event");
});

Check("rehearsal: an alias of the file names the event", () =>
{
    var aliases = new Dictionary<string, string> { ["fairing"] = "sequence:3", ["stage2"] = "sequence:2" };
    Trigger t = RehearsalEdit.TriggerHere(Events(("sequence:3", 100.0)), aliases, 102.0);
    Expect(t.Event == "fairing" && Near(t.Delay, 2.0), "alias: " + t.Event);
});

Check("rehearsal: a speed key is added or moved and the file stays valid", () =>
{
    ShotFile file = ShotFileJson.Parse(DirectorFile).File;
    Take take = file.DefaultTake;
    int index = RehearsalEdit.AddSpeedKey(take, Trigger.OnEvent("liftoff", 12.5), 4.0, 2.0);
    Expect(index == take.Speed.Count - 1 && Near(take.Speed[index].To, 4.0) && Near(take.Speed[index].Ramp, 2.0), "added");
    Expect(RehearsalEdit.MoveSpeedKey(take, index, Trigger.OnEvent("burnout", 1.0), 0.5), "moved");
    Expect(take.Speed[index].At.Event == "burnout" && Near(take.Speed[index].To, 0.5) && Near(take.Speed[index].Ramp, 2.0), "ramp stays");
    Expect(!RehearsalEdit.MoveSpeedKey(take, 99, Trigger.AtTime(1), 1.0), "bad index");
    Expect(ShotFileValidator.Validate(file).IsValid, "the file is valid");
});

Check("capture: an orbit passes through the camera at this moment and keeps its turn", () =>
{
    var shot = new Shot { Type = ShotType.Orbit, Duration = 10.0, Radius = 100.0, Height = 5.0, DegPerSec = 6.0, StartDeg = 0.0, Fov = 40.0, Target = ShotTarget.Site };
    var vehicle = new Basis3(V3.Zero, V3.UnitX, V3.UnitY, V3.UnitZ);
    var camera = new CapturedCamera(new V3(30.0, 60.0, 80.0), new V3(0.0, -0.6, -0.8), 25.0);
    Expect(ShotCapture.ApplyAtProgress(shot, camera, vehicle, 40.0, null, 0.5, out string message), message);
    Expect(Near(shot.Radius, 100.0) && Near(shot.Height, 30.0) && Near(shot.DegPerSec, 6.0) && Near(shot.Fov, 25.0), "radius, height, turn, fov");
    CameraPose pose = PoseMath.Evaluate(shot, 0.5, new PoseInputs());
    Expect(Near3(pose.Position, camera.Position, 0.02), "the orbit is at the camera at 50 percent: " + pose.Position);
});

Check("capture: an orbit in the vehicle frame is written in that frame", () =>
{
    var shot = new Shot { Type = ShotType.Orbit, Duration = 8.0, Radius = 20.0, DegPerSec = -10.0, Target = ShotTarget.Vehicle };
    var vehicle = new Basis3(new V3(1000.0, 0, 0), V3.UnitX, V3.UnitY, V3.UnitZ);
    var camera = new CapturedCamera(new V3(1010.0, 30.0, -40.0), new V3(0, -0.6, 0.8), 40.0);
    Expect(ShotCapture.ApplyAtProgress(shot, camera, vehicle, 40.0, null, 0.25, out string message), message);
    CameraPose pose = PoseMath.Evaluate(shot, 0.25, new PoseInputs());
    Expect(Near3(pose.Position, new V3(10.0, 30.0, -40.0), 0.02), "local position: " + pose.Position);
    Expect(shot.StartDeg >= 0.0 && shot.StartDeg < 360.0, "the start angle is wrapped");
});

Check("capture: an orbit refuses a camera on its axis", () =>
{
    var shot = new Shot { Type = ShotType.Orbit, Duration = 8.0, Radius = 20.0, Target = ShotTarget.Site };
    var camera = new CapturedCamera(new V3(50.0, 0.0, 0.1), V3.UnitZ, 40.0);
    Expect(!ShotCapture.ApplyAtProgress(shot, camera, Basis3.Identity, 40.0, null, 0.0, out string message) && message.Length > 0, "refused with a reason");
    Expect(Near(shot.Radius, 20.0), "the shot did not change");
});

Check("capture: a crane keeps its rise and passes through the camera height", () =>
{
    var shot = new Shot { Type = ShotType.Crane, Duration = 10.0, From = 0.0, To = 30.0, Radius = 14.0, Azimuth = 200.0, Target = ShotTarget.Site };
    var camera = new CapturedCamera(new V3(25.0, 0.0, -40.0), V3.UnitZ, 40.0);
    Expect(ShotCapture.ApplyAtProgress(shot, camera, Basis3.Identity, 40.0, null, 0.5, out string message), message);
    Expect(Near(shot.To - shot.From, 30.0), "the rise stays");
    CameraPose pose = PoseMath.Evaluate(shot, 0.5, new PoseInputs());
    Expect(Near3(pose.Position, camera.Position, 0.02), "the crane is at the camera: " + pose.Position);
});

Check("capture: the other types still use the fixed pose", () =>
{
    var shot = new Shot { Type = ShotType.Tripod, Duration = 5.0, Target = ShotTarget.Site };
    var camera = new CapturedCamera(new V3(3.0, -60.0, -45.0), new V3(0.0, 0.8, 0.6), 30.0);
    Expect(ShotCapture.ApplyAtProgress(shot, camera, Basis3.Identity, 40.0, null, 0.7, out string message), message);
    Expect(Near3(shot.Position, camera.Position, 0.01) && Near(shot.Fov, 30.0), "position and fov");
});

#endregion

#region Actions

const string actionFile = """
{
  "events": { "fairing": "sequence:3" },
  "takes": [
    {
      "name": "launch",
      "camera": [ { "type": "tripod", "position": [1, 2, 3], "lookAt": [4, 5, 6], "duration": 50 } ],
      "actions": [
        { "at": { "event": "sequence:2", "delay": 12.5 }, "action": "stage", "sequence": 3, "note": "fairing" },
        { "at": 30, "action": "Stage" },
      ],
    },
  ],
}
""";

Check("actions: the reader reads the track and keeps unknown fields", () =>
{
    Take take = ShotFileJson.Parse(actionFile).File.DefaultTake;
    Expect(take.Actions.Count == 2, "two actions");
    Expect(take.Actions[0].Kind == ActionKind.Stage && take.Actions[0].Sequence == 3, "kind and sequence");
    Expect(take.Actions[0].At.Kind == TriggerKind.Event && Near(take.Actions[0].At.Delay, 12.5), "event trigger");
    Expect(take.Actions[0].Extras["note"].GetString() == "fairing", "extra kept");
    Expect(take.Actions[1].Sequence == null && take.Actions[1].At.Kind == TriggerKind.Time, "time trigger, any sequence");
});

Check("actions: the file round trips with the same text", () =>
{
    ShotFile file = ShotFileJson.Parse(actionFile).File;
    string first = ShotFileJson.Write(file);
    string second = ShotFileJson.Write(ShotFileJson.Parse(first).File);
    Expect(first == second, "stable text");
    Expect(first.Contains("\"actions\"") && first.Contains("\"action\": \"stage\""), "written");
});

Check("actions: a take without actions writes no actions field", () =>
{
    ShotFile file = ShotFileJson.Parse(minimal).File;
    Expect(file.DefaultTake.Actions.Count == 0 && !ShotFileJson.Write(file).Contains("actions"), "no field");
});

Check("actions: bad actions name the field", () =>
{
    string Wrap(string action) => "{ \"camera\": [ { \"type\": \"tripod\", \"position\": [1,2,3], \"lookAt\": [4,5,6], \"duration\": 5 } ], \"actions\": [ " + action + " ] }";
    Expect(ExpectInvalid(Wrap("{ \"action\": \"stage\" }")).FieldPath == "actions[0].at", "missing trigger");
    Expect(ExpectInvalid(Wrap("{ \"at\": 5 }")).FieldPath == "actions[0].action", "missing kind");
    Expect(ExpectInvalid(Wrap("{ \"at\": 5, \"action\": \"launch\" }")).FieldPath == "actions[0].action", "unknown kind");
    Expect(ExpectInvalid(Wrap("{ \"at\": 5, \"action\": \"stage\", \"sequence\": 2.5 }")).FieldPath == "actions[0].sequence", "fraction");
    Expect(ExpectInvalid(Wrap("{ \"at\": 5, \"action\": \"stage\", \"sequence\": 0 }")).FieldPath == "actions[0].sequence", "range");
    Expect(ExpectInvalid(Wrap("{ \"at\": { \"event\": \"nonsense\" }, \"action\": \"stage\" }")).FieldPath == "actions[0].at.event", "event name");
});

Check("actions: the validator finds an unknown event in an action", () =>
{
    ShotFile file = ShotFileJson.Parse(actionFile).File;
    Expect(ShotFileValidator.Validate(file).IsValid, "valid");
    file.DefaultTake.Actions[0].At = Trigger.OnEvent("nonsense");
    Expect(!ShotFileValidator.Validate(file).IsValid, "invalid");
});

Check("actions: the timeline starts an action once, at the event time plus the delay", () =>
{
    Take take = ShotFileJson.Parse(actionFile).File.DefaultTake;
    var runner = new TimelineRunner(take, new Dictionary<string, string>());
    Expect(runner.Update(10.0, Events()).StartedActions.Count == 0, "the event action waits");
    Expect(runner.ActionStart(0) is null && !runner.ActionStarted(0), "waiting");
    TimelineState s = runner.Update(40.0, Events(("sequence:2", 20.0)));
    Expect(Near(runner.ActionStart(0)!.Value, 32.5), "start is event time plus delay");
    Expect(s.StartedActions.Contains(0) && s.StartedActions.Contains(1), "both started");
    Expect(runner.Update(41.0, Events(("sequence:2", 20.0))).StartedActions.Count == 0, "reported once");
    Expect(runner.ActionStarted(0), "marked started");
});

Check("actions: an action before its time does not start", () =>
{
    Take take = ShotFileJson.Parse(actionFile).File.DefaultTake;
    var runner = new TimelineRunner(take, new Dictionary<string, string>());
    TimelineState s = runner.Update(29.9, Events());
    Expect(s.StartedActions.Count == 0, "not yet");
    s = runner.Update(30.0, Events());
    Expect(s.StartedActions.Count == 1 && s.StartedActions[0] == 1, "time action at 30 s");
});

Check("actions: the director reports started actions and watches their events", () =>
{
    ShotFile file = ShotFileJson.Parse(actionFile).File;
    Take take = file.DefaultTake;
    take.Actions.Add(new TakeAction { At = Trigger.OnEvent("altitude:90000", -3.0), Sequence = 4 });
    Expect(EventDetector.Watched(take, file.Events).Any(e => e.Kind == EventKind.Altitude && e.Value == 90000.0), "altitude watched");
    var director = new TakeDirector(take, file);
    var world = new WorldSample { VehicleHeight = 10.0 };
    DirectorFrame frame = director.Update(31.0, world);
    Expect(frame.StartedActions.Contains(1), "the time action started");
});

Check("actions: the editor helpers add, clone and describe actions", () =>
{
    var take = new Take();
    int index = RehearsalEdit.AddStageAction(take, Trigger.OnEvent("sequence:2", 4.0), 3);
    Expect(index == 0 && take.Actions[0].Sequence == 3, "added");
    Expect(RehearsalEdit.AddStageAction(take, Trigger.AtTime(1.0), 0) == 1 && take.Actions[1].Sequence == null, "zero means any");
    TakeAction copy = ShotEdit.Clone(take.Actions[0]);
    copy.At.Delay = 9.0;
    Expect(Near(take.Actions[0].At.Delay, 4.0), "the clone shares nothing");
    Expect(ShotEdit.Describe(0, take.Actions[0]).Contains("stage sequence 3"), "describe: " + ShotEdit.Describe(0, take.Actions[0]));
    Take taken = ShotEdit.Clone(take, "copy");
    Expect(taken.Actions.Count == 2, "a duplicated take keeps its actions");
});

Check("actions: the session adds, moves and removes actions", () =>
{
    var session = new EditSession();
    session.NewFromDefault("x.json");
    int before = session.Take!.Actions.Count;
    session.AddAction();
    session.AddAction();
    Expect(session.Take!.Actions.Count == before + 2 && session.ActionIndex == before + 1, "added");
    session.MoveAction(-1);
    Expect(session.ActionIndex == before, "moved");
    session.RemoveAction();
    Expect(session.Take!.Actions.Count == before + 1, "removed");
    Expect(ShotFileValidator.Validate(session.Document!).IsValid, "the default action is valid");
});

Check("stage guard: fires the wanted sequence once and waits for the key to take effect", () =>
{
    var guard = new StageGuard();
    StageDecision a = guard.Decide(3, 3, 2, 10.0);
    Expect(a.Verdict == StageVerdict.Fire, a.Reason);
    StageDecision b = guard.Decide(null, 3, 2, 10.02);
    Expect(b.Verdict == StageVerdict.Wait, "a second key waits while the first is queued");
    StageDecision c = guard.Decide(null, 4, 3, 10.1);
    Expect(c.Verdict == StageVerdict.Fire, "after the sequence changed the next one may fire");
});

Check("stage guard: skips a wrong number or an empty list", () =>
{
    var guard = new StageGuard();
    Expect(guard.Decide(3, 4, 3, 5.0).Verdict == StageVerdict.Skip, "3 is already done");
    Expect(guard.Decide(null, -1, 3, 5.0).Verdict == StageVerdict.Skip, "none left");
    Expect(guard.Decide(2, 2, 1, 5.0).Verdict == StageVerdict.Fire, "the wanted one fires after skips");
});

Check("stage guard: waits for an earlier sequence, then gives up", () =>
{
    var guard = new StageGuard();
    Expect(guard.Decide(3, 2, 1, 100.0).Verdict == StageVerdict.Wait, "the next one is 2, so 3 waits");
    Expect(guard.Decide(3, 2, 1, 105.0).Verdict == StageVerdict.Wait, "still waits");
    Expect(guard.Decide(3, 3, 2, 106.0).Verdict == StageVerdict.Fire, "fires when 3 is next");
    var late = new StageGuard();
    Expect(late.Decide(3, 2, 1, 0.0).Verdict == StageVerdict.Wait, "start of the wait");
    Expect(late.Decide(3, 2, 1, StageGuard.EarlierSequenceTimeoutSeconds + 1.0).Verdict == StageVerdict.Skip, "gives up after the timeout");
    Expect(late.Decide(3, 2, 1, StageGuard.EarlierSequenceTimeoutSeconds + 1.5).Verdict == StageVerdict.Wait, "a new wait starts after the skip");
});

Check("stage guard: a key that never took effect times out", () =>
{
    var guard = new StageGuard();
    Expect(guard.Decide(null, 3, 2, 1.0).Verdict == StageVerdict.Fire, "first");
    Expect(guard.Decide(null, 3, 2, 2.0).Verdict == StageVerdict.Wait, "still queued");
    Expect(guard.Decide(null, 3, 2, 1.0 + StageGuard.PendingTimeoutSeconds + 0.5).Verdict == StageVerdict.Fire, "lost key, fire again");
});

#endregion

#region Local horizon and fired sequences

V3 BodyCentre = new(-6.4e6, 0, 0);
V3 PoleAxis = V3.UnitZ;

Basis3 PitchedVehicle(V3 origin, double pitchDeg)
{
    double a = pitchDeg * Math.PI / 180.0;
    var nose = new V3(Math.Cos(a), 0.0, Math.Sin(a));
    return Basis3.FromXAndHint(origin, nose, V3.UnitY);
}

Shot LocalChase(V3 offset, V3 lookAt) => new()
{
    Type = ShotType.Chase,
    Target = ShotTarget.Vehicle,
    Frame = ShotFrame.Local,
    Offset = offset,
    LookAt = lookAt,
    Duration = 5,
    Smoothing = 0,
};

Check("local frame: axes are up, east and north and right handed", () =>
{
    var origin = new V3(8e4, 4e5, 3e5);
    Basis3 f = LocalHorizon.Frame(origin, BodyCentre, PoleAxis);
    V3 radial = (origin - BodyCentre).NormalizedOr(V3.UnitX);
    Expect(Near3(f.X, radial), "X is away from the body center");
    Expect(Near3(V3.Cross(f.X, f.Y), f.Z), "right handed");
    Expect(Near(f.Y.Length, 1.0) && Near(f.Z.Length, 1.0) && Near(V3.Dot(f.X, f.Z), 0.0, 1e-9), "unit and perpendicular");
    Expect(V3.Dot(f.Z, PoleAxis) > 0.9, "north points toward the pole");
    Expect(Near3(LocalHorizon.Frame(origin, null, null).X, V3.UnitX), "without a body center the site axes stand in");
    Basis3 overPole = LocalHorizon.Frame(BodyCentre + new V3(0, 0, 6.5e6), BodyCentre, PoleAxis);
    Expect(Near(overPole.X.Length, 1.0) && Near(overPole.Y.Length, 1.0) && Near(overPole.Z.Length, 1.0), "defined over the pole");
});

Check("local frame: the offset follows the horizon, not the pitch of the vehicle", () =>
{
    var origin = new V3(1e5, 6e5, 2e5);
    Shot shot = LocalChase(new V3(10, 40, 0), V3.Zero);
    var inputs = new PoseInputs { BodyCentre = BodyCentre, Pole = PoleAxis };
    CameraPose flat = PoseMath.EvaluateSite(shot, 0.5, inputs, PitchedVehicle(origin, 0));
    CameraPose steep = PoseMath.EvaluateSite(shot, 0.5, inputs, PitchedVehicle(origin, 70));
    Expect(Near3(flat.Position, steep.Position), "same camera position for any pitch");
    Basis3 f = LocalHorizon.Frame(origin, BodyCentre, PoleAxis);
    Expect(Near3(flat.Position, origin + f.X * 10 + f.Y * 40), "10 m up and 40 m east of the vehicle");
    Expect(Near3(flat.LookAt, origin), "looks at the vehicle");
    V3 radialAtCamera = (flat.Position - BodyCentre).NormalizedOr(V3.UnitX);
    Expect(Near3(flat.Up, radialAtCamera), "the default up of a local shot is the horizon");
});

Check("local frame: a vehicle frame shot keeps the vehicle up unless up is horizon", () =>
{
    var origin = new V3(1e5, 6e5, 2e5);
    var shot = new Shot { Type = ShotType.Chase, Target = ShotTarget.Vehicle, Offset = new V3(-5, 8, 0), LookAt = new V3(10, 0, 0), Duration = 3, Smoothing = 0 };
    var inputs = new PoseInputs { BodyCentre = BodyCentre, Pole = PoleAxis };
    Basis3 vehicle = PitchedVehicle(origin, 40);
    CameraPose old = PoseMath.EvaluateSite(shot, 0.0, inputs, vehicle);
    CameraPose reference = PoseMath.ToSiteFrame(PoseMath.Evaluate(shot, 0.0, inputs), vehicle);
    Expect(Near3(old.Position, reference.Position) && Near3(old.Up, reference.Up), "unchanged for old files");
    shot.Up = ShotUp.Horizon;
    CameraPose level = PoseMath.EvaluateSite(shot, 0.0, inputs, vehicle);
    Expect(Near3(level.Position, old.Position), "up does not move the camera");
    Expect(Near3(level.Up, (level.Position - BodyCentre).NormalizedOr(V3.UnitX)), "up is away from the body center");
    shot.Roll = 90;
    CameraPose rolled = PoseMath.EvaluateSite(shot, 0.0, inputs, vehicle);
    V3 forward = (rolled.LookAt - rolled.Position).NormalizedOr(V3.UnitX);
    Expect(Near(V3.Dot(rolled.Up, forward), 0.0, 1e-9) && Near(V3.Dot(rolled.Up, level.Up), 0.0, 1e-6), "roll turns the horizon up about the view");
});

Check("local frame: a part shot has its origin at the part", () =>
{
    var origin = new V3(1e5, 6e5, 2e5);
    Basis3 vehicle = PitchedVehicle(origin, 30);
    Shot shot = LocalChase(new V3(0, 5, 0), V3.Zero);
    shot.Type = ShotType.Part;
    var inputs = new PoseInputs { BodyCentre = BodyCentre, Pole = PoleAxis, PartPosition = new V3(12, 0, 0) };
    CameraPose pose = PoseMath.EvaluateSite(shot, 0.0, inputs, vehicle);
    V3 partSite = vehicle.ToParent(new V3(12, 0, 0));
    Basis3 f = LocalHorizon.Frame(partSite, BodyCentre, PoleAxis);
    Expect(Near3(pose.LookAt, partSite) && Near3(pose.Position, partSite + f.Y * 5), "the part is the origin");
});

Check("local frame: a camera that looks straight down keeps a defined up", () =>
{
    var origin = new V3(1e5, 6e5, 2e5);
    Shot shot = LocalChase(new V3(200, 0, 0), V3.Zero);
    var inputs = new PoseInputs { BodyCentre = BodyCentre, Pole = PoleAxis };
    CameraPose pose = PoseMath.EvaluateSite(shot, 0.0, inputs, PitchedVehicle(origin, 0));
    V3 forward = pose.LookAt - pose.Position;
    var stabilizer = new UpStabilizer();
    V3 up = stabilizer.Stabilize(forward, pose.Up);
    Expect(Near(up.Length, 1.0) && Near(V3.Dot(up, forward.NormalizedOr(V3.UnitX)), 0.0, 1e-9), "unit and perpendicular on the pole");
    V3 nearPole = stabilizer.Stabilize(forward + new V3(0, 3, 0), pose.Up);
    Expect(Near(nearPole.Length, 1.0) && (nearPole - up).Length < 1.0, "no jump next to the pole");
});

Check("local frame: frame and up are read, written and kept in a clone", () =>
{
    const string json = """
    { "camera": [
      { "type": "chase", "frame": "local", "offset": [10, 40, 0], "lookAt": [0, 0, 0], "duration": 3 },
      { "type": "part", "part": "x", "frame": "local", "up": "vehicle", "offset": [1, 2, 3], "duration": 3 },
      { "type": "chase", "offset": [1, 2, 3], "up": "horizon", "duration": 3, "roll": 5 },
      { "type": "chase", "offset": [1, 2, 3], "duration": 3 } ] }
    """;
    ShotFile file = ShotFileJson.Parse(json).File;
    IReadOnlyList<Shot> shots = file.DefaultTake.Shots;
    Expect(shots[0].Frame == ShotFrame.Local && shots[0].Up == null && shots[0].EffectiveUp == ShotUp.Horizon, "local defaults to the horizon");
    Expect(shots[1].EffectiveUp == ShotUp.Vehicle, "an explicit up wins");
    Expect(shots[2].Frame == ShotFrame.Vehicle && shots[2].EffectiveUp == ShotUp.Horizon, "horizon with the vehicle frame");
    Expect(shots[3].Up == null && shots[3].EffectiveUp == ShotUp.Vehicle && shots[3].Frame == ShotFrame.Vehicle, "old files behave as before");
    string written = ShotFileJson.Write(file);
    ShotFile again = ShotFileJson.Parse(written).File;
    IReadOnlyList<Shot> back = again.DefaultTake.Shots;
    Expect(back[0].Frame == ShotFrame.Local && back[0].Up == null, "frame survives a round trip");
    Expect(back[1].Frame == ShotFrame.Local && back[1].Up == ShotUp.Vehicle, "explicit up survives");
    Expect(back[2].Up == ShotUp.Horizon && Near(back[2].Roll, 5.0), "up with roll survives");
    Expect(back[3].Up == null && back[3].Frame == ShotFrame.Vehicle, "an old shot stays old");
    Shot copy = ShotEdit.Clone(shots[2]);
    Expect(copy.Up == ShotUp.Horizon && Near(copy.Roll, 5.0) && copy.Frame == ShotFrame.Vehicle, "clone keeps up and roll");
    ShotFileException bad = ExpectInvalid("{ \"camera\": [ { \"type\": \"chase\", \"frame\": \"sideways\", \"offset\": [1,2,3], \"duration\": 3 } ] }");
    Expect(bad.FieldPath.Contains("frame"), "a bad frame is named: " + bad.FieldPath);
});

Check("local frame: apply camera writes the offset in the local frame", () =>
{
    var origin = new V3(1e5, 6e5, 2e5);
    Basis3 vehicle = PitchedVehicle(origin, 55);
    Basis3 f = LocalHorizon.Frame(origin, BodyCentre, PoleAxis);
    V3 camPos = origin + f.X * 7 + f.Y * -30 + f.Z * 12;
    V3 forward = (origin - camPos).NormalizedOr(V3.UnitX);
    var camera = new CapturedCamera(camPos, forward, 35);
    Shot shot = LocalChase(V3.Zero, V3.Zero);
    Expect(ShotCapture.Apply(shot, camera, vehicle, 0.0, null, out _, BodyCentre, PoleAxis), "applied");
    Expect(Near3(shot.Offset, new V3(7, -30, 12), 0.01), "offset in the local frame: " + shot.Offset);
    var inputs = new PoseInputs { BodyCentre = BodyCentre, Pole = PoleAxis };
    CameraPose pose = PoseMath.EvaluateSite(shot, 0.0, inputs, vehicle);
    Expect(Near3(pose.Position, camPos, 0.05), "the shot puts the camera back where it was");
});

Check("fired sequence: the call that empties a sequence is recognized", () =>
{
    Expect(SequenceFire.Fired(new SequenceSnapshot(3, 2, 3), new SequenceSnapshot(-1, 2, 2)) == 3, "fairing sequence removed at once");
    Expect(SequenceFire.Fired(new SequenceSnapshot(3, 2, 4), new SequenceSnapshot(4, 3, 4)) == 3, "normal fire");
    Expect(SequenceFire.Fired(new SequenceSnapshot(3, 2, 3), new SequenceSnapshot(3, 2, 3)) == null, "a call that changed nothing");
    Expect(SequenceFire.Fired(new SequenceSnapshot(-1, 3, 3), new SequenceSnapshot(-1, 3, 3)) == null, "nothing left to fire");
});

Check("fired sequence: the flight with the fairing is reported as sequence:3", () =>
{
    var detector = new EventDetector(Array.Empty<EventName>());
    detector.Update(0.0, Sample(sequence: 0));
    detector.Update(66.8, Sample(engine: true, sequence: 1));
    var two = detector.Update(177.6, Sample(contact: false, sequence: 2, altitude: 100000));
    Expect(two.Any(e => e.Name == "sequence:2"), "sequence 2 is seen from the active number");
    Expect(detector.Update(190.0, Sample(contact: false, sequence: 2, altitude: 105000)).Count == 0, "quiet before the key");
    var found = detector.Update(192.4, new VehicleSample(false, false, 2, 110000, 7000, new[] { 3 }));
    Expect(found.Count == 1 && found[0].Name == "sequence:3", "the fired list reports sequence 3 although the active number is 2 again");
    Expect(found[0].Detail.Contains("left the vehicle"), "the detail names the reason");
    Expect(detector.Update(192.5, Sample(contact: false, sequence: 2, altitude: 110100)).Count == 0, "not reported twice");
    Expect(detector.Seen.ContainsKey("sequence:3"), "a shot that waits for sequence 3 can start");
});

Check("fired sequence: the fire and the active number together report once, in order", () =>
{
    var detector = new EventDetector(Array.Empty<EventName>());
    detector.Update(0.0, Sample(sequence: 1));
    var found = detector.Update(1.0, new VehicleSample(false, true, 3, 0, 0, new[] { 3 }));
    Expect(found.Select(e => e.Name).SequenceEqual(new[] { "sequence:2", "sequence:3" }), string.Join(",", found.Select(e => e.Name)));
});

Check("fired sequence: the stage guard does not wait for a sequence that is gone", () =>
{
    var guard = new StageGuard();
    Expect(guard.Decide(3, 3, 2, 192.0).Verdict == StageVerdict.Fire, "fires sequence 3");
    StageDecision after = guard.Decide(null, -1, 2, 192.1);
    Expect(after.Verdict == StageVerdict.Skip, "a second action sees that nothing is left: " + after.Reason);
});

#endregion

#region Path shots and blends

const string pathShotJson = """
{ "camera": [ { "type": "path", "duration": 10, "fov": 35, "roll": 2,
  "points": [
    { "t": 0, "position": [3, -80, -60], "lookAt": [15, 0, 0] },
    { "t": 0.5, "position": [10, -30, -20], "lookAt": [15, 0, 0], "fov": 25, "roll": 10 },
    { "t": 1, "position": [30, -10, -5], "lookAt": [25, 0, 0] },
  ] } ] }
""";

List<PathPoint> SamplePoints() => new()
{
    new PathPoint { T = 0.0, Position = new V3(0, 0, 0), LookAt = new V3(0, 0, 10), Fov = 40, Roll = 0 },
    new PathPoint { T = 0.2, Position = new V3(10, 4, 0), LookAt = new V3(0, 5, 10), Fov = 20, Roll = 15 },
    new PathPoint { T = 0.7, Position = new V3(14, -6, 3), LookAt = new V3(2, 5, 12), Fov = 20, Roll = -5 },
    new PathPoint { T = 1.0, Position = new V3(20, 0, 0), LookAt = new V3(4, 5, 12), Fov = 50, Roll = 0 },
};

Check("path: the reader fills the defaults and keeps the points", () =>
{
    Take take = ParseTake(pathShotJson, out _);
    Shot shot = take.Shots.Single();
    Expect(shot.Type == ShotType.Path && shot.Target == ShotTarget.Site, "type and default target");
    Expect(shot.Ease == Ease.InOut, "a path eases in and out by default");
    Expect(shot.Points.Count == 3 && Near(shot.Points[1].T, 0.5), "points");
    Expect(shot.Points[0].Fov == null && Near(shot.Points[1].Fov!.Value, 25) && Near(shot.Points[1].Roll!.Value, 10), "optional fields");
    Expect(Near3(shot.Points[2].LookAt, new V3(25, 0, 0)), "look point");

    Take named = ParseTake(pathShotJson.Replace("\"type\": \"path\",", "\"type\": \"path\", \"ease\": \"none\", \"target\": \"vehicle\", \"frame\": \"local\","), out _);
    Shot local = named.Shots.Single();
    Expect(local.Ease == Ease.None && local.Target == ShotTarget.Vehicle && local.UsesLocalFrame, "explicit ease, target and frame");
});

Check("path: the reader names a bad point and the writer round-trips", () =>
{
    string Path(string points) => "{ \"camera\": [ { \"type\": \"path\", \"duration\": 4, \"points\": " + points + " } ] }";
    ShotFileException e = ExpectInvalid(Path("[]"));
    Expect(e.FieldPath == "camera[0].points", "empty list: " + e.FieldPath);
    e = ExpectInvalid("{ \"camera\": [ { \"type\": \"path\", \"duration\": 4 } ] }");
    Expect(e.FieldPath == "camera[0].points", "missing list: " + e.FieldPath);
    e = ExpectInvalid(Path("[ { \"t\": 0, \"lookAt\": [0,0,0] } ]"));
    Expect(e.FieldPath == "camera[0].points[0].position", "missing position: " + e.FieldPath);
    e = ExpectInvalid(Path("[ { \"t\": 0.6, \"position\": [0,0,0], \"lookAt\": [0,0,0] }, { \"t\": 0.4, \"position\": [1,0,0], \"lookAt\": [0,0,0] } ]"));
    Expect(e.FieldPath == "camera[0].points", "t out of order: " + e.FieldPath);
    e = ExpectInvalid(Path("[ { \"t\": 1.5, \"position\": [0,0,0], \"lookAt\": [0,0,0] } ]"));
    Expect(e.FieldPath == "camera[0].points[0].t", "t out of range: " + e.FieldPath);
    e = ExpectInvalid(Path("[ { \"t\": 0, \"position\": [0,0,0], \"lookAt\": [0,0,0], \"colour\": 1 } ]"));
    Expect(e.FieldPath == "camera[0].points[0].colour", "unknown point field: " + e.FieldPath);

    ShotFile file = ShotFileJson.Parse(pathShotJson).File;
    string text = ShotFileJson.Write(file);
    Expect(text.All(c => c < 128), "ASCII");
    ShotFile again = ShotFileJson.Parse(text).File;
    Shot a = file.DefaultTake.Shots[0], b = again.DefaultTake.Shots[0];
    Expect(b.Points.Count == 3 && Near3(b.Points[1].Position, a.Points[1].Position) && b.Points[1].Fov == 25 && b.Points[0].Fov == null, "points survive");
    Expect(ShotFileJson.Write(again) == text, "the second write is the same");
});

Check("blend: a blend without seconds takes the default length", () =>
{
    const string shot = "{ \"type\": \"chase\", \"offset\": [1,2,3], \"duration\": 1, \"transition\": ";
    Shot text = ParseTake("{ \"camera\": [ " + shot + "\"blend\" } ] }", out _).Shots.Single();
    Expect(text.Transition.Kind == TransitionKind.Blend && Near(text.Transition.Seconds, 3.0), "text form");
    Shot obj = ParseTake("{ \"camera\": [ " + shot + "{ \"type\": \"blend\" } } ] }", out _).Shots.Single();
    Expect(obj.Transition.Kind == TransitionKind.Blend && Near(obj.Transition.Seconds, 3.0), "object form");
    Shot given = ParseTake("{ \"camera\": [ " + shot + "{ \"type\": \"blend\", \"seconds\": 1.5 } } ] }", out _).Shots.Single();
    Expect(Near(given.Transition.Seconds, 1.5), "a given length stays");
});

Check("path maths: the curve passes through every point and holds outside", () =>
{
    List<PathPoint> points = SamplePoints();
    foreach (PathPoint p in points)
    {
        PathSample s = PathMath.Evaluate(points, p.T, 33);
        Expect(Near3(s.Position, p.Position, 1e-9) && Near3(s.LookAt, p.LookAt, 1e-9), $"pose at t {p.T}");
        Expect(Near(s.Fov, p.Fov!.Value, 1e-9) && Near(s.Roll, p.Roll!.Value, 1e-9), $"fov and roll at t {p.T}");
    }

    var late = new List<PathPoint> { new() { T = 0.3, Position = V3.UnitX, LookAt = V3.UnitY }, new() { T = 0.6, Position = V3.UnitY, LookAt = V3.UnitZ } };
    Expect(Near3(PathMath.Evaluate(late, 0.0, 33).Position, V3.UnitX), "holds before the first point");
    Expect(Near3(PathMath.Evaluate(late, 1.0, 33).Position, V3.UnitY), "holds after the last point");
    Expect(Near(PathMath.Evaluate(late, 0.0, 33).Fov, 33), "a point without a field of view takes the one of the shot");
});

Check("path maths: one point is a fixed camera and two points are a straight line", () =>
{
    var one = new List<PathPoint> { new() { T = 0.0, Position = new V3(1, 2, 3), LookAt = new V3(4, 5, 6) } };
    foreach (double u in new[] { 0.0, 0.37, 1.0 })
        Expect(Near3(PathMath.Evaluate(one, u, 40).Position, new V3(1, 2, 3)), "fixed at " + u);

    var two = new List<PathPoint>
    {
        new() { T = 0.0, Position = new V3(0, 0, 0), LookAt = V3.Zero },
        new() { T = 1.0, Position = new V3(10, 20, 30), LookAt = V3.Zero },
    };
    for (double u = 0.0; u <= 1.0; u += 0.125)
    {
        V3 p = PathMath.Evaluate(two, u, 40).Position;
        Expect(Near3(p, new V3(10, 20, 30) * u, 1e-9), "line at " + u);
    }
});

Check("path maths: no overshoot at the ends and no jump of the velocity", () =>
{
    List<PathPoint> points = SamplePoints();
    // The curve must not leave the first point backward or run past the last one.
    for (double u = 0.0; u <= points[1].T; u += 0.005)
        Expect(PathMath.Evaluate(points, u, 40).Position.X >= -1e-9, "behind the start at " + u);
    for (double u = points[2].T; u <= 1.0; u += 0.005)
        Expect(PathMath.Evaluate(points, u, 40).Position.X <= 20.0 + 1e-9, "past the end at " + u);

    // The fov and roll stay between the values of the two points of a segment where they are monotone.
    for (double u = points[1].T; u <= points[2].T; u += 0.005)
        Expect(Near(PathMath.Evaluate(points, u, 40).Fov, 20.0, 1e-9), "fov between two equal values stays level at " + u);
    for (double u = points[2].T; u <= 1.0; u += 0.005)
    {
        double fov = PathMath.Evaluate(points, u, 40).Fov;
        Expect(fov >= 20.0 - 1e-9 && fov <= 50.0 + 1e-9, "fov between its neighbours at " + u);
    }

    const double h = 1e-5;
    foreach (PathPoint p in points.Skip(1).Take(2))
    {
        V3 before = (PathMath.Evaluate(points, p.T, 40).Position - PathMath.Evaluate(points, p.T - h, 40).Position) / h;
        V3 after = (PathMath.Evaluate(points, p.T + h, 40).Position - PathMath.Evaluate(points, p.T, 40).Position) / h;
        Expect((before - after).Length < 1e-2 * Math.Max(1.0, before.Length), $"velocity at t {p.T}: {before} against {after}");
        V3 lookBefore = (PathMath.Evaluate(points, p.T, 40).LookAt - PathMath.Evaluate(points, p.T - h, 40).LookAt) / h;
        V3 lookAfter = (PathMath.Evaluate(points, p.T + h, 40).LookAt - PathMath.Evaluate(points, p.T, 40).LookAt) / h;
        Expect((lookBefore - lookAfter).Length < 1e-2 * Math.Max(1.0, lookBefore.Length), $"look velocity at t {p.T}");
    }

    // The path is continuous over the whole range.
    V3 last = PathMath.Evaluate(points, 0.0, 40).Position;
    for (double u = 0.001; u <= 1.0; u += 0.001)
    {
        V3 now = PathMath.Evaluate(points, u, 40).Position;
        Expect((now - last).Length < 0.5, "step at " + u);
        last = now;
    }
});

Check("path maths: repeated points and a very short segment stay finite", () =>
{
    var points = new List<PathPoint>
    {
        new() { T = 0.0, Position = new V3(0, 0, 0), LookAt = V3.UnitX },
        new() { T = 0.3, Position = new V3(5, 0, 0), LookAt = V3.UnitX },
        new() { T = 0.6, Position = new V3(5, 0, 0), LookAt = V3.UnitX, Fov = 30 },
        new() { T = 0.6005, Position = new V3(5, 0, 0), LookAt = V3.UnitX, Fov = 30 },
        new() { T = 1.0, Position = new V3(9, 3, 0), LookAt = V3.UnitX, Fov = 30 },
    };
    V3 last = PathMath.Evaluate(points, 0.0, 40).Position;
    for (double u = 0.0; u <= 1.0; u += 0.0005)
    {
        PathSample s = PathMath.Evaluate(points, u, 40);
        Expect(double.IsFinite(s.Position.X + s.Position.Y + s.Position.Z + s.Fov + s.Roll), "finite at " + u);
        Expect((s.Position - last).Length < 0.5, "no jump at " + u);
        last = s.Position;
    }

    var same = new List<PathPoint>
    {
        new() { T = 0.0, Position = new V3(1, 1, 1), LookAt = V3.UnitX },
        new() { T = 1.0, Position = new V3(1, 1, 1), LookAt = V3.UnitX },
    };
    Expect(Near3(PathMath.Evaluate(same, 0.4, 40).Position, new V3(1, 1, 1)), "two equal points are a fixed camera");
});

Check("path maths: the check of the times", () =>
{
    Expect(PathMath.CheckTimes(SamplePoints()) == null, "good times");
    List<PathPoint> same = SamplePoints();
    same[2].T = same[1].T;
    Expect(PathMath.CheckTimes(same) != null, "equal times");
    List<PathPoint> out_ = SamplePoints();
    out_[3].T = 1.2;
    Expect(PathMath.CheckTimes(out_) != null, "outside 0 to 1");
});

Check("path pose: site, vehicle and local frames", () =>
{
    Shot shot = MakeShot(ShotType.Path, 10.0);
    shot.Fov = 35.0;
    shot.Points.Add(new PathPoint { T = 0.0, Position = new V3(0, 0, 0), LookAt = new V3(0, 0, 10) });
    shot.Points.Add(new PathPoint { T = 1.0, Position = new V3(0, 10, 0), LookAt = new V3(0, 10, 10), Fov = 25 });
    var inputs = new PoseInputs();
    Basis3 vehicle = new(new V3(100, 0, 0), V3.UnitY, V3.UnitX, new V3(0, 0, -1));

    CameraPose site = PoseMath.EvaluateSite(shot, 0.5, inputs, vehicle);
    Expect(Near3(site.Position, new V3(0, 5, 0)) && Near(site.Fov, 30.0), "site frame: " + site.Position);

    shot.Target = ShotTarget.Vehicle;
    CameraPose body = PoseMath.EvaluateSite(shot, 0.5, inputs, vehicle);
    Expect(Near3(body.Position, vehicle.ToParent(new V3(0, 5, 0))), "vehicle frame: " + body.Position);

    shot.Frame = ShotFrame.Local;
    inputs.BodyCentre = V3.Zero;
    inputs.Pole = V3.UnitZ;
    var onEast = new Basis3(new V3(0, 6000, 0), V3.UnitX, V3.UnitX, V3.UnitX);
    CameraPose local = PoseMath.EvaluateSite(shot, 0.0, inputs, onEast);
    Basis3 horizon = LocalHorizon.Frame(onEast.Origin, inputs.BodyCentre, inputs.Pole);
    Expect(Near3(local.Position, horizon.ToParent(V3.Zero)) && Near3(local.Up, horizon.X), "local frame at the vehicle");
    Expect(shot.EffectiveUp == ShotUp.Horizon, "a local path has the horizon up");
});

Check("path pose: the roll of the points adds to the roll of the shot", () =>
{
    Shot shot = MakeShot(ShotType.Path, 10.0);
    shot.Roll = 5.0;
    shot.Points.Add(new PathPoint { T = 0.0, Position = new V3(0, -10, 0), LookAt = V3.Zero, Roll = 0.0 });
    shot.Points.Add(new PathPoint { T = 1.0, Position = new V3(0, -10, 0), LookAt = V3.Zero, Roll = 20.0 });
    Expect(Near(PoseMath.RollOf(shot, 0.0), 5.0) && Near(PoseMath.RollOf(shot, 1.0), 25.0), "roll at the ends");
    CameraPose level = PoseMath.EvaluateSite(shot, 0.0, new PoseInputs(), Basis3.Identity);
    CameraPose rolled = PoseMath.EvaluateSite(shot, 1.0, new PoseInputs(), Basis3.Identity);
    double cos = V3.Dot(level.Up.NormalizedOr(V3.UnitX), rolled.Up.NormalizedOr(V3.UnitX));
    Expect(Near(Math.Acos(Math.Clamp(cos, -1, 1)) * 180.0 / Math.PI, 20.0, 1e-6), "the up turns by 20 degrees between the ends");
});

Check("blend: the weight is a smootherstep", () =>
{
    Expect(Near(Easing.Smootherstep(0.0), 0.0) && Near(Easing.Smootherstep(1.0), 1.0) && Near(Easing.Smootherstep(0.5), 0.5), "ends and middle");
    Expect(Near(Easing.Smootherstep(0.25), 0.103515625), "quarter " + Easing.Smootherstep(0.25));

    Take take = TimedTake();
    take.Shots[1].Transition = new Transition { Kind = TransitionKind.Blend, Seconds = 4.0 };
    take.Shots[0].Ease = Ease.None;
    var runner = new TimelineRunner(take, new Dictionary<string, string>());
    runner.Update(0.0, Events());
    TimelineState s = runner.Update(6.0, Events());
    Expect(Near(s.BlendWeight, 0.103515625), "weight after a quarter of the blend " + s.BlendWeight);
});

Check("blend: a curved blend keeps the ends and swings around the subject", () =>
{
    var a = new CameraPose(PoseFrame.Site, new V3(0, -100, 0), new V3(0, 0, 0), V3.UnitX, 30);
    var b = new CameraPose(PoseFrame.Site, new V3(0, 100, 0), new V3(0, 0, 0), V3.UnitX, 50);
    Expect(Near3(PoseMath.Blend(a, b, 0.0, curved: true).Position, a.Position), "start");
    Expect(Near3(PoseMath.Blend(a, b, 1.0, curved: true).Position, b.Position), "end");
    CameraPose mid = PoseMath.Blend(a, b, 0.5, curved: true);
    CameraPose straight = PoseMath.Blend(a, b, 0.5);
    Expect(straight.Position.Length < 1e-9, "the straight line goes through the subject");
    Expect((mid.Position - V3.Zero).Length > 20.0, "the curve keeps away from the subject: " + mid.Position);

    // A chord far from the subject stays a straight line.
    var c = new CameraPose(PoseFrame.Site, new V3(0, -10, 1000), new V3(0, 0, 0), V3.UnitX, 30);
    var d = new CameraPose(PoseFrame.Site, new V3(0, 10, 1000), new V3(0, 0, 0), V3.UnitX, 30);
    Expect(Near3(PoseMath.Blend(c, d, 0.5, curved: true).Position, PoseMath.Blend(c, d, 0.5).Position, 1e-6), "far chord is straight");

    // Two shots at the same place do not move the camera.
    CameraPose still = PoseMath.Blend(a, a with { LookAt = new V3(50, 0, 0) }, 0.5, curved: true);
    Expect(Near3(still.Position, a.Position), "same position");
});

Check("blend: the motion frames of the shot types", () =>
{
    Expect(PoseMath.MotionFrameOf(MakeShot(ShotType.Track)) == PoseMath.MotionFrame.Site, "track");
    Expect(PoseMath.MotionFrameOf(MakeShot(ShotType.Orbit)) == PoseMath.MotionFrame.Site, "orbit");
    Expect(PoseMath.MotionFrameOf(MakeShot(ShotType.Chase)) == PoseMath.MotionFrame.Vehicle, "chase");
    Shot local = MakeShot(ShotType.Chase);
    local.Frame = ShotFrame.Local;
    Expect(PoseMath.MotionFrameOf(local) == PoseMath.MotionFrame.Local, "local chase");
    Shot path = MakeShot(ShotType.Path);
    Expect(PoseMath.MotionFrameOf(path) == PoseMath.MotionFrame.Site, "site path");
    path.Target = ShotTarget.Vehicle;
    Expect(PoseMath.MotionFrameOf(path) == PoseMath.MotionFrame.Vehicle, "vehicle path");
});

Check("director: a blend between frames curves and arrives at the next shot", () =>
{
    ShotFile file = ShotFileJson.Parse("""
{ "takes": [ { "name": "t", "camera": [
  { "type": "tripod", "target": "site", "position": [0, -60, 0], "lookAt": [0, 0, 0], "duration": 2 },
  { "type": "tripod", "target": "vehicle", "position": [0, 60, 0], "lookAt": [0, 0, 0], "duration": 6, "transition": "blend" }
] } ] }
""").File;
    Expect(file.DefaultTake.Shots[1].Transition.Seconds == 3.0, "default blend length");
    var director = new TakeDirector(file.DefaultTake, file);
    var world = new WorldSample { VehicleFrame = Basis3.Identity, VehicleHeight = 10.0 };
    DirectorFrame f = director.Update(0.0, world);
    double farthest = 0.0;
    for (double t = 0.0; t <= 2.0; t += 0.05)
        f = director.Update(t, world);
    Expect(Near3(f.Pose!.Value.Position, new V3(0, -60, 0), 1e-6), "start of the blend");
    for (double t = 2.0; t <= 5.0; t += 0.05)
    {
        f = director.Update(t, world);
        farthest = Math.Max(farthest, f.Pose!.Value.Position.Length);
    }

    Expect(Near3(f.Pose!.Value.Position, new V3(0, 60, 0), 1e-6), "end of the blend");
    Expect(farthest > 0.0, "the pose exists");
    V3 middle = director.Update(5.0, world).Pose!.Value.Position;
    Expect(Near3(middle, new V3(0, 60, 0), 1e-6), "after the blend the shot is exact");
});

Check("capture: a camera becomes a point of a path shot", () =>
{
    Shot shot = MakeShot(ShotType.Path, 10.0);
    shot.Points.Add(new PathPoint { T = 0.0, Position = new V3(0, -10, 0), LookAt = V3.Zero });
    shot.Points.Add(new PathPoint { T = 1.0, Position = new V3(0, 10, 0), LookAt = V3.Zero });
    var camera = new CapturedCamera(new V3(5, 1, 2), new V3(0, 0, 1), 28.0);
    Basis3 vehicle = Basis3.Identity;
    Expect(ShotCapture.AddPathPoint(shot, camera, vehicle, 20.0, 0.4, out string message), "added: " + message);
    Expect(shot.Points.Count == 3 && Near(shot.Points[1].T, 0.4) && Near3(shot.Points[1].Position, new V3(5, 1, 2)), "inserted in order");
    Expect(Near(shot.Points[1].Fov!.Value, 28.0), "field of view");

    Expect(ShotCapture.AddPathPoint(shot, camera with { Position = new V3(6, 1, 2) }, vehicle, 20.0, 0.401, out _), "replaced");
    Expect(shot.Points.Count == 3 && Near3(shot.Points[1].Position, new V3(6, 1, 2)) && Near(shot.Points[1].T, 0.4), "a point at the same progress is replaced");
    Expect(PathMath.CheckTimes(shot.Points) == null, "times stay in order");

    // The frame of the points follows the shot.
    shot.Target = ShotTarget.Vehicle;
    var body = new Basis3(new V3(100, 0, 0), V3.UnitY, V3.UnitX, new V3(0, 0, -1));
    Expect(ShotCapture.AddPathPoint(shot, camera with { Position = new V3(100, 3, 0) }, body, 20.0, 0.8, out _), "vehicle frame");
    Expect(Near3(shot.Points[2].Position, new V3(3, 0, 0)), "point in the vehicle frame " + shot.Points[2].Position);

    var wrong = MakeShot(ShotType.Orbit);
    Expect(!ShotCapture.AddPathPoint(wrong, camera, vehicle, 20.0, 0.5, out message) && message.Length > 0, "an orbit cannot take a point");
    Expect(ShotCapture.ApplyAtProgress(shot, camera, vehicle, 20.0, null, 0.9, out _), "apply on a path adds a point");
    Expect(shot.Points.Count == 5, "apply added a point");
});

Check("capture: a tripod or chase shot becomes a path with its old pose first", () =>
{
    Shot tripod = MakeShot(ShotType.Tripod, 8.0);
    tripod.Position = new V3(3, -60, -45);
    tripod.LookAt = new V3(15, 0, 0);
    tripod.Fov = 45.0;
    var camera = new CapturedCamera(new V3(20, -20, -10), new V3(-1, 0, 0), 30.0);
    Expect(ShotCapture.AddPathPoint(tripod, camera, Basis3.Identity, 30.0, 0.5, out string message), "converted: " + message);
    Expect(tripod.Type == ShotType.Path && tripod.Ease == Ease.InOut && tripod.Target == ShotTarget.Site, "path shot");
    Expect(tripod.Points.Count == 2 && Near(tripod.Points[0].T, 0.0) && Near3(tripod.Points[0].Position, new V3(3, -60, -45)), "old pose first");
    Expect(Near3(tripod.Points[0].LookAt, new V3(15, 0, 0)) && Near(tripod.Points[0].Fov!.Value, 45.0), "old look point and fov");
    Expect(Near(tripod.Points[1].T, 0.5) && Near3(tripod.Points[1].Position, new V3(20, -20, -10)), "camera second");
    Expect(ShotFileValidator.Validate(WrapShot(tripod)).IsValid, "the result is a valid file");

    Shot early = MakeShot(ShotType.Tripod, 8.0);
    early.Position = new V3(1, 2, 3);
    ShotCapture.AddPathPoint(early, camera, Basis3.Identity, 30.0, 0.01, out _);
    Expect(Near(early.Points[1].T, 1.0), "a conversion at the start puts the camera at the end");

    Shot chase = MakeShot(ShotType.Chase, 8.0);
    chase.Offset = new V3(-25, 8, -12);
    chase.LookAt = new V3(10, 0, 0);
    chase.Frame = ShotFrame.Local;
    var body = new Basis3(new V3(0, 6000, 0), V3.UnitX, V3.UnitY, V3.UnitZ);
    var near = new CapturedCamera(new V3(0, 6000, -30), V3.UnitY, 40.0);
    Expect(ShotCapture.AddPathPoint(chase, near, body, 20.0, 0.6, out _, V3.Zero, V3.UnitZ), "chase converted");
    Expect(chase.Type == ShotType.Path && chase.Target == ShotTarget.Vehicle && chase.UsesLocalFrame, "vehicle path in the local frame");
    Expect(Near3(chase.Points[0].Position, new V3(-25, 8, -12)), "old offset first");
    Expect(ShotFileValidator.Validate(WrapShot(chase)).IsValid, "chase result is valid");

    Expect(!ShotCapture.CanConvertToPath(ShotType.Part) && !ShotCapture.CanConvertToPath(ShotType.Track), "other types stay");
});

Check("editing: add, remove and move path points", () =>
{
    Shot shot = ShotEdit.NewShot(ShotType.Path);
    Expect(shot.Points.Count == 2 && shot.Ease == Ease.InOut && shot.Target == ShotTarget.Site, "a new path shot");
    Expect(ShotFileValidator.Validate(WrapShot(shot)).IsValid, "a new path shot is valid");

    int added = ShotEdit.AddPoint(shot, 0);
    Expect(added == 1 && shot.Points.Count == 3 && Near(shot.Points[1].T, 0.5), "a point in the middle");
    PathSample onCurve = PathMath.Evaluate(new[] { shot.Points[0], shot.Points[2] }, 0.5, shot.Fov);
    Expect((shot.Points[1].Position - onCurve.Position).Length < 30.0, "the new point is near the path");
    added = ShotEdit.AddPoint(shot, 2);
    Expect(added == 2 && shot.Points.Count == 4 && PathMath.CheckTimes(shot.Points) == null, "adding after the last point at 1 goes before it");

    List<V3> positions = shot.Points.Select(p => p.Position).ToList();
    int moved = ShotEdit.MovePoint(shot, 1, 1);
    Expect(moved == 2 && Near3(shot.Points[2].Position, positions[1]) && Near3(shot.Points[1].Position, positions[2]), "poses swap");
    Expect(PathMath.CheckTimes(shot.Points) == null, "times stay in order after a move");
    Expect(ShotEdit.MovePoint(shot, 0, -1) == 0 && ShotEdit.MovePoint(shot, 3, 1) == 3, "no move at the ends");

    int next = ShotEdit.RemovePoint(shot, 3);
    Expect(next == 2 && shot.Points.Count == 3, "remove");
    ShotEdit.RemovePoint(shot, 0);
    ShotEdit.RemovePoint(shot, 0);
    Expect(ShotEdit.RemovePoint(shot, 0) == 0 && shot.Points.Count == 1, "the last point stays");

    Shot clone = ShotEdit.Clone(ShotEdit.NewShot(ShotType.Path));
    Expect(clone.Points.Count == 2, "clone copies points");
    clone.Points[0].Position = new V3(9, 9, 9);
    Shot original = ShotEdit.NewShot(ShotType.Path);
    Expect(!Near3(original.Points[0].Position, clone.Points[0].Position), "clone shares no point");

    Shot tripod = ShotEdit.NewShot(ShotType.Tripod);
    ShotEdit.ChangeType(tripod, ShotType.Path);
    Expect(tripod.Points.Count == 2 && tripod.Ease == Ease.InOut, "the type change gives a path points");
    Expect(ShotFields.Uses(ShotType.Path, ShotField.Points) && !ShotFields.Uses(ShotType.Tripod, ShotField.Points), "field rules");
    Expect(ShotFields.Uses(ShotType.Path, ShotField.Frame) && ShotFields.Uses(ShotType.Path, ShotField.Target), "path uses target and frame");
});

ShotFile WrapShot(Shot shot)
{
    var file = new ShotFile();
    var take = new Take { Name = "default", IsDefault = true };
    take.Shots.Add(shot);
    file.Takes.Add(take);
    return file;
}

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

#region View text

Check("view text: triggers read as words", () =>
{
    Expect(ViewText.TriggerText(Trigger.OnEvent("ignition", 2.0), 0) == "ignition +2.0 s", "event plus delay");
    Expect(ViewText.TriggerText(Trigger.OnEvent("altitude:5000", -1.5), 0) == "altitude:5000 -1.5 s", "negative delay");
    Expect(ViewText.TriggerText(Trigger.OnEvent("liftoff"), 0) == "liftoff", "no delay");
    Expect(ViewText.TriggerText(Trigger.AtTime(12.0), 3) == "at 12.0 s", "time");
    Expect(ViewText.TriggerText(Trigger.After(), 3) == "after #3", "after names the item before");
    Expect(ViewText.TriggerText(Trigger.After(), 0) == "at the start", "first item");
});

Check("view text: rows of the three tracks", () =>
{
    Shot shot = MakeShot(ShotType.Chase, 8.0);
    shot.At = Trigger.OnEvent("sequence:3", 1.0);
    TrackRow row = ViewText.ShotRow(1, shot);
    Expect(row.Number == "2" && row.Name == "chase" && row.Starts == "sequence:3 +1.0 s" && row.Extra == "8.0 s", "shot row " + row);
    TrackRow key = ViewText.KeyRow(0, new SpeedKey { To = 0.25, Ramp = 2.0 });
    Expect(key.Name == "to 0.25x" && key.Extra == "ramp 2.0 s" && key.Starts == "at the start", "key row " + key);
    Expect(ViewText.KeyRow(0, new SpeedKey { To = 4.0 }).Extra == "jump", "no ramp");
    TrackRow action = ViewText.ActionRow(0, new TakeAction { Sequence = 3, At = Trigger.AtTime(9.5) });
    Expect(action.Name == "stage" && action.Extra == "sequence 3" && action.Starts == "at 9.5 s", "action row " + action);
});

Check("view text: the state of the take", () =>
{
    var idle = new RunStateInput(false, false, false, false, false, false, false);
    Expect(ViewText.StateOf(idle) == RunState.Idle, "idle");
    Expect(ViewText.StateOf(idle with { ResetReady = true }) == RunState.Ready, "ready");
    Expect(ViewText.StateOf(idle with { ResetActive = true, ResetReady = true }) == RunState.Resetting, "reset first");
    Expect(ViewText.StateOf(idle with { HasRun = true }) == RunState.Recording, "recording");
    Expect(ViewText.StateOf(idle with { HasRun = true, DryRun = true }) == RunState.DryRun, "dry");
    Expect(ViewText.StateOf(idle with { HasRun = true, Preview = true }) == RunState.Preview, "preview");
    Expect(ViewText.StateOf(idle with { HasRun = true, Capturing = true }) == RunState.Capturing, "capturing");
    Expect(ViewText.StateOf(idle with { HasRun = true, Paused = true }) == RunState.Paused, "paused");
});

Check("view text: ready latch holds until a take starts", () =>
{
    var latch = new ReadyLatch();
    latch.Update(ResetPhase.Loading, false);
    Expect(!latch.Ready, "loading");
    latch.Update(ResetPhase.Done, false);
    Expect(latch.Ready, "done");
    latch.Update(ResetPhase.Done, false);
    Expect(latch.Ready, "still ready");
    latch.Update(ResetPhase.Done, true);
    Expect(!latch.Ready, "take runs");
    latch.Update(ResetPhase.Done, false);
    Expect(!latch.Ready, "take ended, not ready again");
    latch.Update(ResetPhase.Loading, false);
    latch.Update(ResetPhase.Done, false);
    Expect(latch.Ready, "second reset");
});

Check("view text: shot and speed texts", () =>
{
    Take take = ParseTake(minimal, out _);
    Expect(ViewText.ShotText(take, 0, 0.404) == "shot 1 of 1, tripod, 40 %", ViewText.ShotText(take, 0, 0.404));
    Expect(ViewText.ShotText(take, -1, 0) == "no shot yet", "no shot");
    Expect(ViewText.SimSpeedText(0.25, 4.0) == "0.25x (target 4x)", "target");
    Expect(ViewText.SimSpeedText(1.0, 1.0) == "1x", "same");
    Expect(ViewText.SimSpeedText(0.0, null) == "stopped", "stopped");
});

Check("view text: the next trigger is the earliest known one", () =>
{
    var take = new Take();
    take.Shots.Add(new Shot { Type = ShotType.Chase, Duration = 5 });
    take.Shots.Add(new Shot { Type = ShotType.Orbit, Duration = 5, At = Trigger.OnEvent("sequence:3", 1.0) });
    take.Speed.Add(new SpeedKey { To = 4.0, At = Trigger.OnEvent("altitude:5000") });
    take.Speed.Add(new SpeedKey { To = 1.0, At = Trigger.AtTime(300.0) });
    var aliases = new Dictionary<string, string>();
    var none = new Dictionary<string, double>();
    var predicted = new Dictionary<string, double> { ["altitude:5000"] = 40.0 };

    NextTrigger? next = ViewText.FindNext(take, aliases, none, predicted, 100.0, 0);
    Expect(next.HasValue && next.Value.What.StartsWith("speed key 1") && next.Value.Eta == 40.0, "predicted altitude wins: " + next?.Text);

    var seen = new Dictionary<string, double> { ["altitude:5000"] = 90.0, ["sequence:3"] = 99.0 };
    next = ViewText.FindNext(take, aliases, seen, predicted, 99.5, 0);
    Expect(next.HasValue && next.Value.What.StartsWith("shot 2") && Near(next.Value.Eta!.Value, 0.5), "delay still pending: " + next?.Text);

    next = ViewText.FindNext(take, aliases, seen, predicted, 101.5, 1);
    Expect(next.HasValue && next.Value.What.StartsWith("speed key 2") && Near(next.Value.Eta!.Value, 198.5), "only the time key is left: " + next?.Text);

    take.Speed.RemoveAt(1);
    next = ViewText.FindNext(take, aliases, none, none, 0.0, -1);
    Expect(next.HasValue && next.Value.Eta == null && next.Value.What.StartsWith("shot 2"), "unknown time names the first waiting item: " + next?.Text);
});

Check("view text: event rows list seen events, then awaited ones", () =>
{
    var seen = Events(("liftoff", 40.0), ("ignition", 38.1));
    var predicted = new Dictionary<string, double> { ["altitude:5000"] = 41.6 };
    List<EventRow> rows = ViewText.EventRows(seen, predicted, new[] { "ignition", "altitude:5000", "sequence:3", "sequence:3" });
    Expect(rows.Count == 4, "four rows");
    Expect(rows[0].Name == "ignition" && rows[0].Time == "38.1" && rows[0].Seen, "first seen");
    Expect(rows[2].Name == "altitude:5000" && rows[2].Status == "in about 42.0 s", "prediction " + rows[2].Status);
    Expect(rows[3].Name == "sequence:3" && rows[3].Status == "waiting" && !rows[3].Seen, "waiting");
});

Check("view text: reasons name the first blocker", () =>
{
    Expect(ViewText.StartBlock(false, false, true, false, true) == "", "free");
    Expect(ViewText.StartBlock(true, false, false, false, true) == "A take or a preview runs already.", "running first");
    Expect(ViewText.StartBlock(false, false, false, false, true) == "No controlled vehicle in flight.", "vehicle");
    Expect(ViewText.StartBlock(false, false, true, false, false).Contains("problems"), "file");
    Expect(ViewText.FileEditBlock(true).StartsWith("Stop the take") && ViewText.FileEditBlock(false) == "", "file edit");
    Expect(ViewText.CheckpointBlock(false, true, false) == "A reset runs.", "checkpoint");
});

Check("view text: every shot field has a tip in plain ASCII", () =>
{
    foreach (ShotField field in Enum.GetValues<ShotField>())
    {
        string tip = ViewText.FieldTip(field);
        Expect(tip.Length > 10 && tip.All(c => c < 128), "tip of " + field);
    }
});

Check("counted events: an engine that lights a few seconds after the others is still ignition 1", () =>
{
    var detector = new EventDetector(Array.Empty<EventName>());
    VehicleSample S(bool engine, int fresh = 0) => new(engine, true, 0, 0, 0, null, fresh);
    detector.Update(0.0, S(false));
    detector.Update(1.0, S(true, fresh: 2));
    Expect(detector.Update(4.0, S(true, fresh: 1)).Count == 0, "3 s later is the same ignition");
    Expect(!detector.Seen.ContainsKey("ignition:2"), "no ignition:2");
});

Check("keep-out: boxes of a take that change after the director was made apply at once", () =>
{
    ShotFile file = ShotFileJson.Parse("""
    { "camera": [ { "type": "tripod", "position": [-5, 0, 2], "lookAt": [20, 0, 0], "duration": 5 } ] }
    """).File;
    var director = new TakeDirector(file.DefaultTake, file);
    Expect(Near3(director.Update(0.0, World(Sample())).Pose!.Value.Position, new V3(-5, 0, 2)), "no boxes");
    file.DefaultTake.KeepOut = new List<KeepOutBox> { Box(-30, -30, -30, 0, 30, 30) };
    Expect(Near3(director.Update(0.1, World(Sample())).Pose!.Value.Position, new V3(1, 0, 2)), "the new list is used");
});

Check("shot file from the environment: loads, round trips and plays through a simulated ascent with finite poses outside the boxes", () =>
{
    string? path = Environment.GetEnvironmentVariable("CINECAM_SHOT_FILE");
    if (string.IsNullOrEmpty(path) || !File.Exists(path))
        return;
    ShotFile file = ShotFileJson.Parse(File.ReadAllText(path)).File;
    Expect(ShotFileJson.Write(ShotFileJson.Parse(ShotFileJson.Write(file)).File) == ShotFileJson.Write(file), "the file round trips");
    foreach (Take take in file.Takes)
    {
        var director = new TakeDirector(take, file);
        IReadOnlyList<KeepOutBox> boxes = file.KeepOutFor(take);
        for (double t = 0.0; t < 900.0; t += 0.1)
        {
            double flight = Math.Max(t - 14.0, 0.0);
            double altitude = Math.Min(0.5 * 12.0 * flight * flight, 250000.0);
            double east = Math.Min(0.5 * 4.0 * flight * flight, 900000.0);
            double vUp = flight > 0 ? 12.0 * flight : 0.0, vEast = flight > 0 ? 4.0 * flight : 0.0;
            WorldSample world = new()
            {
                Vehicle = new VehicleSample(t >= 2.0 && t < 700.0, flight <= 0.0, 0, altitude, Math.Sqrt(vUp * vUp + vEast * vEast)),
                VehicleFrame = new Basis3(new V3(altitude + 5.3, east, 0), V3.UnitX, V3.UnitY, V3.UnitZ),
                VehicleHeight = 60.0,
                VehicleWidth = 5.0,
                BodyCentre = new V3(-6371000.0, 0, 0),
                Pole = V3.UnitZ,
                SurfaceVelocity = new V3(vUp, vEast, 0),
            };
            DirectorFrame frame = director.Update(t, world);
            if (frame.Pose is not CameraPose pose)
                continue;
            bool finite = new[] { pose.Position.X, pose.Position.Y, pose.Position.Z, pose.LookAt.X, pose.LookAt.Y, pose.LookAt.Z, pose.Fov }.All(double.IsFinite);
            Expect(finite && pose.Fov >= 1.0 && pose.Fov <= 120.0, $"{take.Name} t={t:0.0}: pose {pose.Position} look {pose.LookAt} fov {pose.Fov}");
            if (pose.Position.Length <= KeepOutMath.NearSiteMeters)
                Expect(boxes.All(b => !KeepOutMath.Contains(b, pose.Position)), $"{take.Name} t={t:0.0}: camera {pose.Position} is inside a box");
            if (failures > 0)
                return;
        }
    }
});

#endregion

#region Pre-roll, aim, sun and separated vehicle

const string preRollFile = """
{
  "preRoll": { "until": "liftoff" },
  "camera": [
    { "type": "tripod", "position": [1, 2, 3], "lookAt": [4, 5, 6], "duration": 4 },
    { "type": "tripod", "position": [1, 2, 3], "lookAt": [4, 5, 6], "duration": 6, "transition": 1 },
    { "type": "tripod", "position": [9, 9, 9], "lookAt": [4, 5, 6], "duration": 20, "at": { "event": "liftoff" }, "transition": 2 },
  ],
}
""";

Check("pre-roll: the shots before the waiting shot repeat until the event comes", () =>
{
    Take take = ParseTake(preRollFile, out ShotFile file);
    Expect(take.PreRoll is { Loop: true } && take.PreRoll.Until == "liftoff", "read");
    var runner = new TimelineRunner(take, file.Events);
    var none = Events();
    Expect(runner.Update(1.0, none).ShotIndex == 0, "first pass, shot 0");
    TimelineState s = runner.Update(4.5, none);
    Expect(s.ShotIndex == 1 && s.PreviousShotIndex == 0, "first pass, shot 1 blends from shot 0");
    s = runner.Update(10.5, none);
    Expect(s.ShotIndex == 0 && Near(s.LocalTime, 0.5), "second pass starts at 10 s");
    Expect(s.StartedShots.Contains(0), "the new pass is reported as a start");
    s = runner.Update(26.0, none);
    Expect(s.ShotIndex == 1 && Near(s.LocalTime, 2.0) && !s.Finished, "third pass");
});

Check("pre-roll: the event leaves the active loop shot through the transition of the waiting shot", () =>
{
    Take take = ParseTake(preRollFile, out ShotFile file);
    var runner = new TimelineRunner(take, file.Events);
    runner.Update(0.0, Events());
    TimelineState s = runner.Update(12.0, Events(("liftoff", 12.0)));
    Expect(s.ShotIndex == 2 && s.PreviousShotIndex == 0 && Near(s.BlendWeight, 0.0), "shot 2 blends from the loop shot 0 of the second pass");
    s = runner.Update(13.0, Events(("liftoff", 12.0)));
    Expect(s.ShotIndex == 2 && s.PreviousShotIndex == 0 && s.BlendWeight > 0.0 && s.BlendWeight < 1.0, "mid blend");
    s = runner.Update(40.0, Events(("liftoff", 12.0)));
    Expect(s.ShotIndex == 2 && s.Finished, "the loop does not come back and the take ends after the waiting shot");
    Expect(Near(runner.ShotStart(2)!.Value, 12.0), "the waiting shot starts at the event");
});

Check("pre-roll: an event in the middle of a pass uses that pass shot", () =>
{
    Take take = ParseTake(preRollFile, out ShotFile file);
    var runner = new TimelineRunner(take, file.Events);
    runner.Update(0.0, Events());
    runner.Update(16.0, Events());
    TimelineState s = runner.Update(16.5, Events(("liftoff", 16.5)));
    Expect(s.ShotIndex == 2 && s.PreviousShotIndex == 1, "the active loop shot at 16.5 s is shot 1 of the second pass");
    Expect(Near(s.PreviousProgress, 0.0, 1e-9) || s.PreviousProgress > 0.0, "progress " + s.PreviousProgress);
});

Check("pre-roll: an event before the first pass ends cannot be cut in on by a later loop shot", () =>
{
    Take take = ParseTake(preRollFile, out ShotFile file);
    var runner = new TimelineRunner(take, file.Events);
    runner.Update(0.0, Events());
    TimelineState s = runner.Update(2.0, Events(("liftoff", 2.0)));
    Expect(s.ShotIndex == 2 && s.PreviousShotIndex == 0, "the waiting shot blends from shot 0");
    s = runner.Update(5.0, Events(("liftoff", 2.0)));
    Expect(s.ShotIndex == 2, "shot 1 (first pass at 4 s) does not start after the event");
    s = runner.Update(30.0, Events(("liftoff", 2.0)));
    Expect(s.ShotIndex == 2 && s.Finished, "the waiting shot stays active and the take ends after it");
});

Check("pre-roll: without the field, or with loop false, the shots play once", () =>
{
    string without = preRollFile.Replace("\"preRoll\": { \"until\": \"liftoff\" },", "");
    Take take = ParseTake(without, out ShotFile file);
    var runner = new TimelineRunner(take, file.Events);
    runner.Update(0.0, Events());
    Expect(runner.Update(11.0, Events()).ShotIndex == 1, "no second pass without the field");
    take = ParseTake(preRollFile.Replace("{ \"until\": \"liftoff\" }", "{ \"until\": \"liftoff\", \"loop\": false }"), out file);
    runner = new TimelineRunner(take, file.Events);
    runner.Update(0.0, Events());
    Expect(runner.Update(11.0, Events()).ShotIndex == 1, "loop false");
});

Check("pre-roll: a loop that cannot end is an error", () =>
{
    Take take = ParseTake(preRollFile.Replace("\"until\": \"liftoff\"", "\"until\": \"burnout:1\""), out ShotFile file);
    bool threw = false;
    try { _ = new TimelineRunner(take, file.Events); } catch (ArgumentException ex) { threw = ex.Message.Contains("preRoll.until"); }
    Expect(threw, "no shot starts on burnout:1");
    ExpectInvalid(preRollFile.Replace("\"until\": \"liftoff\"", "\"until\": \"nonsense\""));
    take = ParseTake(preRollFile.Replace("\"duration\": 6, \"transition\": 1", "\"duration\": 6, \"at\": 5"), out file);
    threw = false;
    try { _ = new TimelineRunner(take, file.Events); } catch (ArgumentException ex) { threw = ex.Message.Contains("camera[1].at"); }
    Expect(threw, "a loop shot with its own time");
});

Check("pre-roll and the new shot fields survive a write and a read", () =>
{
    const string json = """
    {
      "preRoll": { "until": "liftoff", "loop": false },
      "camera": [
        { "type": "chase", "target": "separated", "pick": "nearest", "aim": "vehicle", "aimOffset": [1, 2, 3], "avoidSun": 30, "offset": [-5, 0, 0], "duration": 5 },
        { "type": "path", "target": "separated", "points": [ { "t": 0, "position": [1, 2, 3], "lookAt": [0, 0, 0] } ], "duration": 5, "at": { "event": "liftoff" } },
      ],
    }
    """;
    ShotFile file = ShotFileJson.Parse(json).File;
    Shot a = file.DefaultTake.Shots[0];
    Expect(a.Subject == ShotSubject.Separated && a.Target == ShotTarget.Vehicle && a.Pick == SeparatedPick.Nearest, "subject");
    Expect(a.Aim == ShotAim.Vehicle && Near3(a.AimOffset, new V3(1, 2, 3)) && a.AvoidSun == 30.0, "aim and sun");
    ShotFile again = ShotFileJson.Parse(ShotFileJson.Write(file)).File;
    Shot b = again.DefaultTake.Shots[0];
    Expect(b.Subject == ShotSubject.Separated && b.Pick == SeparatedPick.Nearest && b.Aim == ShotAim.Vehicle && b.AvoidSun == 30.0, "round trip");
    Expect(again.DefaultTake.PreRoll is { Loop: false } p && p.Until == "liftoff", "pre-roll round trip");
    Expect(again.DefaultTake.Shots[1].Subject == ShotSubject.Separated, "path subject");
    Expect(ShotEdit.Clone(file.DefaultTake, "x").PreRoll is { Loop: false }, "take clone keeps the pre-roll");
    Expect(ShotEdit.Clone(a).Aim == ShotAim.Vehicle && ShotEdit.Clone(a).Pick == SeparatedPick.Nearest, "shot clone");
    ExpectInvalid("""{ "camera": [ { "type": "orbit", "target": "separated", "radius": 5, "duration": 3 } ] }""");
    ExpectInvalid("""{ "camera": [ { "type": "chase", "avoidSun": 0, "offset": [1,0,0], "duration": 3 } ] }""");
    Expect(ShotFileJson.Parse("""{ "camera": [ { "type": "chase", "avoidSun": "off", "offset": [1,0,0], "duration": 3 } ] }""").File.DefaultTake.Shots[0].AvoidSun == null, "off");
});

Check("aim: a chase shot looks at the center of the vehicle plus the offset in the shot frame", () =>
{
    var shot = new Shot { Type = ShotType.Chase, Offset = new V3(-30, 10, 0), LookAt = new V3(100, 0, 0), Aim = ShotAim.Vehicle, AimOffset = new V3(2, 0, 0) };
    var frame = new Basis3(new V3(1000, 0, 0), V3.UnitY, V3.UnitZ, V3.UnitX);
    var inputs = new PoseInputs { Vehicle = frame, VehicleHeight = 40.0, VehicleWidth = 4.0 };
    CameraPose pose = PoseMath.EvaluateSite(shot, 0.0, inputs, frame);
    Expect(Near3(pose.LookAt, new V3(1000, 22, 0)), "look " + pose.LookAt);
    shot.Aim = ShotAim.Look;
    Expect(!Near3(PoseMath.EvaluateSite(shot, 0.0, inputs, frame).LookAt, new V3(1000, 22, 0)), "off by default");
});

Check("aim: a local shot takes the aim offset in the local frame and fit frames the vehicle", () =>
{
    var shot = new Shot { Type = ShotType.Chase, Frame = ShotFrame.Local, Offset = new V3(0, 60, 0), Aim = ShotAim.Vehicle, AimOffset = new V3(0, 0, 5), Fit = 0.8 };
    var frame = new Basis3(new V3(1000, 0, 0), V3.UnitX, V3.UnitY, V3.UnitZ);
    var inputs = new PoseInputs { Vehicle = frame, VehicleHeight = 30.0, VehicleWidth = 4.0 };
    CameraPose pose = PoseMath.EvaluateSite(shot, 0.0, inputs, frame);
    Expect(Near3(pose.LookAt, new V3(1015, 0, 5)), "look " + pose.LookAt);
    Expect(pose.Fov > PoseMath.FitMinFov && pose.Fov < PoseMath.FitMaxFov, "fit " + pose.Fov);
});

Check("sun: the angle, the hidden test and the turn", () =>
{
    V3 sun = V3.UnitX;
    CameraPose pose = new(PoseFrame.Site, new V3(-100, 0, 0), V3.Zero, V3.UnitZ, 40);
    Expect(Near(SunAvoidMath.AngleDeg(pose.Position, pose.LookAt, sun), 0.0, 1e-6), "looking at the sun");
    Expect(Near(SunAvoidMath.RequiredTurnDeg(pose.Position, pose.LookAt, sun, 30.0), 30.0, 1e-6), "needs 30");
    CameraPose turned = SunAvoidMath.Turn(pose, sun, 30.0);
    Expect(Near(SunAvoidMath.AngleDeg(turned.Position, turned.LookAt, sun), 30.0, 1e-6), "angle after the turn");
    Expect(Near((turned.LookAt - turned.Position).Length, 100.0, 1e-6) && Near3(turned.LookAt, pose.LookAt), "distance and look point stay");
    Expect(Near(V3.Dot(turned.Up, (turned.LookAt - turned.Position)), 0.0, 1e-6), "up stays perpendicular to the view");

    CameraPose off = new(PoseFrame.Site, new V3(-100, 0, 0), new V3(-100, 100, 0), V3.UnitZ, 40);
    Expect(Near(SunAvoidMath.AngleDeg(off.Position, off.LookAt, sun), 90.0, 1e-6) && SunAvoidMath.RequiredTurnDeg(off.Position, off.LookAt, sun, 30.0) == 0.0, "far enough");
    CameraPose slight = new(PoseFrame.Site, new V3(-100, 0, 0), new V3(0, 20, 0), V3.UnitZ, 40);
    double before = SunAvoidMath.AngleDeg(slight.Position, slight.LookAt, sun);
    CameraPose moved = SunAvoidMath.Turn(slight, sun, SunAvoidMath.RequiredTurnDeg(slight.Position, slight.LookAt, sun, 30.0));
    Expect(before < 30.0 && Near(SunAvoidMath.AngleDeg(moved.Position, moved.LookAt, sun), 30.0, 1e-6), "from an angle of " + before);

    Expect(SunAvoidMath.IsHidden(new V3(-200, 0, 0), sun, V3.Zero, 100.0), "behind the body");
    Expect(!SunAvoidMath.IsHidden(new V3(200, 0, 0), sun, V3.Zero, 100.0), "in front");
    Expect(!SunAvoidMath.IsHidden(new V3(-200, 150, 0), sun, V3.Zero, 100.0), "beside the shadow");
    Expect(SunAvoidMath.IsHidden(new V3(-99, 0, 0), sun, V3.Zero, 100.0), "on the night side of the ground");
    Expect(!SunAvoidMath.IsHidden(new V3(100, 0, 0), sun, V3.Zero, 100.0), "on the day side of the ground");
});

Check("sun: the director turns the camera away from the sun, eased, and skips it in the shadow", () =>
{
    Take MakeTake()
    {
        var take = new Take { Name = "t", IsDefault = true };
        take.Shots.Add(new Shot { Type = ShotType.Tripod, Target = ShotTarget.Site, Position = new V3(0, -100, 0), LookAt = V3.Zero, Duration = 100.0, AvoidSun = 40.0 });
        return take;
    }

    TakeDirector MakeDirector(Take take)
    {
        var file = new ShotFile();
        file.Takes.Add(take);
        return new TakeDirector(take, file);
    }

    WorldSample World(bool shadow) => new()
    {
        Vehicle = new VehicleSample(false, true, 0, 0, 0),
        VehicleFrame = Basis3.Identity,
        VehicleHeight = 10.0,
        SunDirection = new V3(0, 1, 0),
        BodyCentre = shadow ? new V3(0, 1000, 0) : new V3(-1000000, 0, 0),
        BodyRadius = shadow ? 500.0 : 100.0,
    };

    TakeDirector director = MakeDirector(MakeTake());
    DirectorFrame first = director.Update(0.0, World(false));
    CameraPose p0 = first.Pose!.Value;
    Expect(Near(SunAvoidMath.AngleDeg(p0.Position, p0.LookAt, new V3(0, 1, 0)), 40.0, 1e-6), "the first frame snaps to the limit");
    Expect(first.Reports.Any(r => r.Name == "avoidSun"), "reported once");
    Expect(director.Update(0.1, World(false)).Reports.Count == 0, "not reported twice");

    CameraPose hidden = MakeDirector(MakeTake()).Update(0.0, World(true)).Pose!.Value;
    Expect(Near3(hidden.Position, new V3(0, -100, 0)), "the body hides the sun, so the camera stays");

    TakeDirector eased = MakeDirector(MakeTake());
    WorldSample far = World(false);
    far.SunDirection = new V3(0, 0, 1);
    eased.Update(0.0, far);
    WorldSample near = World(false);
    double lastAngle = 0.0;
    for (int i = 1; i <= 100; i++)
    {
        CameraPose p = eased.Update(i * 0.05, near).Pose!.Value;
        double angle = SunAvoidMath.AngleDeg(p.Position, p.LookAt, near.SunDirection!.Value);
        Expect(angle >= lastAngle - 1e-9 && angle - lastAngle < 6.0, $"step {i}: the angle {angle} moves smoothly from {lastAngle}");
        lastAngle = angle;
    }

    Expect(lastAngle >= 39.0, "the limit is reached at the end: " + lastAngle);
});

Check("separated: the shot follows the picked vehicle, falls back to the controlled one and reports the pick", () =>
{
    var take = new Take { Name = "t", IsDefault = true };
    take.Shots.Add(new Shot { Type = ShotType.Chase, Subject = ShotSubject.Separated, Offset = new V3(-30, 0, 0), Aim = ShotAim.Vehicle, Duration = 100.0, Smoothing = 0.0 });
    var file = new ShotFile();
    file.Takes.Add(take);
    var director = new TakeDirector(take, file);
    var stage = new SubjectSample { Name = "stage", Frame = new Basis3(new V3(500, 0, 0), V3.UnitX, V3.UnitY, V3.UnitZ), Height = 20.0, Width = 4.0, Distance = 80 };
    var sat = new SubjectSample { Name = "sat", Frame = new Basis3(new V3(520, 30, 0), V3.UnitX, V3.UnitY, V3.UnitZ), Height = 0.4, Width = 0.2, Distance = 30 };
    WorldSample World(SubjectSample? newest, SubjectSample? nearest) => new()
    {
        Vehicle = new VehicleSample(false, true, 0, 0, 0),
        VehicleFrame = new Basis3(new V3(100, 0, 0), V3.UnitX, V3.UnitY, V3.UnitZ),
        VehicleHeight = 50.0,
        VehicleWidth = 4.0,
        SeparatedNewest = newest,
        SeparatedNearest = nearest,
    };

    DirectorFrame f = director.Update(0.0, World(null, null));
    Expect(Near3(f.Pose!.Value.LookAt, new V3(125, 0, 0)), "controlled vehicle center " + f.Pose!.Value.LookAt);
    Expect(f.Reports.Count == 1 && f.Reports[0].Text.Contains("no vehicle has separated"), "fallback reported");
    Expect(director.Update(0.1, World(null, null)).Reports.Count == 0, "fallback reported once");
    f = director.Update(0.2, World(sat, stage));
    Expect(Near3(f.Pose!.Value.LookAt, new V3(520.2, 30, 0)), "newest sat " + f.Pose!.Value.LookAt);
    Expect(f.Reports.Count == 1 && f.Reports[0].Text.Contains("\"sat\"") && f.Reports[0].Text.Contains("newest"), "pick reported");
    Expect(director.Update(0.3, World(sat, stage)).Reports.Count == 0, "pick reported once");

    take.Shots[0].Pick = SeparatedPick.Nearest;
    f = director.Update(0.4, World(sat, stage));
    Expect(Near3(f.Pose!.Value.LookAt, new V3(510, 0, 0)), "nearest stage " + f.Pose!.Value.LookAt);
    Expect(f.Reports.Count == 1 && f.Reports[0].Text.Contains("\"stage\"") && f.Reports[0].Text.Contains("nearest"), "pick change reported");
});

#endregion

#region Timeline dock layout

var tlNoAliases = new Dictionary<string, string>();

Shot TlShot(double duration, Trigger? at = null)
{
    Shot shot = MakeShot(ShotType.Tripod, duration);
    if (at != null)
        shot.At = at;
    return shot;
}

Check("timeline layout: after and time triggers give exact blocks and blends", () =>
{
    var take = new Take();
    take.Shots.Add(TlShot(5.0));
    Shot second = TlShot(4.0);
    second.Transition = new Transition { Kind = TransitionKind.Blend, Seconds = 2.0 };
    take.Shots.Add(second);
    take.Shots.Add(TlShot(3.0, Trigger.AtTime(20.0)));
    TimelineLayout layout = TimelineLayout.Build(take, tlNoAliases);
    Expect(Near(layout.Shots[0].Start, 0.0) && Near(layout.Shots[0].End, 5.0) && layout.Shots[0].BlendSeconds == 0.0, "first");
    Expect(Near(layout.Shots[1].Start, 5.0) && Near(layout.Shots[1].End, 9.0) && Near(layout.Shots[1].BlendSeconds, 2.0), "second blends");
    Expect(Near(layout.Shots[2].Start, 20.0) && Near(layout.Duration, 23.0), "time trigger and duration");
    Expect(!layout.HasEstimates && layout.Events.Count == 0 && layout.Shots.All(s => !s.Estimated), "nothing estimated");
});

Check("timeline layout: an event of the running take beats the last run, and the last run beats an estimate", () =>
{
    var take = new Take();
    take.Shots.Add(TlShot(5.0));
    take.Shots.Add(TlShot(4.0, Trigger.OnEvent("ignition", 1.0)));
    take.Shots.Add(TlShot(4.0, Trigger.OnEvent("liftoff", 0.5)));
    var lastRun = Events(("ignition", 12.0), ("liftoff", 18.0));
    var live = Events(("ignition", 14.0));
    TimelineLayout layout = TimelineLayout.Build(take, tlNoAliases, lastRun, live, now: 15.0);
    Expect(Near(layout.Shots[1].Start, 15.0) && !layout.Shots[1].Estimated, "live ignition plus delay");
    Expect(Near(layout.Shots[2].Start, 18.5) && !layout.Shots[2].Estimated, "liftoff from the last run");
    Expect(layout.Events.Single(e => e.Key == "ignition").Source == EventTimeSource.Live, "ignition source");
    Expect(layout.Events.Single(e => e.Key == "liftoff").Source == EventTimeSource.LastRun, "liftoff source");
    Expect(!layout.HasEstimates, "no estimate");
});

Check("timeline layout: unknown events are spaced evenly and keep the shots in order", () =>
{
    var take = new Take();
    take.Shots.Add(TlShot(5.0));
    take.Shots.Add(TlShot(4.0, Trigger.OnEvent("ignition")));
    take.Shots.Add(TlShot(30.0, Trigger.OnEvent("liftoff")));
    take.Shots.Add(TlShot(3.0, Trigger.OnEvent("burnout", 2.0)));
    TimelineLayout layout = TimelineLayout.Build(take, tlNoAliases);
    Expect(layout.HasEstimates, "estimates");
    Expect(Near(layout.Shots[1].Start, TimelineLayout.EstimateSpacing) && layout.Shots[1].Estimated, "first estimate at one spacing");
    Expect(Near(layout.Shots[2].Start, 2.0 * TimelineLayout.EstimateSpacing) && layout.Shots[2].Estimated, "second estimate one spacing later");
    Expect(Near(layout.Shots[3].Start, layout.Shots[2].End), "a long shot pushes the next estimate so the blocks keep their order");
    Expect(layout.Events.Select(e => e.Key).SequenceEqual(new[] { "ignition", "liftoff", "burnout" }), "events in order");
    Expect(layout.Events.All(e => e.Estimated), "all estimated");
});

Check("timeline layout: estimates of a running take come after its take time", () =>
{
    var take = new Take();
    take.Shots.Add(TlShot(5.0, Trigger.OnEvent("liftoff")));
    TimelineLayout layout = TimelineLayout.Build(take, tlNoAliases, null, Events(), now: 50.0);
    Expect(Near(layout.Shots[0].Start, 50.0 + TimelineLayout.EstimateSpacing), "after now");
});

Check("timeline layout: aliases resolve, a bad name still shows, and seen events that no item names show", () =>
{
    var take = new Take();
    take.Shots.Add(TlShot(5.0, Trigger.OnEvent("go")));
    take.Shots.Add(TlShot(5.0, Trigger.OnEvent("nonsense:x")));
    var aliases = new Dictionary<string, string> { ["go"] = "sequence:2" };
    TimelineLayout layout = TimelineLayout.Build(take, aliases, Events(("sequence:2", 7.0), ("liftoff", 3.0)));
    Expect(Near(layout.Shots[0].Start, 7.0) && !layout.Shots[0].Estimated, "alias");
    Expect(layout.Shots[1].Estimated && layout.Events.Any(e => e.Key == "nonsense:x" && e.Estimated), "bad name estimated");
    Expect(layout.Events.Any(e => e.Key == "liftoff" && e.Source == EventTimeSource.LastRun), "unnamed seen event");
    Expect(layout.Events[0].Key == "liftoff", "events sorted by time");
    Expect(TimelineLayout.EventKey(" Liftoff ", tlNoAliases) == "liftoff", "canonical key");
});

Check("timeline layout: speed keys chain like the runner, actions take their own trigger", () =>
{
    var take = new Take();
    take.Shots.Add(TlShot(5.0));
    take.Speed.Add(new SpeedKey { At = Trigger.AtTime(2.0), To = 4.0, Ramp = 3.0 });
    take.Speed.Add(new SpeedKey { At = Trigger.After(), To = 1.0, Ramp = 1.0 });
    take.Speed.Add(new SpeedKey { At = Trigger.OnEvent("liftoff", 1.0), To = 2.0, Ramp = 0.0 });
    take.Actions.Add(new TakeAction { At = Trigger.AtTime(6.0) });
    take.Actions.Add(new TakeAction { At = Trigger.OnEvent("liftoff", -1.0) });
    TimelineLayout layout = TimelineLayout.Build(take, tlNoAliases, Events(("liftoff", 9.0)));
    Expect(Near(layout.Keys[0].Time, 2.0) && Near(layout.Keys[0].RampEnd, 5.0), "first key");
    Expect(Near(layout.Keys[1].Time, 5.0) && Near(layout.Keys[1].RampEnd, 6.0), "after the ramp before");
    Expect(Near(layout.Keys[2].Time, 10.0), "event key");
    Expect(Near(layout.Actions[0].Time, 6.0) && Near(layout.Actions[1].Time, 8.0), "actions");
    Expect(Near(layout.Duration, 10.0), "duration covers keys and events");
});

Check("timeline layout: the shots of a pre-roll loop are marked", () =>
{
    var take = new Take { PreRoll = new PreRoll { Until = "ignition" } };
    take.Shots.Add(TlShot(5.0));
    take.Shots.Add(TlShot(5.0));
    take.Shots.Add(TlShot(5.0, Trigger.OnEvent("ignition")));
    TimelineLayout layout = TimelineLayout.Build(take, tlNoAliases, Events(("ignition", 30.0)));
    Expect(layout.Shots[0].Loop && layout.Shots[1].Loop && !layout.Shots[2].Loop, "loop flags");
    Expect(Near(layout.Shots[2].Start, 30.0), "the waiting shot starts on the event");
});

Check("timeline layout: the event log gives the first time of each event and tells a finished run", () =>
{
    string csv = EventLogCsv.Header + "\n"
        + "0.000,100.000,0.000,1,,take,start,\"a, quoted\nline\"\n"
        + "4.250,104.250,0.000,1,,event,ignition,\"engines \"\"on\"\"\"\n"
        + "6.500,106.500,2.250,1,,event,liftoff,\n"
        + "7.000,107.000,2.750,1,,event,ignition,again\n"
        + "9.000,109.000,4.750,1,,take,end,the last shot is done\n";
    Dictionary<string, double> times = TimelineLayout.ReadEventTimes(new StringReader(csv), out bool finished);
    Expect(finished, "finished");
    Expect(times.Count == 2 && Near(times["ignition"], 4.25) && Near(times["liftoff"], 6.5), "first times");

    var writer = new StringWriter();
    using (var log = new EventLogCsv(writer))
        log.Write(1.5, 0, 0, 1, -1, "event", "liftoff", "with, comma");
    times = TimelineLayout.ReadEventTimes(new StringReader(writer.ToString()), out finished);
    Expect(!finished && Near(times["liftoff"], 1.5), "a log of the writer without an end");
});

Check("timeline layout: the take folders of a take, newest first", () =>
{
    string[] names =
    {
        "20261110-120000-launch_one", "20261111-090000-launch_one", "20261111-090000-launch_one~2",
        "20261112-080000-other", "notes", "20261109-235959-Launch_One",
    };
    IReadOnlyList<string> runs = TimelineLayout.RunFoldersNewestFirst(names, "launch one");
    Expect(runs.SequenceEqual(new[]
    {
        "20261111-090000-launch_one~2", "20261111-090000-launch_one", "20261110-120000-launch_one", "20261109-235959-Launch_One",
    }), "order: " + string.Join(", ", runs));
});

Check("timeline layout: snapping, zoom around the cursor and ruler steps", () =>
{
    Expect(Near(TimelineLayout.SnapDuration(2.349), 2.3) && Near(TimelineLayout.SnapDuration(0.01), 0.1)
        && Near(TimelineLayout.SnapDuration(-5.0), 0.1), "duration");
    Expect(Near(TimelineLayout.SnapTime(3.06), 3.1) && Near(TimelineLayout.SnapTime(-1.0), 0.0), "time");
    (double start, double span) = TimelineLayout.ZoomAround(10.0, 20.0, 15.0, 0.5, 0.5, 100.0);
    Expect(Near(span, 10.0) && Near(start, 12.5), "the anchor stays at the same share of the view");
    (start, span) = TimelineLayout.ZoomAround(0.0, 20.0, 5.0, 100.0, 0.5, 60.0);
    Expect(Near(span, 60.0) && Near(start, 0.0), "the span and the start are limited");
    (_, span) = TimelineLayout.ZoomAround(0.0, 1.0, 0.5, 0.01, 0.5, 60.0);
    Expect(Near(span, 0.5), "the smallest span");
    Expect(TimelineLayout.TickStep(100.0, 70.0) == 1.0 && TimelineLayout.TickStep(1.0, 70.0) == 120.0
        && TimelineLayout.TickStep(1000.0, 70.0) == 0.1 && TimelineLayout.TickStep(0.001, 70.0) == 3600.0, "steps");
});

Check("timeline layout: the session edits of the dock mark the file and keep other triggers", () =>
{
    var session = new EditSession();
    session.NewFromDefault(TempFile("timeline.json"));
    Take take = session.Take!;
    take.Speed.Clear();
    take.Speed.Add(new SpeedKey { At = Trigger.AtTime(2.0) });
    take.Speed.Add(new SpeedKey { At = Trigger.OnEvent("liftoff") });
    take.Actions.Clear();
    take.Actions.Add(new TakeAction { At = Trigger.AtTime(1.0) });
    session.Save();
    Expect(!session.Dirty, "saved first: " + session.Message);

    Expect(session.SetShotDuration(0, 7.26) && Near(take.Shots[0].Duration, 7.3) && session.Dirty, "duration snapped");
    Expect(!session.SetShotDuration(0, 7.3), "same duration is no change");
    Expect(session.SetShotDuration(0, 0.0) && Near(take.Shots[0].Duration, TimelineLayout.MinDuration), "shortest duration");
    Expect(!session.SetShotDuration(99, 3.0), "bad index");

    Expect(session.SetKeyTime(0, 4.04) && Near(take.Speed[0].At.Time, 4.0), "key time");
    Expect(!session.SetKeyTime(1, 4.0) && take.Speed[1].At.Kind == TriggerKind.Event, "an event key stays");
    Expect(session.SetActionTime(0, -3.0) && Near(take.Actions[0].At.Time, 0.0), "action time not below 0");

    take.Shots[0].Transition = new Transition();
    Expect(session.SetShotTransition(0, TransitionKind.Blend) && Near(take.Shots[0].Transition.Seconds, Transition.DefaultSeconds), "blend gets the default length");
    Expect(session.SetShotTransition(0, TransitionKind.Cut) && take.Shots[0].Transition.Seconds == 0.0, "cut has no length");
    Expect(!session.SetShotTransition(0, TransitionKind.Cut), "same kind is no change");
});

#endregion

#region World

const string WorldShots = "\"camera\": [ { \"type\": \"tripod\", \"position\": [5,0,0], \"lookAt\": [6,1,0], \"duration\": 2 } ]";

List<string> WorldNames(IEnumerable<DetectedEvent> events) => events.Select(e => e.Name).ToList();

Check("world: lat and lon give the direction of the game and come back", () =>
{
    Expect(Near3(WorldPoint.Direction(0, 0), V3.UnitX), "lat 0 lon 0 is +X");
    Expect(Near3(WorldPoint.Direction(0, 90), V3.UnitY, 1e-12), "lon 90 is +Y");
    Expect(Near3(WorldPoint.Direction(90, 0), V3.UnitZ, 1e-12), "lat 90 is +Z");
    foreach ((double lat, double lon) in new[] { (28.6, -80.6), (-45.0, 170.0), (5.0, -179.0), (89.9, 12.0) })
    {
        WorldPoint.LatLon(WorldPoint.Direction(lat, lon), out double lat2, out double lon2);
        Expect(Near(lat, lat2, 1e-9) && Near(lon, lon2, 1e-9), $"round trip {lat} {lon}: {lat2} {lon2}");
    }
});

Check("world: the axes match the Landmark axes and are right handed", () =>
{
    WorldPoint.Axes(V3.UnitX, out V3 east, out V3 north);
    Expect(Near3(east, V3.UnitY) && Near3(north, V3.UnitZ), "equator at lon 0: east +Y, north +Z");
    foreach ((double lat, double lon) in new[] { (28.6, -80.6), (-60.0, 45.0), (89.999, 3.0), (90.0, 0.0), (-90.0, 0.0) })
    {
        V3 up = WorldPoint.Direction(lat, lon);
        WorldPoint.Axes(up, out V3 e, out V3 n);
        Expect(Near(e.Length, 1, 1e-9) && Near(n.Length, 1, 1e-9), "unit axes");
        Expect(Near(V3.Dot(e, up), 0, 1e-9) && Near(V3.Dot(n, up), 0, 1e-9) && Near(V3.Dot(e, n), 0, 1e-9), "perpendicular");
        Expect(Near3(V3.Cross(up, e), n, 1e-9), $"up cross east is north at {lat} {lon}");
        if (Math.Abs(lat) < 89.0)
            Expect(n.Z > 0, "north points to the pole side");
    }
});

Check("world: an anchor from a direction keeps the place", () =>
{
    V3 position = WorldPoint.Direction(12.5, -33.25) * 6.4e6;
    WorldAnchor anchor = WorldAnchor.FromDirection("Earth", position, 0.0);
    Expect(anchor.Body == "Earth" && Near(anchor.Latitude, 12.5, 1e-9) && Near(anchor.Longitude, -33.25, 1e-9), "lat lon");
    Expect(Near3(anchor.DirectionCcf, position / position.Length, 1e-12), "direction");
});

Check("world: an anchor is read, written and keeps unknown fields", () =>
{
    string json = "{ \"anchor\": { \"body\": \"Earth\", \"latitude\": 28.5, \"longitude\": -80.6, \"altitude\": 3, \"note\": \"pad B\" }, " + WorldShots + " }";
    ShotFile file = ShotFileJson.Parse(json).File;
    WorldAnchor? anchor = file.DefaultTake.Anchor;
    Expect(anchor != null && anchor.Body == "Earth" && anchor.Latitude == 28.5 && anchor.Longitude == -80.6 && anchor.Altitude == 3, "read at the top level of a one-take file");
    Expect(anchor!.Extras.ContainsKey("note") && !file.Extras.ContainsKey("anchor"), "unknown field kept in the anchor");
    string text = ShotFileJson.Write(file);
    Expect(ShotFileJson.Write(ShotFileJson.Parse(text).File) == text, "round trip");
    Expect(text.Contains("\"note\": \"pad B\""), "extra written back");

    string listed = "{ \"takes\": [ { \"name\": \"a\", \"anchor\": { \"body\": \"Moon\", \"latitude\": 0, \"longitude\": 0 }, " + WorldShots + " }, { \"name\": \"b\", " + WorldShots + " } ] }";
    ShotFile two = ShotFileJson.Parse(ShotFileJson.Write(ShotFileJson.Parse(listed).File)).File;
    Expect(two.Takes[0].Anchor is { Body: "Moon", Altitude: 0.0 } && two.Takes[1].Anchor == null, "per take, altitude defaults to 0");

    Expect(ExpectInvalid("{ \"anchor\": { \"latitude\": 1, \"longitude\": 2 }, " + WorldShots + " }").FieldPath == "anchor.body", "body needed");
    Expect(ExpectInvalid("{ \"anchor\": { \"body\": \"Earth\", \"latitude\": 91, \"longitude\": 2 }, " + WorldShots + " }").FieldPath == "anchor.latitude", "latitude range");
    Expect(ExpectInvalid("{ \"takes\": [ { \"name\": \"a\", \"anchor\": { \"body\": \"Earth\", \"latitude\": 1 }, " + WorldShots + " } ] }").FieldPath == "takes[0].anchor.longitude", "longitude needed");
});

Check("world: the validator warns when a Landmark hides the anchor", () =>
{
    ShotFile file = ShotFileJson.Parse("{ \"site\": \"Pad\", \"anchor\": { \"body\": \"Earth\", \"latitude\": 1, \"longitude\": 2 }, " + WorldShots + " }").File;
    ValidationResult result = ShotFileValidator.Validate(file);
    Expect(result.IsValid && result.Warnings.Any(w => w.Contains("anchor has no effect")), "warning");
    file.Site = null;
    Expect(!ShotFileValidator.Validate(file).Warnings.Any(w => w.Contains("anchor")), "no warning without a site");
});

Check("world: formatVersion is written first, old files are version 1, newer ones warn", () =>
{
    ShotFileReadResult old = ShotFileJson.Parse("{ \"fps\": 30, " + WorldShots + " }");
    Expect(old.File.FormatVersion == 1 && old.Warnings.Count == 0, "missing value is version 1 without a warning");
    string text = ShotFileJson.Write(old.File);
    Expect(text.StartsWith("{\n  \"formatVersion\": 2,", StringComparison.Ordinal), "written at the top: " + text[..Math.Min(40, text.Length)]);
    ShotFileReadResult again = ShotFileJson.Parse(text);
    Expect(again.File.FormatVersion == 2 && again.Warnings.Count == 0 && ShotFileJson.Write(again.File) == text, "version 2 round trip");
    Expect(new ShotFile().FormatVersion == ShotFile.CurrentFormatVersion, "a new file has the current version");

    ShotFileReadResult newer = ShotFileJson.Parse("{ \"formatVersion\": 3, \"futureField\": 1, " + WorldShots + " }");
    Expect(newer.File.FormatVersion == 3 && newer.Warnings.Any(w => w.StartsWith("formatVersion:", StringComparison.Ordinal)), "newer version warns");
    string newerText = ShotFileJson.Write(newer.File);
    Expect(newerText.Contains("\"formatVersion\": 3") && newerText.Contains("\"futureField\": 1"), "the higher version and the unknown field are kept");
    Expect(ExpectInvalid("{ \"formatVersion\": 1.5, " + WorldShots + " }").FieldPath == "formatVersion", "fraction rejected");
    Expect(ExpectInvalid("{ \"formatVersion\": 0, " + WorldShots + " }").FieldPath == "formatVersion", "zero rejected");
});

Check("world: the new events parse, count and resolve", () =>
{
    foreach ((string text, EventKind kind, string canonical) in new[]
    {
        ("landed", EventKind.Landed, "landed"), ("Landed:2", EventKind.Landed, "landed:2"), ("soi", EventKind.Soi, "soi"),
        ("soi:3", EventKind.Soi, "soi:3"), ("apoapsis", EventKind.Apoapsis, "apoapsis"), ("periapsis:2", EventKind.Periapsis, "periapsis:2"),
        ("docked", EventKind.Docked, "docked"), ("undocked:1", EventKind.Undocked, "undocked"), ("mark:4", EventKind.Mark, "mark:4"),
        ("ignition:1", EventKind.Ignition, "ignition"), ("burnout:2", EventKind.Burnout, "burnout:2"),
    })
    {
        Expect(EventName.TryParse(text, out EventName name, out string error), text + ": " + error);
        Expect(name.Kind == kind && name.Canonical == canonical && !name.IsPredictable, text + " gives " + name.Canonical);
    }

    Expect(!EventName.TryParse("mark:0", out _, out _) && !EventName.TryParse("soi:x", out _, out _), "bad counts");
    Expect(!EventName.TryParse("dock", out _, out string unknown) && unknown.Contains("periapsis"), "the error lists the new names");
    ExpectInvalid("{ \"events\": { \"mark\": \"liftoff\" }, " + WorldShots + " }");
    Take take = ParseTake("{ \"events\": { \"cut\": \"mark:2\" }, \"camera\": [ { \"type\": \"tripod\", \"position\": [5,0,0], \"lookAt\": [6,1,0], \"duration\": 2, \"at\": { \"event\": \"cut\" } } ] }", out ShotFile file);
    Expect(EventDetector.Watched(take, file.Events).Single().Canonical == "mark:2", "an alias of a mark");
});

Check("world: landed counts after the lift-off and ignores bounces and the pad", () =>
{
    var detector = new EventDetector(Array.Empty<EventName>());
    detector.Update(0.0, Sample(contact: true, altitude: 10));
    Expect(detector.Update(0.1, Sample(contact: false, altitude: 10.1)).Count == 0, "a pad flicker is no lift-off");
    Expect(detector.Update(0.2, Sample(contact: true, altitude: 10)).Count == 0, "and no landing");
    Expect(WorldNames(detector.Update(1.0, Sample(contact: false, altitude: 12))).SequenceEqual(new[] { "liftoff" }), "lift-off");
    Expect(WorldNames(detector.Update(5.0, Sample(contact: true, altitude: 10))).SequenceEqual(new[] { "landed" }), "landed");
    detector.Update(5.2, Sample(contact: false, altitude: 10.5));
    Expect(detector.Update(5.6, Sample(contact: true, altitude: 10)).Count == 0, "a short bounce is not a landing");
    detector.Update(6.0, Sample(contact: false, altitude: 20));
    Expect(WorldNames(detector.Update(9.0, Sample(contact: true, altitude: 10))).SequenceEqual(new[] { "landed:2" }), "the second landing");

    var flying = new EventDetector(Array.Empty<EventName>());
    flying.Update(0.0, Sample(contact: false, altitude: 500));
    Expect(WorldNames(flying.Update(3.0, Sample(contact: true, altitude: 0))).SequenceEqual(new[] { "landed" }), "a take that starts in flight sees the landing");
});

Check("world: soi changes are counted with the bodies", () =>
{
    var detector = new EventDetector(Array.Empty<EventName>());
    detector.Update(0.0, new VehicleSample(false, false, 0, 1e6, 3000, Body: "Earth"));
    Expect(detector.Update(1.0, new VehicleSample(false, false, 0, 1e6, 3000, Body: "Earth")).Count == 0, "same body");
    var first = detector.Update(2.0, new VehicleSample(false, false, 0, 1e6, 3000, Body: "Moon"));
    Expect(WorldNames(first).SequenceEqual(new[] { "soi" }) && first[0].Detail == "from Earth to Moon", "soi " + first.FirstOrDefault()?.Detail);
    Expect(detector.Update(3.0, new VehicleSample(false, false, 0, 1e6, 3000)).Count == 0, "an unknown body changes nothing");
    Expect(WorldNames(detector.Update(4.0, new VehicleSample(false, false, 0, 1e6, 3000, Body: "Earth"))).SequenceEqual(new[] { "soi:2" }), "soi:2");
});

Check("world: apoapsis and periapsis need a stable orbit outside the atmosphere", () =>
{
    VehicleSample Orbit(double radial, bool closed = true, bool outside = true) =>
        new(false, false, 0, 2e5, 7800, Body: "Earth", RadialSpeed: radial, StableOrbit: closed, OutsideAtmosphere: outside);

    var detector = new EventDetector(Array.Empty<EventName>());
    detector.Update(0.0, Orbit(50));
    Expect(detector.Update(1.0, Orbit(0.05)).Count == 0 && detector.Update(2.0, Orbit(-0.05)).Count == 0, "the dead band gives nothing");
    Expect(WorldNames(detector.Update(3.0, Orbit(-20))).SequenceEqual(new[] { "apoapsis" }), "apoapsis");
    Expect(WorldNames(detector.Update(4.0, Orbit(20))).SequenceEqual(new[] { "periapsis" }), "periapsis");
    Expect(WorldNames(detector.Update(5.0, Orbit(-20))).SequenceEqual(new[] { "apoapsis:2" }), "apoapsis:2");
    Expect(detector.Update(6.0, Orbit(20, closed: false)).Count == 0, "an open orbit gives no periapsis");
    Expect(detector.Update(7.0, Orbit(-20, outside: false)).Count == 0, "inside the atmosphere gives no apoapsis");
    Expect(WorldNames(detector.Update(8.0, Orbit(20))).SequenceEqual(new[] { "periapsis:2" }), "periapsis:2");

    var hop = new EventDetector(Array.Empty<EventName>());
    hop.Update(0.0, new VehicleSample(false, true, 0, 0, 0, Body: "Earth", RadialSpeed: 0.01, StableOrbit: true));
    hop.Update(1.0, new VehicleSample(false, false, 0, 100, 50, Body: "Earth", RadialSpeed: 50, StableOrbit: true));
    Expect(!hop.Update(5.0, new VehicleSample(false, false, 0, 200, 10, Body: "Earth", RadialSpeed: -5, StableOrbit: true)).Any(e => e.Name.StartsWith("apo", StringComparison.Ordinal)), "the top of a hop in the air is no apoapsis");

    var soi = new EventDetector(Array.Empty<EventName>());
    soi.Update(0.0, Orbit(30));
    var changed = soi.Update(1.0, new VehicleSample(false, false, 0, 1e7, 900, Body: "Moon", RadialSpeed: -30, StableOrbit: true, OutsideAtmosphere: true));
    Expect(WorldNames(changed).SequenceEqual(new[] { "soi" }), "a new body starts the apsis watch again");
});

Check("world: docking and undocking follow the docked ports", () =>
{
    VehicleSample Ports(int docked, int sequence = 0, IReadOnlyList<int>? fired = null, bool changed = false) =>
        new(false, false, sequence, 4e5, 7700, fired, DockedPorts: docked, VehicleChanged: changed);

    var detector = new EventDetector(Array.Empty<EventName>());
    detector.Update(0.0, Ports(1));
    Expect(detector.Update(1.0, Ports(1)).Count == 0, "the ports at the start are no event");
    var docked = detector.Update(2.0, Ports(3, changed: true));
    Expect(WorldNames(docked).SequenceEqual(new[] { "docked" }) && docked[0].Detail == "docked ports 1 to 3", "docked");
    Expect(WorldNames(detector.Update(3.0, Ports(1))).SequenceEqual(new[] { "undocked" }), "undocked");
    Expect(WorldNames(detector.Update(4.0, Ports(2))).SequenceEqual(new[] { "docked:2" }), "docked:2");
    var staged = detector.Update(5.0, Ports(1, sequence: 1, fired: new[] { 1 }));
    Expect(WorldNames(staged).SequenceEqual(new[] { "sequence:1" }), "a port that leaves with a stage is no undocking: " + string.Join(",", WorldNames(staged)));
    Expect(WorldNames(detector.Update(5.5, Ports(2, sequence: 1))).SequenceEqual(new[] { "docked:3" }), "docked:3");
    Expect(detector.Update(5.8, Ports(1, sequence: 1)).Count == 0, "still within a second of the stage");
    detector.Update(7.0, Ports(2, sequence: 1));
    Expect(WorldNames(detector.Update(8.0, Ports(1, sequence: 1))).SequenceEqual(new[] { "undocked:2" }), "undocked:2");
});

Check("world: a new vehicle object gives no sequence events for its own list", () =>
{
    var detector = new EventDetector(Array.Empty<EventName>());
    detector.Update(0.0, new VehicleSample(false, false, 2, 4e5, 7700));
    var found = detector.Update(1.0, new VehicleSample(false, false, 7, 4e5, 7700, Fired: new[] { 7 }, DockedPorts: 2, VehicleChanged: true));
    Expect(WorldNames(found).SequenceEqual(new[] { "docked" }), "only the docking: " + string.Join(",", WorldNames(found)));
    Expect(WorldNames(detector.Update(2.0, new VehicleSample(false, false, 8, 4e5, 7700, DockedPorts: 2))).SequenceEqual(new[] { "sequence:8" }), "the next stage of the new list counts");
});

Check("world: marks are counted in order", () =>
{
    var detector = new EventDetector(Array.Empty<EventName>());
    Expect(WorldNames(detector.Update(0.0, new VehicleSample(false, true, 0, 0, 0, Marks: 1))).SequenceEqual(new[] { "mark" }), "first mark at the start");
    Expect(detector.Update(1.0, Sample()).Count == 0, "no mark");
    Expect(WorldNames(detector.Update(2.0, new VehicleSample(false, true, 0, 0, 0, Marks: 2))).SequenceEqual(new[] { "mark:2", "mark:3" }), "two marks in one frame");
    Expect(detector.Seen["mark:3"] == 2.0, "the time of the frame");
});

Check("world: events have plain names for the player", () =>
{
    foreach ((string text, string shown) in new[]
    {
        ("ignition", "Ignition"), ("ignition:2", "Ignition 2"), ("liftoff", "Lift-off"), ("burnout:2", "Burnout 2"),
        ("sequence:3", "Stage 3"), ("altitude:5000", "Altitude 5 km"), ("altitude:12500", "Altitude 12.5 km"), ("altitude:800", "Altitude 800 m"),
        ("speed:300", "Speed 300 m/s"), ("landed", "Landed"), ("soi:2", "SOI change 2"), ("apoapsis", "Apoapsis"),
        ("periapsis:3", "Periapsis 3"), ("docked", "Docked"), ("undocked:2", "Undocked 2"), ("mark:3", "Mark 3"), ("Liftoff", "Lift-off"),
        ("fairing", "fairing"), ("not an event:x", "not an event:x"), ("", ""),
    })
        Expect(EventDisplay.Name(text) == shown, $"{text} shows as \"{EventDisplay.Name(text)}\", not \"{shown}\"");
});

#endregion

#region Window

ValidationResult ValidateShots(params Shot[] shots)
{
    var file = new ShotFile();
    var take = new Take { Name = "t", IsDefault = true };
    take.Shots.AddRange(shots);
    file.Takes.Add(take);
    return ShotFileValidator.Validate(file);
}

Check("window: every preset makes a valid shot for a small and a large vehicle", () =>
{
    var vehicle = new Basis3(new V3(5.0, 100.0, -20.0), V3.UnitX, V3.UnitY, V3.UnitZ);
    foreach (double length in new[] { 0.0, 12.0, 90.0 })
    {
        foreach (ShotPreset preset in ShotPresets.All)
        {
            Shot shot = ShotPresets.Make(preset, new PresetInput(length, length / 8.0, vehicle, new V3(50.0, 0.0, 10.0)));
            ValidationResult result = ValidateShots(shot);
            Expect(result.IsValid, $"{preset} at {length} m: " + string.Join("; ", result.Errors));
            Expect(ShotPresets.Label(preset).Length > 0 && ShotPresets.Help(preset).Length > 0, $"{preset} has a label and a help line");
        }
    }
});

Check("window: presets scale with the vehicle and stand where the vehicle is", () =>
{
    var origin = new V3(1000.0, -50.0, 20.0);
    var vehicle = new Basis3(origin, V3.UnitX, V3.UnitY, V3.UnitZ);
    Shot small = ShotPresets.Make(ShotPreset.OrbitReveal, new PresetInput(10.0, 2.0, vehicle, V3.Zero));
    Shot large = ShotPresets.Make(ShotPreset.OrbitReveal, new PresetInput(100.0, 10.0, vehicle, V3.Zero));
    Expect(large.Radius > small.Radius * 5.0 && small.Target == ShotTarget.Vehicle, "orbit radius follows the length");
    Shot tower = ShotPresets.Make(ShotPreset.TowerCam, new PresetInput(40.0, 4.0, vehicle, V3.Zero));
    Expect(tower.Target == ShotTarget.Site && (tower.Position - origin).Length < 30.0, "tower cam stands close to the vehicle");
    Expect(tower.LookAt.X > tower.Position.X + 20.0, "tower cam looks up");
    Shot tracker = ShotPresets.Make(ShotPreset.GroundTracker, new PresetInput(40.0, 4.0, vehicle, V3.Zero));
    Expect(tracker.Type == ShotType.Track && tracker.Fit.HasValue && (tracker.Position - origin).Length > 400.0, "tracker is far away with a fit");
    Shot onboard = ShotPresets.Make(ShotPreset.OnboardStack, new PresetInput(40.0, 4.0, vehicle, V3.Zero));
    Expect(onboard.Type == ShotType.Part && onboard.Offset.X > 40.0 && onboard.LookAt.X == 0.0, "onboard looks down from the top");
    Shot chase = ShotPresets.Make(ShotPreset.ChaseLow, new PresetInput(40.0, 4.0, vehicle, V3.Zero));
    Expect(chase.Frame == ShotFrame.Flight && chase.Offset.Z < 0.0 && chase.Aim == ShotAim.Vehicle, "chase follows behind along the flight");
});

Check("window: a flyby stands at the point that the vehicle reaches at the middle of the shot", () =>
{
    var vehicle = new Basis3(V3.Zero, V3.UnitX, V3.UnitY, V3.UnitZ);
    var velocity = new V3(0.0, 0.0, 200.0);
    Shot shot = ShotPresets.Make(ShotPreset.Flyby, new PresetInput(30.0, 3.0, vehicle, velocity));
    Expect(shot.Type == ShotType.Track && shot.Aim == ShotAim.Vehicle, "a track shot that looks at the vehicle");
    double along = V3.Dot(shot.Position, V3.UnitZ);
    Expect(Near(along, 200.0 * shot.Duration * 0.5, 1.0), "ahead by half the flight of the shot: " + along);
    double side = Math.Sqrt(shot.Position.X * shot.Position.X + shot.Position.Y * shot.Position.Y);
    Expect(side > 20.0, "beside the path");
    Shot standing = ShotPresets.Make(ShotPreset.Flyby, new PresetInput(30.0, 3.0, vehicle, V3.Zero));
    Expect(standing.Position.X > 60.0, "a standing vehicle uses its axis as the direction");
});

Check("window: the frame choice maps to the target and the frame of each shot type", () =>
{
    foreach (ShotType type in Enum.GetValues<ShotType>())
    {
        foreach (FrameChoice choice in ShotLabels.Choices(type))
        {
            Shot shot = ShotEdit.NewShot(type);
            Expect(ShotLabels.SetFrame(shot, choice), $"{type} takes {choice}");
            Expect(ShotLabels.FrameOf(shot) == choice, $"{type} {choice} reads back as {ShotLabels.FrameOf(shot)}");
            Expect(ValidateShots(shot).IsValid, $"{type} {choice} is valid");
            Expect(ShotLabels.Frame(choice, type).Length > 0 && ShotLabels.FrameHelp(choice).Length > 0, "label");
        }

        Expect(ShotLabels.Type(type).Length > 0 && ShotLabels.TypeHelp(type).Length > 0, $"{type} label");
    }

    Shot track = ShotEdit.NewShot(ShotType.Track);
    Expect(!ShotLabels.SetFrame(track, FrameChoice.Vehicle) && ShotLabels.FrameOf(track) == FrameChoice.World, "a track shot has one frame");
    Shot chase = ShotEdit.NewShot(ShotType.Chase);
    Expect(!ShotLabels.SetFrame(chase, FrameChoice.World), "a chase shot has no world frame");
    Expect(ShotLabels.Frame(FrameChoice.Horizon, ShotType.Part) == "Part, horizon level", "part words");
    Expect(ShotLabels.Frame(FrameChoice.World, ShotType.Tripod) == "World point" && ShotLabels.Type(ShotType.Tripod) == "Fixed", "plain words");
});

Check("window: add key here makes each kind of shot from the camera pose", () =>
{
    var vehicle = new Basis3(new V3(10.0, 0.0, 0.0), V3.UnitX, V3.UnitY, V3.UnitZ);
    var position = new V3(15.0, 30.0, 40.0);
    V3 forward = (new V3(25.0, 0.0, 0.0) - position).NormalizedOr(V3.UnitX);
    var camera = new CapturedCamera(position, forward, 35.0);
    foreach (KeyKind kind in Enum.GetValues<KeyKind>())
    {
        Expect(ShotCapture.KeyLabel(kind).Length > 0 && ShotCapture.KeyHelp(kind).Length > 0, $"{kind} label");
        if (kind == KeyKind.PathPoint)
            continue;
        Shot? shot = ShotCapture.NewKey(kind, camera, vehicle, 30.0, out string message);
        Expect(shot != null, $"{kind}: {message}");
        Expect(Near(shot!.Fov, 35.0), $"{kind} takes the field of view");
        Expect(ValidateShots(shot).IsValid, $"{kind} is valid");
    }

    Shot fixedShot = ShotCapture.NewKey(KeyKind.Fixed, camera, vehicle, 30.0, out _)!;
    Expect(fixedShot.Type == ShotType.Tripod && fixedShot.Target == ShotTarget.Site && Near3(fixedShot.Position, position), "fixed at the world point");
    Shot mount = ShotCapture.NewKey(KeyKind.PartMount, camera, vehicle, 30.0, out _)!;
    Expect(mount.Type == ShotType.Part && Near3(mount.Offset, position - vehicle.Origin), "part mount in the vehicle frame");
    Shot orbit = ShotCapture.NewKey(KeyKind.Orbit, camera, vehicle, 30.0, out _)!;
    Expect(orbit.Target == ShotTarget.Vehicle && Near(orbit.Radius, 50.0, 0.01) && Near(orbit.Height, 5.0, 0.01), "orbit through the camera");
    Expect(ShotCapture.NewKey(KeyKind.PathPoint, camera, vehicle, 30.0, out _) == null, "a path point is no new key");
    var onAxis = new CapturedCamera(new V3(20.0, 0.0, 0.0), V3.UnitX, 40.0);
    Expect(ShotCapture.NewKey(KeyKind.Orbit, onAxis, vehicle, 30.0, out string axis) == null && axis.Length > 0, "no orbit on the axis");
});

Check("window: path points from the camera start a path and add to its end", () =>
{
    var vehicle = new Basis3(V3.Zero, V3.UnitX, V3.UnitY, V3.UnitZ);
    var first = new CapturedCamera(new V3(5.0, 20.0, 0.0), new V3(0.0, -1.0, 0.0), 40.0);
    Shot path = ShotCapture.NewPath(first, vehicle, 30.0);
    Expect(path.Type == ShotType.Path && path.Points.Count == 1 && path.Points[0].T == 0.0, "one point at the start");
    Expect(ValidateShots(path).IsValid, "a one point path is valid");
    var second = new CapturedCamera(new V3(5.0, 0.0, 20.0), new V3(0.0, 0.0, -1.0), 30.0);
    Expect(ShotCapture.AppendPathPoint(path, second, vehicle, 30.0, out _), "append");
    Expect(path.Points.Count == 2 && path.Points[0].T == 0.0 && path.Points[1].T == 1.0, "two points");
    Expect(ShotCapture.AppendPathPoint(path, first, vehicle, 30.0, out _), "append again");
    Expect(path.Points.Count == 3 && Near(path.Points[1].T, 0.667, 0.001) && path.Points[2].T == 1.0, "earlier points move to the start");
    Expect(PathMath.CheckTimes(path.Points) == null && ValidateShots(path).IsValid, "times in order");
    Expect(!ShotCapture.AppendPathPoint(ShotEdit.NewShot(ShotType.Chase), first, vehicle, 30.0, out _), "only a path takes a point");
});

Check("window: the session inserts a shot after the selection and makes a take without a file", () =>
{
    var session = new EditSession();
    session.AddTake();
    Expect(session.Document != null && session.Document.Takes.Count == 1 && session.Take!.IsDefault, "a new document with a default take");
    Expect(session.Take!.Shots.Count == 1 && session.Take.Shots[0].Aim == ShotAim.Vehicle && session.Validation.IsValid, "one generic shot");
    session.InsertShot(ShotEdit.NewShot(ShotType.Orbit));
    Expect(session.ShotIndex == 1 && session.Take.Shots[1].Type == ShotType.Orbit, "after the selection");
    session.ShotIndex = 0;
    session.InsertShot(ShotEdit.NewShot(ShotType.Tripod));
    Expect(session.ShotIndex == 1 && session.Take.Shots[1].Type == ShotType.Tripod && session.Take.Shots[2].Type == ShotType.Orbit, "between");
    session.ShotIndex = -1;
    session.InsertShot(ShotEdit.NewShot(ShotType.Track));
    Expect(session.ShotIndex == 3 && session.Take.Shots[3].Type == ShotType.Track && session.Dirty, "at the end");
    session.AddTake();
    Expect(session.Document!.Takes.Count == 2 && session.Document.Takes.Count(t => t.IsDefault) == 1, "a second take is not the default");
});

Check("window: the default file has no world point shots and every take is valid", () =>
{
    ShotFile file = DefaultShotFile.Load().File;
    Expect(ShotFileValidator.Validate(file).IsValid, "valid");
    foreach (Take take in file.Takes)
    {
        Expect(take.Site == null, take.Name + " names no site");
        foreach (Shot shot in take.Shots)
            Expect(shot.Target == ShotTarget.Vehicle && shot.Type != ShotType.Track, $"{take.Name} {shot.Type} follows the vehicle");
        Expect(take.Shots.Count(s => s.Fit.HasValue) >= 3, take.Name + " frames the vehicle with a fit");
    }

    Expect(file.Site == null && file.KeepOut.Count == 0, "no site and no keep-out boxes");
    Expect(file.DefaultTake.PreRoll?.Until == "ignition", "the launch take loops until the ignition");
});

#endregion

#region Quick

QuickWorld QuickWorldAt(V3 surfaceVelocity, V3? inertialVelocity = null, double altitude = 1000.0)
{
    const double radius = 6_371_000.0;
    return new QuickWorld
    {
        Vehicle = Basis3.Identity,
        Centre = V3.Zero,
        Length = 30.0,
        Width = 4.0,
        BodyCentre = new V3(-(radius + altitude), 0.0, 0.0),
        Pole = V3.UnitZ,
        SurfaceVelocity = surfaceVelocity,
        InertialVelocity = inertialVelocity ?? surfaceVelocity,
        Altitude = altitude,
        AtmosphereHeight = 100_000.0,
    };
}

Check("quick: the flyby point is fixed to the ground in the air and falls freely above it", () =>
{
    Expect(QuickMath.ChooseAnchor(5_000.0, 100_000.0, false) == FlybyAnchor.Ground, "in the atmosphere");
    Expect(QuickMath.ChooseAnchor(200_000.0, 100_000.0, false) == FlybyAnchor.Orbit, "above the atmosphere");
    Expect(QuickMath.ChooseAnchor(200_000.0, 100_000.0, true) == FlybyAnchor.Ground, "contact keeps the ground");
    Expect(QuickMath.ChooseAnchor(5_000.0, 0.0, false) == FlybyAnchor.Ground, "low over a body without air");
    Expect(QuickMath.ChooseAnchor(20_000.0, 0.0, false) == FlybyAnchor.Orbit, "high over a body without air");
});

Check("quick: a ground flyby point is ahead, to the side and above the line of flight", () =>
{
    QuickWorld world = QuickWorldAt(new V3(0.0, 0.0, 100.0));
    FlybyPlan plan = QuickMath.PlaceFlyby(world, FlybyAnchor.Ground, new QuickOptions(), 1);
    // 100 m/s for 6 s is 600 m ahead (north), 15 % of that to the side and 30 % of the side up.
    Expect(Near3(plan.Position, new V3(63.0, 210.0, 600.0), 1e-6), $"position {plan.Position}");
    Expect(Near3(plan.PointVelocity, V3.Zero), "a ground point has no velocity of its own");
    Expect(Near(plan.Distance, plan.Position.Length, 1e-9), "distance");
    FlybyPlan left = QuickMath.PlaceFlyby(world, FlybyAnchor.Ground, new QuickOptions(), -1);
    Expect(Near(left.Position.Y, -210.0, 1e-6), "the other side");
    FlybyPlan fixedSide = QuickMath.PlaceFlyby(world, FlybyAnchor.Ground, new QuickOptions { FlybySide = 40.0 }, 1);
    Expect(Near(fixedSide.Position.Y, 40.0, 1e-6), "a set side distance");
});

Check("quick: an orbit flyby point starts with the vehicle velocity minus the pass speed", () =>
{
    QuickWorld world = QuickWorldAt(new V3(0.0, 0.0, 7000.0), new V3(0.0, 0.0, 7500.0), 200_000.0);
    FlybyPlan plan = QuickMath.PlaceFlyby(world, FlybyAnchor.Orbit, new QuickOptions(), 1);
    Expect(Near3(plan.PointVelocity, new V3(0.0, 0.0, 7485.0), 1e-9), $"velocity {plan.PointVelocity}");
    Expect(Near(plan.Position.Z, 90.0, 1e-6), "15 m/s for 6 s ahead along the inertial velocity");
    Expect(plan.Anchor == FlybyAnchor.Orbit, "anchor");
});

Check("quick: a standing or climbing vehicle still gets a usable flyby point", () =>
{
    QuickWorld standing = QuickWorldAt(V3.Zero);
    FlybyPlan plan = QuickMath.PlaceFlyby(standing, FlybyAnchor.Ground, new QuickOptions(), 1);
    // The nose points up, so the point goes north at the shortest distance: 2 sizes plus 10 m.
    Expect(Near(plan.Position.Z, 70.0, 1e-6) && plan.Position.Y > 0.0, $"standing {plan.Position}");
    QuickWorld climbing = QuickWorldAt(new V3(100.0, 0.0, 0.0));
    FlybyPlan up = QuickMath.PlaceFlyby(climbing, FlybyAnchor.Ground, new QuickOptions(), 1);
    Expect(Near(up.Position.X, 600.0, 1e-6), $"ahead is up {up.Position}");
    Expect(Near(up.Position.Y, 210.0, 1e-6) && Near(up.Position.Z, 0.0, 1e-6), "the side is east with no lift");
});

Check("quick: a flyby is done only when the vehicle moves away and is far past", () =>
{
    V3 velocity = new(0.0, 0.0, 100.0);
    Expect(!QuickMath.FlybyDone(new V3(0.0, -10.0, -500.0), velocity, 600.0), "approaching");
    Expect(!QuickMath.FlybyDone(new V3(0.0, -10.0, 300.0), velocity, 600.0), "past but near");
    Expect(QuickMath.FlybyDone(new V3(0.0, -10.0, 1000.0), velocity, 600.0), "far past");
    Expect(!QuickMath.FlybyDone(new V3(0.0, -10.0, 1000.0), V3.Zero, 600.0), "a standing vehicle never ends it");
});

Check("quick: chase and orbit cameras sit in the local horizon frame", () =>
{
    QuickWorld world = QuickWorldAt(new V3(0.0, 0.0, 100.0));
    var options = new QuickOptions { ChaseDistance = 2.0, ChaseHeight = 0.5 };
    CameraPose chase = QuickMath.ChasePose(world, V3.UnitZ, options);
    Expect(Near3(chase.Position, new V3(15.0, 0.0, -60.0), 1e-6), $"chase {chase.Position}");
    Expect(Near3(chase.LookAt, V3.Zero) && Near3(chase.Up, V3.UnitX, 1e-9), "chase look and up");
    CameraPose east = QuickMath.ChasePose(world, V3.UnitY, options);
    Expect(Near3(east.Position, new V3(15.0, -60.0, 0.0), 1e-6), $"chase east {east.Position}");

    var orbit = new QuickOptions { OrbitDistance = 2.0, OrbitElevation = 0.0 };
    Expect(Near3(QuickMath.OrbitPose(world, 0.0, orbit).Position, new V3(0.0, 0.0, 60.0), 1e-6), "north");
    Expect(Near3(QuickMath.OrbitPose(world, 90.0, orbit).Position, new V3(0.0, 60.0, 0.0), 1e-6), "east");
    orbit.OrbitElevation = 30.0;
    CameraPose high = QuickMath.OrbitPose(world, 45.0, orbit);
    Expect(Near(high.Position.Length, 60.0, 1e-6) && Near(high.Position.X, 30.0, 1e-6), "elevation and distance");
});

Check("quick: the onboard camera sits outside the part and looks along the stack", () =>
{
    CameraPose down = QuickMath.OnboardPose(new V3(-1.0, -1.0, -1.0), new V3(3.0, 1.0, 1.0), 30.0, false, 70.0);
    Expect(down.Frame == PoseFrame.Vehicle, "vehicle frame");
    Expect(down.Position.Y > 1.0 && Near(down.Position.Z, 0.0) && down.Position.X > 2.0 && down.Position.X < 3.0, $"down {down.Position}");
    Expect((down.LookAt - down.Position).X < -29.0 && Near3(down.Up, V3.UnitY), "looks to the base, up away from the hull");
    CameraPose nose = QuickMath.OnboardPose(new V3(-1.0, -1.0, -1.0), new V3(3.0, 1.0, 1.0), 30.0, true, 70.0);
    Expect(nose.Position.X < 0.0 && (nose.LookAt - nose.Position).X > 29.0, "looks to the nose from the base");
    CameraPose booster = QuickMath.OnboardPose(new V3(0.0, -4.0, 2.0), new V3(10.0, -2.0, 4.0), 30.0, false, 70.0);
    V3 across = new(0.0, booster.Position.Y, booster.Position.Z);
    Expect(across.Length > new V3(0.0, -3.0, 3.0).Length + 1.0, $"a side part gets the camera on its outer side {booster.Position}");
    Expect(PoseMath.ToSiteFrame(down, new Basis3(new V3(5.0, 0.0, 0.0), V3.UnitX, V3.UnitY, V3.UnitZ)).Position.X > 7.0, "the part shot maps it");
});

Check("quick: the ground tracker stands the set distance from the point under the vehicle", () =>
{
    QuickWorld world = QuickWorldAt(new V3(0.0, 0.0, 100.0));
    V3 direction = QuickMath.TrackerDirection(world, V3.UnitZ, 1500.0);
    Expect(Near(direction.Length, 1.0, 1e-12), "unit");
    double ground = Math.Acos(Math.Clamp(V3.Dot(direction, V3.UnitX), -1.0, 1.0)) * world.BodyRadius;
    Expect(Near(ground, 1500.0 * Math.Sqrt(1.0 + 1.0 / 16.0), 0.5), $"distance {ground}");
    Expect(direction.Y > 0.0 && direction.Z < 0.0, "to the right of the flight and behind");
    Expect(QuickMath.TrackerBlock(149_000.0) == null && QuickMath.TrackerBlock(151_000.0) != null, "range limit");
});

Check("quick: a camera point stays above the ground", () =>
{
    V3 centre = new(-1000.0, 0.0, 0.0);
    V3 lifted = QuickMath.KeepAbove(new V3(-10.0, 0.0, 0.0), centre, 995.0);
    Expect(Near3(lifted, new V3(-5.0, 0.0, 0.0), 1e-9), $"lifted {lifted}");
    Expect(Near3(QuickMath.KeepAbove(new V3(10.0, 0.0, 0.0), centre, 995.0), new V3(10.0, 0.0, 0.0)), "high enough");
});

Check("quick: the auto-zoom is the fit math, and the long lens zooms further", () =>
{
    Expect(Near(QuickMath.AutoZoomFov(30.0, 4.0, 100.0, 0.35, QuickMath.NormalMinFov), PoseMath.FitFov(30.0, 4.0, 100.0, 0.35)), "near");
    Expect(Near(QuickMath.AutoZoomFov(30.0, 4.0, 50_000.0, 0.35, QuickMath.NormalMinFov), PoseMath.FitMinFov), "normal lower limit");
    double longLens = QuickMath.AutoZoomFov(30.0, 4.0, 2_000.0, 0.35, QuickMath.LongLensMinFov);
    Expect(longLens < PoseMath.FitMinFov && longLens > QuickMath.LongLensMinFov, $"long lens {longLens}");
    Expect(Near(QuickMath.AutoZoomFov(30.0, 4.0, 1e7, 0.35, QuickMath.LongLensMinFov), QuickMath.LongLensMinFov), "long lens limit");
});

Check("quick: a free-falling point stays on its orbit", () =>
{
    const double mu = 3.986004418e14;
    const double r = 6_771_000.0;
    double v = Math.Sqrt(mu / r);
    var point = new FreeFallPoint(new V3(r, 0.0, 0.0), new V3(0.0, v, 0.0), mu);
    for (int i = 0; i < 60; i++)
        point.Advance(10.0);
    double angle = v / r * 600.0;
    V3 expected = new(r * Math.Cos(angle), r * Math.Sin(angle), 0.0);
    Expect(Near(point.Position.Length, r, 1.0), $"radius {point.Position.Length - r}");
    Expect(Near3(point.Position, expected, 1.0), $"position error {(point.Position - expected).Length}");
    point.Advance(0.0);
    point.Advance(-5.0);
    var straight = new FreeFallPoint(new V3(1.0, 2.0, 3.0), new V3(1.0, 0.0, 0.0), 0.0);
    straight.Advance(2.0);
    Expect(Near3(straight.Position, new V3(3.0, 2.0, 3.0), 1e-9), "no gravity is a straight line");
});

Check("quick: the part list steps with wrap", () =>
{
    Expect(QuickMath.Step(0, -1, 5) == 4 && QuickMath.Step(4, 1, 5) == 0 && QuickMath.Step(2, 7, 5) == 4, "wrap");
    Expect(QuickMath.Step(3, 1, 0) == 0, "empty list");
});

#endregion

#region Replay dock

Check("replay dock: recorded event times are exact and nothing is estimated", () =>
{
    var take = new Take();
    take.Shots.Add(TlShot(5.0));
    take.Shots.Add(TlShot(4.0, Trigger.OnEvent("ignition", 1.0)));
    take.Shots.Add(TlShot(3.0));
    var recorded = Events(("ignition", 7.5), ("liftoff", 9.0));
    var lastRun = Events(("ignition", 2.0));
    TimelineLayout layout = TimelineLayout.Build(take, tlNoAliases, lastRun, null, 0.0, recorded);
    Expect(Near(layout.Shots[1].Start, 8.5) && !layout.Shots[1].Estimated, "ignition from the recording, not the last run");
    Expect(Near(layout.Shots[2].Start, 12.5), "the after shot follows");
    Expect(!layout.HasEstimates && layout.Unreached.Count == 0, "nothing estimated or unreached");
    Expect(layout.Events.All(e => e.Source == EventTimeSource.Recording), "all events from the recording");
    Expect(layout.Events.Any(e => e.Key == "liftoff" && Near(e.Time, 9.0)), "an event that no item names still shows");
});

Check("replay dock: an item that waits for an event the recording lacks never starts, and nor does what follows it", () =>
{
    var take = new Take();
    take.Shots.Add(TlShot(5.0));
    take.Shots.Add(TlShot(4.0, Trigger.OnEvent("burnout")));
    take.Shots.Add(TlShot(3.0));
    take.Shots.Add(TlShot(2.0, Trigger.AtTime(30.0)));
    take.Speed.Add(new SpeedKey { At = Trigger.OnEvent("burnout"), To = 0.5, Ramp = 1.0 });
    take.Speed.Add(new SpeedKey { At = Trigger.After(), To = 2.0, Ramp = 0.0 });
    take.Speed.Add(new SpeedKey { At = Trigger.AtTime(3.0), To = 1.0, Ramp = 0.0 });
    take.Actions.Add(new TakeAction { At = Trigger.OnEvent("burnout") });
    TimelineLayout layout = TimelineLayout.Build(take, tlNoAliases, recorded: Events(("liftoff", 2.0)));
    Expect(layout.Shots.Select(s => s.Index).SequenceEqual(new[] { 0, 3 }), "shots 2 and 3 are left out, the time shot stays");
    Expect(layout.Keys.Select(k => k.Index).SequenceEqual(new[] { 2 }), "only the speed key at a time stays");
    Expect(layout.Actions.Count == 0, "the action is left out");
    Expect(layout.Unreached.Count == 3 && layout.Unreached.All(u => u.EventKey == "burnout"), "the three items that wait for burnout");
    Expect(layout.Unreached.Select(u => u.Kind).SequenceEqual(new[] { TimelineItemKind.Shot, TimelineItemKind.Key, TimelineItemKind.Action }), "kinds in order");
    Expect(!layout.HasEstimates && layout.Events.All(e => e.Key != "burnout"), "no estimated burnout");
});

Check("replay dock: the marks of a recording are sorted, extend the duration, and stop at the end of the take", () =>
{
    var marks = TimelineLayout.MarkersBefore(new[]
    {
        ("Separation: B", 40.0, 20.0),
        ("Separation: A", 10.0, 6.0),
        ("Separation: C", 90.0, 25.0),
    }, recordingEnd: 60.0);
    Expect(marks.Select(m => m.Label).SequenceEqual(new[] { "Separation: A", "Separation: B" }), "a mark after the end is left out");
    var take = new Take();
    take.Shots.Add(TlShot(5.0));
    TimelineLayout layout = TimelineLayout.Build(take, tlNoAliases, recorded: Events(), markers: marks);
    Expect(layout.Markers.Count == 2 && Near(layout.Markers[0].Time, 6.0), "markers in the layout");
    Expect(Near(layout.Duration, 20.0), "the last mark sets the duration");
});

Check("replay dock: a scrub stays inside the replay and a playing replay keeps its play head in view", () =>
{
    Expect(Near(TimelineLayout.ScrubTime(-3.0, 50.0), 0.0) && Near(TimelineLayout.ScrubTime(70.0, 50.0), 50.0), "clamped");
    Expect(Near(TimelineLayout.ScrubTime(12.5, 50.0), 12.5) && Near(TimelineLayout.ScrubTime(double.NaN, 50.0), 0.0), "inside and NaN");
    Expect(Near(TimelineLayout.FollowStart(10.0, 20.0, 15.0), 10.0), "play head in view, the view stays");
    Expect(Near(TimelineLayout.FollowStart(10.0, 20.0, 29.5), 27.5), "near the right edge, the view turns a page");
    Expect(Near(TimelineLayout.FollowStart(10.0, 20.0, 1.0), 0.0), "behind the view, it never starts before 0");
});

#endregion

#region Key placement
Take PlacementTake()
{
    var take = new Take { Name = "launch", PreRoll = new PreRoll { Until = "liftoff" } };
    take.Shots.Add(new Shot { Type = ShotType.Orbit, Duration = 10.0 });
    take.Shots.Add(new Shot { Type = ShotType.Tripod, Duration = 15.0, At = Trigger.OnEvent("liftoff") });
    take.Shots.Add(new Shot { Type = ShotType.Chase, Duration = 8.0 });
    return take;
}

double?[] PlacementStarts(Take take, double liftoff)
{
    var runner = new TimelineRunner(take, new Dictionary<string, string>());
    runner.Update(100.0, new Dictionary<string, double> { ["liftoff"] = liftoff });
    return runner.Starts.Shots;
}

Check("key placement: a key after the lift-off goes after the shot that shows then, not into the pre-roll loop", () =>
{
    Take take = PlacementTake();
    var aliases = new Dictionary<string, string>();
    double?[] starts = PlacementStarts(take, 20.0);
    var seen = new Dictionary<string, double> { ["liftoff"] = 20.0 };
    Trigger at = Trigger.OnEvent("liftoff", 10.0);
    double start = ShotPlacer.StartOf(at, seen, aliases)!.Value;
    Expect(Near(start, 30.0), $"start {start}");
    int loop = ShotPlacer.LoopShots(take, aliases);
    Expect(loop == 1, $"loop shots {loop}");
    ShotPlacement place = ShotPlacer.Place(take, start, starts, loop, selected: 0);
    Expect(place.Index == 2 && !place.InPreRoll, $"index {place.Index}");
    Expect(place.Duration is double d && Near(d, 5.0), $"fills the time until the chase starts: {place.Duration}");
    take.Shots.Insert(place.Index, new Shot { Type = ShotType.Track, Duration = place.Duration!.Value, At = at });
    double?[] after = PlacementStarts(take, 20.0);
    Expect(Near(after[2]!.Value, 30.0) && Near(after[3]!.Value, 35.0), "the new shot starts at its time and the chase keeps its start");
});

Check("key placement: a key during the pre-roll loop goes after the shot that ends the loop", () =>
{
    Take take = PlacementTake();
    double?[] starts = PlacementStarts(take, 20.0);
    ShotPlacement place = ShotPlacer.Place(take, 5.0, starts, 1, selected: 0);
    Expect(place.Index == 2 && place.InPreRoll, $"index {place.Index}, in pre-roll {place.InPreRoll}");
    Expect(place.Duration is double d && Near(d, 30.0), $"duration {place.Duration}");
    take.Shots.Insert(place.Index, new Shot { Type = ShotType.Track, Duration = place.Duration!.Value, At = Trigger.AtTime(5.0) });
    double?[] after = PlacementStarts(take, 20.0);
    Expect(Near(after[3]!.Value, 35.0), "the chase keeps its start");
});

Check("key placement: a key before a shot with its own start keeps its duration", () =>
{
    var take = new Take { Name = "plain" };
    take.Shots.Add(new Shot { Type = ShotType.Orbit, Duration = 10.0 });
    take.Shots.Add(new Shot { Type = ShotType.Tripod, Duration = 10.0, At = Trigger.AtTime(40.0) });
    double?[] starts = PlacementStarts(take, 0.0);
    ShotPlacement place = ShotPlacer.Place(take, 4.0, starts, 0, selected: 1);
    Expect(place.Index == 1 && place.Duration == null && !place.InPreRoll, $"index {place.Index}, duration {place.Duration}");
    ShotPlacement late = ShotPlacer.Place(take, 45.0, starts, 0, selected: 0);
    Expect(late.Index == 2 && late.Duration == null, $"after the last shot: {late.Index}");
    ShotPlacement early = ShotPlacer.Place(take, -1.0, starts, 0, selected: 1);
    Expect(early.Index == 0, $"before every shot: {early.Index}");
});

Check("key placement: without known starts the key goes after the selected shot, behind the pre-roll loop", () =>
{
    Take take = PlacementTake();
    Expect(ShotPlacer.Place(take, 30.0, null, 1, selected: 0).Index == 2, "behind the loop");
    Expect(ShotPlacer.Place(take, 30.0, null, 1, selected: 2).Index == 3, "after the selected shot");
    Expect(ShotPlacer.Place(take, 30.0, new double?[1], 0, selected: 0).Index == 1, "starts of another take count as unknown");
    Expect(ShotPlacer.LoopShots(new Take(), new Dictionary<string, string>()) == 0, "no loop");
    Expect(ShotPlacer.StartOf(Trigger.OnEvent("ignition"), new Dictionary<string, double>(), new Dictionary<string, string>()) == null, "an event not seen waits");
    Expect(ShotPlacer.StartOf(Trigger.OnEvent("go", 2.0), new Dictionary<string, double> { ["liftoff"] = 3.0 },
        new Dictionary<string, string> { ["go"] = "liftoff" }) is double s && Near(s, 5.0), "an alias resolves");
});

Check("key placement: the edit session inserts at an index and selects the shot", () =>
{
    string path = Path.Combine(Path.GetTempPath(), "cinecam-place-" + Guid.NewGuid().ToString("N") + ".json");
    var session = new EditSession();
    session.NewFromDefault(path);
    try
    {
        int count = session.Take!.Shots.Count;
        var shot = new Shot { Type = ShotType.Tripod, Duration = 3.0 };
        session.InsertShot(shot, 0);
        Expect(session.ShotIndex == 0 && ReferenceEquals(session.Take.Shots[0], shot), "inserted at 0 and selected");
        session.InsertShot(new Shot { Type = ShotType.Tripod, Duration = 3.0 }, 999);
        Expect(session.ShotIndex == count + 1, "an index past the end goes to the end");
    }
    finally
    {
        File.Delete(path);
    }
});
#endregion

#region Key placement in a pause

Check("key placement: a key in a live pause during the pre-roll loop goes behind the shots that wait for the lift-off", () =>
{
    Take take = PlacementTake();
    var aliases = new Dictionary<string, string>();
    double?[]? starts = ShotPlacer.StartsAt(take, 5.0, new Dictionary<string, double>(), aliases);
    Expect(starts != null && starts[1] == null && starts[2] == null, "the lift-off has not come, so the waiting shot and the chase have no start");
    ShotPlacement place = ShotPlacer.Place(take, 5.0, starts, ShotPlacer.LoopShots(take, aliases), selected: 0);
    Expect(place.Index == 3 && place.Duration == null && place.InPreRoll, $"index {place.Index}, duration {place.Duration}, in pre-roll {place.InPreRoll}");
    take.Shots.Insert(place.Index, new Shot { Type = ShotType.Track, Duration = 5.0, At = Trigger.AtTime(5.0) });
    double?[] after = PlacementStarts(take, 20.0);
    Expect(Near(after[1]!.Value, 20.0) && Near(after[2]!.Value, 35.0) && Near(after[3]!.Value, 5.0),
        $"the chase stays behind the lift-off shot: {string.Join(", ", after)}");
});

Check("key placement: a second key in one pause uses the starts of the take as the first key changed it", () =>
{
    var take = new Take { Name = "plain" };
    take.Shots.Add(new Shot { Type = ShotType.Orbit, Duration = 10.0 });
    take.Shots.Add(new Shot { Type = ShotType.Chase, Duration = 10.0 });
    take.Shots.Add(new Shot { Type = ShotType.Tripod, Duration = 10.0 });
    var aliases = new Dictionary<string, string>();
    var seen = new Dictionary<string, double>();

    ShotPlacement first = ShotPlacer.Place(take, 12.0, ShotPlacer.StartsAt(take, 12.0, seen, aliases), 0, selected: 0);
    Expect(first.Index == 2 && first.Duration is double d1 && Near(d1, 8.0), $"first: index {first.Index}, duration {first.Duration}");
    take.Shots.Insert(first.Index, new Shot { Type = ShotType.Track, Duration = first.Duration!.Value, At = Trigger.AtTime(12.0) });

    double?[]? starts = ShotPlacer.StartsAt(take, 12.0, seen, aliases);
    Expect(starts is { Length: 4 }, "one start per shot of the changed take");
    ShotPlacement second = ShotPlacer.Place(take, 15.03, starts, 0, selected: 2);
    Expect(second.Index == 3 && second.Duration is double d2 && Near(d2, 4.97, 1e-9), $"second: index {second.Index}, duration {second.Duration}");
    take.Shots.Insert(second.Index, new Shot { Type = ShotType.Track, Duration = second.Duration!.Value, At = Trigger.AtTime(15.03) });

    double?[] after = PlacementStarts(take, 0.0);
    Expect(Near(after[2]!.Value, 12.0) && Near(after[3]!.Value, 15.03) && Near(after[4]!.Value, 20.0, 1e-9),
        $"both keys start at their times and the tripod keeps its exact start: {string.Join(", ", after)}");
});

Check("key placement: the starts of a take with errors are unknown", () =>
{
    var take = new Take { Name = "broken", PreRoll = new PreRoll { Until = "liftoff" } };
    take.Shots.Add(new Shot { Type = ShotType.Orbit, Duration = 10.0 });
    Expect(ShotPlacer.StartsAt(take, 1.0, new Dictionary<string, double>(), new Dictionary<string, string>()) == null, "no shot ends the loop");
});

#endregion

#region Labels

Check("labels: the frame labels are short", () =>
{
    foreach (ShotType type in new[] { ShotType.Chase, ShotType.Part })
        foreach (FrameChoice choice in new[] { FrameChoice.Vehicle, FrameChoice.Horizon, FrameChoice.Flight })
            Expect(ShotLabels.Frame(choice, type).Length <= 22, $"{type} {choice} label fits a combo");
    Expect(ShotLabels.Frame(FrameChoice.Flight, ShotType.Chase) == "Vehicle, along flight", "flight frame words");
});

#endregion

#region Release checks

Check("release: a checkpoint in a clean document with errors is kept as an unsaved change", () =>
{
    string folder = Path.Combine(Path.GetTempPath(), "cinecam-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        string path = Path.Combine(folder, "shots.json");
        File.WriteAllText(path, DefaultShotFile.Json);
        var session = new EditSession();
        Expect(session.Load(path), "load");
        Take take = session.Document!.DefaultTake;
        take.Shots[0].Duration = 0.0;
        Expect(session.RecordCheckpoint(take.Name, "CineCam broken") == CheckpointRecord.NotSaved, "the refused save is reported");
        Expect(session.Dirty && take.Checkpoint == "CineCam broken", "the name stays as an unsaved change");
        Expect(ShotFileJson.Load(path).File.DefaultTake.Checkpoint != "CineCam broken", "the file is not written");
    }
    finally
    {
        Directory.Delete(folder, recursive: true);
    }
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
