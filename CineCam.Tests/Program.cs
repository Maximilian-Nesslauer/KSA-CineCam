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
