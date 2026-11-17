using System.Reflection;
using CineCam.Api;
using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Editing;

namespace CineCam.Tests;

// The cue action, the shot name, the replay feed and the event surface for other mods.
internal static class IntegrationTests
{
    private const string CueFile = """
    {
      "takes": [
        {
          "name": "launch",
          "camera": [
            { "type": "tripod", "name": "pad-wide", "position": [1, 2, 3], "lookAt": [0, 0, 0], "duration": 30 }
          ],
          "actions": [
            { "at": 0, "action": "cue", "name": "intro" },
            { "at": { "event": "liftoff" }, "action": "cue", "name": "overlay-in" },
            { "at": 1, "action": "stage", "name": "kept" },
            { "at": { "event": "liftoff", "delay": 2 }, "action": "cue", "name": "overlay:full" },
            { "at": { "event": "landed" }, "action": "cue", "name": "never" }
          ]
        }
      ]
    }
    """;

    public static void Run(Action<string, Action> check, Action<bool, string> expect)
    {
        check("integration: a cue action reads, writes back and keeps the stage fields apart", () =>
        {
            ShotFile file = ShotFileJson.Parse(CueFile).File;
            Take take = file.Takes[0];
            expect(take.Actions.Count == 5, "five actions");
            expect(take.Actions[0].Kind == ActionKind.Cue && take.Actions[0].Name == "intro", "a cue at a time");
            expect(take.Actions[1].At.Event == "liftoff" && take.Actions[1].Name == "overlay-in", "a cue at an event");
            expect(take.Actions[2].Kind == ActionKind.Stage && take.Actions[2].Name.Length == 0
                && take.Actions[2].Extras["name"].GetString() == "kept", "a stage action keeps an unknown name field");
            expect(take.Shots[0].Name == "pad-wide", "shot name");
            string written = ShotFileJson.Write(file);
            ShotFile again = ShotFileJson.Parse(written).File;
            expect(again.Takes[0].Actions[3].Name == "overlay:full" && again.Takes[0].Shots[0].Name == "pad-wide", "round trip");
            expect(again.Takes[0].Actions[2].Extras["name"].GetString() == "kept", "the extra survives the round trip");
            expect(ShotFileValidator.Validate(file).IsValid, "valid");
        });

        check("integration: a cue needs a valid name and a shot name is checked", () =>
        {
            expect(Invalid(CueFile.Replace("\"name\": \"intro\"", "\"note\": \"x\"")).Contains("a cue needs a name"), "missing");
            expect(Invalid(CueFile.Replace("\"intro\"", "\"two words\"")).Contains("takes[0].actions[0].name"), "space");
            expect(Invalid(CueFile.Replace("\"intro\"", "\"\"")).Contains("expected 1 to 64"), "empty");
            expect(Invalid(CueFile.Replace("\"intro\"", "\"" + new string('a', 65) + "\"")).Contains("expected"), "too long");
            expect(Invalid(CueFile.Replace("\"pad-wide\"", "\"a;b\"")).Contains("camera[0].name"), "shot name");
            expect(Invalid(CueFile.Replace("\"name\": \"intro\"", "\"name\": \"intro\", \"sequence\": 2")).Contains("only a stage action"), "sequence on a cue");
            expect(TakeAction.IsValidName("Overlay_In.2:a-b") && !TakeAction.IsValidName("ue" + (char)0xE4) && !TakeAction.IsValidName(null), "rule");
        });

        check("integration: the editor helpers keep and show the names", () =>
        {
            Take take = ShotFileJson.Parse(CueFile).File.Takes[0];
            TakeAction copy = ShotEdit.Clone(take.Actions[1]);
            expect(copy.Kind == ActionKind.Cue && copy.Name == "overlay-in", "clone of a cue");
            expect(ShotEdit.Clone(take.Shots[0]).Name == "pad-wide", "clone of a shot");
            expect(ShotEdit.Describe(1, take.Actions[1]).Contains("cue overlay-in"), "describe: " + ShotEdit.Describe(1, take.Actions[1]));
            expect(ViewText.ActionRow(1, take.Actions[1]) is { Name: "cue", Extra: "overlay-in" }, "track row");
        });

        check("integration: a live take announces a cue at its time and at its event", () =>
        {
            ShotFile file = ShotFileJson.Parse(CueFile).File;
            var runner = new TimelineRunner(file.Takes[0], file.Events);
            var none = new Dictionary<string, double>();
            expect(runner.Update(0.0, none).StartedActions.SequenceEqual([0]), "the cue at 0");
            expect(runner.Update(4.0, none).StartedActions.SequenceEqual([2]), "the stage action at 1");
            var seen = new Dictionary<string, double> { ["liftoff"] = 5.0 };
            expect(runner.Update(5.0, seen).StartedActions.SequenceEqual([1]), "the cue at the lift-off");
            expect(runner.Update(7.0, seen).StartedActions.SequenceEqual([3]), "the cue 2 s later");
        });

        check("integration: the replay feed gives each event and cue once, in the order of time", () =>
        {
            ReplayTakePlan plan = Plan();
            List<FeedItem> start = ReplayFeed.Due(plan, 0.0, 0.0, includeFrom: true);
            expect(start.Count == 1 && start[0].Kind == FeedKind.Cue && start[0].Name == "intro", "the cue at 0 from the start");
            expect(ReplayFeed.Due(plan, 0.0, 0.0, includeFrom: false).Count == 0, "nothing at 0 after the start");
            List<FeedItem> lift = ReplayFeed.Due(plan, 2.0, 6.0, includeFrom: false);
            expect(lift.Select(i => $"{i.Kind}:{i.Name}").SequenceEqual(["Event:liftoff", "Cue:overlay-in", "Event:sequence:1", "Cue:overlay:full"]),
                "order: " + string.Join(", ", lift.Select(i => $"{i.Kind}:{i.Name}@{i.TakeSeconds}")));
            expect(lift[1].Index == 1 && Math.Abs(lift[1].TakeSeconds - 3.0) < 1e-9, "the cue has the event time");
            List<FeedItem> split = [.. ReplayFeed.Due(plan, 0.0, 3.0, false), .. ReplayFeed.Due(plan, 3.0, 20.0, false)];
            expect(split.Count(i => i.Kind == FeedKind.Cue) == 2, "a step boundary at the event gives the cue once");
            expect(ReplayFeed.Due(plan, 6.0, 4.0, false).Count == 0, "no item for a move back");
            expect(ReplayFeed.CuesUntil(plan, 4.0).Select(i => i.Name).SequenceEqual(["intro", "overlay-in"]), "passed cues");
            expect(ReplayFeed.CuesUntil(plan, 100.0).All(i => i.Name != "never"), "a cue whose event never comes");
        });

        check("integration: a render writes the cues on the picture that first shows them", () =>
        {
            ReplayTakePlan plan = Plan();
            var job = new ReplayRenderJob(2.0, plan.Duration, 10.0);
            List<ReplayRenderRow> rows = ReplayRenderLog.TimedRows(plan, job);
            ReplayRenderRow cue = rows.Single(r => r.Kind == "cue" && r.Name == "overlay-in");
            expect(cue.Picture == 11 && cue.Detail.StartsWith("actions[1]", StringComparison.Ordinal), $"picture {cue.Picture} detail {cue.Detail}");
            expect(rows.FindIndex(r => r.Name == "liftoff") < rows.IndexOf(cue), "the event before the cue of the same time");
            expect(rows.All(r => r.Name != "intro"), "a cue before the range has no row");
            expect(rows.Where(r => r.Kind == "event").Select(r => r.Name).SequenceEqual(
                ReplayRenderLog.EventRows(plan.EventTimes, job).Select(r => r.Name)), "the same event rows as before");
            List<ReplayRenderRow> fromStart = ReplayRenderLog.TimedRows(plan, new ReplayRenderJob(0.0, plan.Duration, 10.0));
            expect(fromStart.Single(r => r.Name == "intro").Picture == 1, "a cue at the start is on the first picture");
        });

        check("integration: the events reach every handler in order, and a handler that throws is logged once", () =>
        {
            CineCamEvents.Reset();
            var seen = new List<string>();
            var errors = new List<string>();
            CineCamEvents.ErrorLog = errors.Add;
            Action<string, double, double> thrower = (_, _, _) => throw new InvalidOperationException("boom");
            CineCamEvents.Cue += thrower;
            CineCamEvents.Cue += (name, t, _) => seen.Add(FormattableString.Invariant($"cue {name} {t}"));
            CineCamEvents.TakeStarted += (take, mode, hidden) => seen.Add(FormattableString.Invariant($"start {take} {mode} {hidden} {CineCamEvents.TakeSeconds}"));
            CineCamEvents.ShotStarted += (index, type, name) => seen.Add($"shot {index} {type} {name}");
            CineCamEvents.TakeEnded += (take, mode, reason) => seen.Add($"end {take} {mode} {reason} {CineCamEvents.IsTakeRunning}");

            CineCamEvents.RaiseCue("early", 0.0, 0.0);
            CineCamEvents.RaiseTakeStarted("launch", "replay", true, 4.0, ["intro"]);
            expect(CineCamEvents.PassedCues.SequenceEqual(["intro"]) && CineCamEvents.IsTakeRunning, "state at the start");
            CineCamEvents.RaiseShotStarted(0, "tripod", "pad-wide");
            CineCamEvents.RaiseCue("overlay-in", 5.0, 105.0);
            CineCamEvents.RaiseCue("overlay-out", 9.0, 109.0);
            expect(CineCamEvents.ShotType == "tripod" && CineCamEvents.ShotName == "pad-wide", "shot state");
            expect(CineCamEvents.PassedCues.SequenceEqual(["intro", "overlay-in", "overlay-out"]), "passed");
            CineCamEvents.RaiseTakeEnded("the replay paused");
            CineCamEvents.RaiseTakeEnded("twice");

            expect(seen.SequenceEqual(["start launch replay True 4", "shot 0 tripod pad-wide", "cue overlay-in 5", "cue overlay-out 9",
                "end launch replay the replay paused False"]), "events: " + string.Join(" | ", seen));
            expect(errors.Count == 1 && errors[0].Contains("CineCamEvents.Cue") && errors[0].Contains("boom"), "one error line");
            expect(CineCamEvents.PassedCues.Length == 0 && CineCamEvents.ShotIndex == -1 && CineCamEvents.TakeName.Length == 0, "state after the end");
            CineCamEvents.Reset();
        });

        check("integration: the interface event comes only on a change, and the reset drops the handlers", () =>
        {
            CineCamEvents.Reset();
            var seen = new List<bool>();
            CineCamEvents.InterfaceChanged += seen.Add;
            CineCamEvents.SyncInterface(false);
            CineCamEvents.SyncInterface(true);
            CineCamEvents.SyncInterface(true);
            CineCamEvents.SyncInterface(false);
            expect(seen.SequenceEqual([true, false]), "changes only");
            CineCamEvents.RaiseReplayOpened("Flight 1");
            expect(CineCamEvents.IsReplayOpen && CineCamEvents.ReplayName == "Flight 1", "replay state");
            CineCamEvents.Reset();
            CineCamEvents.SyncInterface(true);
            expect(seen.Count == 2 && !CineCamEvents.IsReplayOpen, "no handler after the reset");
            CineCamEvents.Reset();
        });

        check("integration: the reflection pattern of docs/integration.md subscribes without a reference", () =>
        {
            CineCamEvents.Reset();
            Probe.Lines.Clear();
            Type? api = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("CineCam.Api.CineCamEvents", throwOnError: false))
                .FirstOrDefault(type => type != null);
            expect(api != null, "type found");
            FieldInfo? version = api!.GetField("ApiVersion", BindingFlags.Public | BindingFlags.Static);
            expect(version != null && (int)version.GetValue(null)! >= 1, "version");
            bool cue = Subscribe(api, "Cue", nameof(Probe.OnCue));
            bool take = Subscribe(api, "TakeStarted", nameof(Probe.OnTakeStarted));
            bool wrong = Subscribe(api, "Cue", nameof(Probe.OnTakeStarted));
            expect(cue && take && !wrong, "subscribed, and a wrong signature is refused");
            CineCamEvents.RaiseTakeStarted("launch", "live", false, 0.0, []);
            CineCamEvents.RaiseCue("overlay-in", 12.5, 1012.5);
            expect(Probe.Lines.SequenceEqual(["take launch live False", "cue overlay-in 12.5"]), string.Join(" | ", Probe.Lines));
            PropertyInfo? passed = api.GetProperty("PassedCues", BindingFlags.Public | BindingFlags.Static);
            expect(passed?.GetValue(null) is string[] { Length: 1 } names && names[0] == "overlay-in", "state by reflection");
            CineCamEvents.Reset();
        });
    }

    // The same steps as the example in docs/integration.md.
    private static bool Subscribe(Type api, string eventName, string handlerName)
    {
        EventInfo? info = api.GetEvent(eventName, BindingFlags.Public | BindingFlags.Static);
        MethodInfo? handler = typeof(Probe).GetMethod(handlerName, BindingFlags.Public | BindingFlags.Static);
        if (info?.EventHandlerType == null || handler == null)
            return false;
        Delegate? created = Delegate.CreateDelegate(info.EventHandlerType, handler, throwOnBindFailure: false);
        if (created == null)
            return false;
        info.AddEventHandler(null, created);
        return true;
    }

    private static class Probe
    {
        public static readonly List<string> Lines = new();

        public static void OnCue(string name, double takeSeconds, double gameSeconds) =>
            Lines.Add(FormattableString.Invariant($"cue {name} {takeSeconds}"));

        public static void OnTakeStarted(string take, string mode, bool interfaceHidden) =>
            Lines.Add($"take {take} {mode} {interfaceHidden}");
    }

    private static string Invalid(string json)
    {
        try
        {
            ShotFileJson.Parse(json);
        }
        catch (ShotFileException ex)
        {
            return ex.Message;
        }

        return "accepted";
    }

    // Lift-off at 3 s and the first sequence at 5 s of a 20 s recording at 1x.
    private static ReplayTakePlan Plan()
    {
        ShotFile file = ShotFileJson.Parse(CueFile).File;
        Take take = file.Takes[0];
        var events = new List<ReplayEvent> { new(3.0, "liftoff"), new(5.0, "sequence:1") };
        return ReplayTakePlan.Resolve(take, file, events, 20.0);
    }
}
