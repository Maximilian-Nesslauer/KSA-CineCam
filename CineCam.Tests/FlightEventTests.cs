using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Editing;

namespace CineCam.Tests;

/// <summary>The flight state events: control, probe cores, power, life support, crew, mission, EVA, explosions, gimbals and dynamic pressure.</summary>
internal static class FlightEventTests
{
    public static void Run(Action<string, Action> check, Action<bool, string> expect)
    {
        static VehicleSample Flying(bool engine = true) => new(engine, false, 0, 1000.0, 100.0);

        static bool Near(double a, double b, double tolerance) => Math.Abs(a - b) <= tolerance;

        static double Q(double t) => Math.Max(0.0, 30000.0 - 75.0 * (t - 20.0) * (t - 20.0));

        check("flight events: the new names parse, count, display and keep old aliases", () =>
        {
            foreach ((string text, EventKind kind, string canonical) in new[]
            {
                ("control-lost", EventKind.ControlLost, "control-lost"), ("Control-Back:2", EventKind.ControlBack, "control-back:2"),
                ("reboot", EventKind.Reboot, "reboot"), ("probe-failed:1", EventKind.ProbeFailed, "probe-failed"),
                ("power-low", EventKind.PowerLow, "power-low"), ("life-support-low:3", EventKind.LifeSupportLow, "life-support-low:3"),
                ("incapacitated", EventKind.Incapacitated, "incapacitated"), ("arrival", EventKind.Arrival, "arrival"),
                ("eva:2", EventKind.Eva, "eva:2"), ("board", EventKind.Board, "board"), ("explosion", EventKind.Explosion, "explosion"),
                ("gimbal-limit", EventKind.GimbalLimit, "gimbal-limit"), ("MaxQ", EventKind.MaxQ, "maxq"), ("maxq:2", EventKind.MaxQ, "maxq:2"),
            })
            {
                expect(EventName.TryParse(text, out EventName name, out string error), text + ": " + error);
                expect(name.Kind == kind && name.Canonical == canonical, text + " gives " + name.Canonical);
                expect(name.AliasMayShadow, text + " may be an old alias");
                expect(name.IsPredictable == (kind == EventKind.MaxQ) && name.IsFoundLate == (kind == EventKind.MaxQ), text + " predictable");
            }

            expect(EventName.TryParse("q:20000", out EventName q, out _) && q.Kind == EventKind.Q && q.Canonical == "q:20000" && q.IsPredictable, "q");
            expect(!EventName.TryParse("q:0", out _, out _) && !EventName.TryParse("q", out _, out _) && !EventName.TryParse("eva:0", out _, out _), "bad values");
            expect(!EventName.TryParse("liftoff", out EventName liftoff, out _) || !liftoff.AliasMayShadow, "an old event is no alias");
            expect(EventDisplay.Name("maxq:2") == "Max Q 2" && EventDisplay.Name("q:25000") == "Dynamic pressure 25 kPa"
                && EventDisplay.Name("control-lost") == "Control lost" && EventDisplay.Name("eva") == "EVA", "display names");
            expect(!EventName.TryParse("nope", out _, out string unknown) && unknown.Contains("gimbal-limit") && unknown.Contains("q:PASCALS"), "the error lists the new names");

            ShotFile file = ShotFileJson.Parse("""
                { "events": { "maxq": "altitude:11000", "eva": "mark" },
                  "camera": [ { "type": "tripod", "position": [1,2,3], "lookAt": [4,5,6], "duration": 2, "at": { "event": "maxq" } } ] }
                """).File;
            List<EventName> watched = EventDetector.Watched(file.DefaultTake, file.Events);
            expect(watched.Single().Canonical == "altitude:11000", "an alias of an older file keeps its meaning");
            bool rejected = false;
            try { ShotFileJson.Parse("""{ "events": { "liftoff": "mark" }, "camera": [] }"""); }
            catch (ShotFileException) { rejected = true; }
            expect(rejected, "an alias still cannot take an older built-in name");

            ShotFileReadResult negative = ShotFileJson.Parse("""
                { "camera": [ { "type": "tripod", "position": [1,2,3], "lookAt": [4,5,6], "duration": 2, "at": { "event": "maxq", "delay": -3 } },
                              { "type": "tripod", "position": [1,2,3], "lookAt": [4,5,6], "duration": 2, "at": { "event": "q:20000", "delay": -2 } },
                              { "type": "tripod", "position": [1,2,3], "lookAt": [4,5,6], "duration": 2, "at": { "event": "eva", "delay": -2 } } ] }
                """);
            Take take = negative.File.DefaultTake;
            expect(take.Shots[0].At.Delay == -3 && take.Shots[1].At.Delay == -2 && take.Shots[2].At.Delay == 0, "a negative delay works for maxq and q only");
            expect(negative.Warnings.Count == 1 && negative.Warnings[0].Contains("q and maxq"), "one warning for eva");
        });

        check("flight events: control lost and back with the reason, never from the state at the start", () =>
        {
            var detector = new EventDetector([]);
            expect(detector.Update(0.0, Flying() with { Controllable = false, ControlReason = "no crew" }).All(e => !e.Name.StartsWith("control")), "no event at the start");
            var back = detector.Update(1.0, Flying() with { Controllable = true });
            expect(back.Single(e => e.Name.StartsWith("control")).Name == "control-back", "control back");
            var lost = detector.Update(2.0, Flying() with { Controllable = false, ControlReason = "probe core unpowered" });
            expect(lost.Single().Name == "control-lost" && lost.Single().Detail == "probe core unpowered", "control lost with the reason");
            expect(detector.Update(3.0, Flying() with { Controllable = false }).Count == 0, "no repeat");
            expect(detector.Update(4.0, Flying() with { Controllable = true }).Single().Name == "control-back:2", "second return");
            expect(detector.Update(5.0, Flying()).Count == 0 && detector.Update(6.0, Flying() with { Controllable = false }).Count == 0,
                "a vehicle without a control module gives no change");
        });

        check("flight events: probe core reboot and failure count per vehicle", () =>
        {
            var detector = new EventDetector([]);
            detector.Update(0.0, Flying() with { ProbesFailed = 1 });
            expect(detector.Update(1.0, Flying() with { ProbesRebooting = 1 }).Single().Name == "reboot", "reboot");
            expect(detector.Update(2.0, Flying() with { ProbesRebooting = 2 }).Count == 0, "a second core in the same reboot");
            detector.Update(3.0, Flying());
            expect(detector.Update(4.0, Flying() with { ProbesRebooting = 1 }).Single().Name == "reboot:2", "the next reboot");
            var failed = detector.Update(5.0, Flying() with { ProbesFailed = 2 });
            expect(failed.Single().Name == "probe-failed" && failed.Single().Detail.Contains("0 to 2"), "a failure after the start");
            expect(detector.Update(6.0, Flying() with { ProbesFailed = 3, VehicleChanged = true }).Count == 0, "another vehicle object starts its count");
            expect(detector.Update(7.0, Flying() with { ProbesFailed = 4 }).Single().Name == "probe-failed:2", "then counts again");
        });

        check("flight events: power and life support warnings come once per warning and rearm after a clear time", () =>
        {
            var detector = new EventDetector([]);
            detector.Update(0.0, Flying());
            var low = detector.Update(1.0, Flying() with { ProbePower = PowerWarning.Low });
            expect(low.Single().Name == "power-low" && low.Single().Detail == "low", "power low");
            detector.Update(2.0, Flying());
            expect(detector.Update(3.0, Flying() with { ProbePower = PowerWarning.Out }).Count == 0, "a flicker gives no second event");
            detector.Update(4.0, Flying());
            expect(detector.Update(20.0, Flying()).Count == 0, "clear");
            var out2 = detector.Update(21.0, Flying() with { ProbePower = PowerWarning.Out });
            expect(out2.Single().Name == "power-low:2" && out2.Single().Detail == "out of power", "after a clear time it comes again");

            var supply = new EventDetector([]);
            expect(supply.Update(0.0, Flying() with { LifeSupport = SupplyWarning.Low }).All(e => !e.Name.StartsWith("life")), "no event at the start");
            expect(supply.Update(1.0, Flying() with { LifeSupport = SupplyWarning.Critical }).Count == 0, "a warning from the start is not new");
            supply.Update(2.0, Flying());
            expect(supply.Update(5.0, Flying() with { LifeSupport = SupplyWarning.Low }).Count == 0, "not armed before the clear time");
            supply.Update(6.0, Flying());
            var critical = supply.Update(17.0, Flying() with { LifeSupport = SupplyWarning.Critical });
            expect(critical.Single().Name == "life-support-low" && critical.Single().Detail == "critical", "life support low");
        });

        check("flight events: crew, mission arrival, EVA, boarding and explosions", () =>
        {
            var detector = new EventDetector([]);
            detector.Update(0.0, Flying() with { MissionBody = "Luna" });
            expect(detector.Update(1.0, Flying() with { Incapacitated = 1 }).Single().Name == "incapacitated", "incapacitated");
            expect(detector.Update(2.0, Flying() with { Incapacitated = 1 }).Count == 0, "no repeat");
            var arrival = detector.Update(3.0, Flying() with { Incapacitated = 1, MissionArrived = true, MissionBody = "Luna" });
            expect(arrival.Single().Name == "arrival" && arrival.Single().Detail == "Luna", "arrival");
            var crew = detector.Update(4.0, Flying() with { Incapacitated = 1, MissionArrived = true, Evas = 2, Boardings = 1 });
            expect(crew.Select(e => e.Name).SequenceEqual(new[] { "eva", "eva:2", "board" }), "counted EVAs and boarding");
            var boom = detector.Update(5.0, Flying() with { Incapacitated = 1, MissionArrived = true, Explosions = ["Tank, excessive g force"] });
            expect(boom.Single().Name == "explosion" && boom.Single().Detail == "Tank, excessive g force", "explosion with what blew up");
            var gone = detector.Destroyed(6.0, ["the whole vehicle, ground impact"]);
            expect(gone.Single().Name == "explosion:2" && gone.Single().TakeTime == 6.0, "the explosion of a destroyed vehicle");

            var started = new EventDetector([]);
            expect(started.Update(0.0, Flying() with { Incapacitated = 2, MissionArrived = true }).Count(e => e.Name is "arrival" or "incapacitated") == 0,
                "the state at the start gives no event");
        });

        check("flight events: the gimbal limit needs a running engine and rearms below 90 percent", () =>
        {
            expect(Near(GimbalMath.Share(0.1, 0.0, 0.1, 0.1, true), 1.0, 1e-12) && Near(GimbalMath.Share(0.07, 0.07, 0.1, 0.1, true), Math.Sqrt(0.98), 1e-12)
                && Near(GimbalMath.Share(0.07, -0.09, 0.1, 0.1, false), 0.9, 1e-12) && GimbalMath.Share(0.1, 0.1, 0.0, 0.0, true) == 0.0, "share");
            var detector = new EventDetector([]);
            detector.Update(0.0, Flying());
            expect(detector.Update(1.0, Flying(engine: false) with { GimbalLimit = 1.0 }).All(e => e.Name != "gimbal-limit"), "no engine, no event");
            var limit = detector.Update(2.0, Flying() with { GimbalLimit = 0.99 });
            expect(limit.Single(e => e.Name.StartsWith("gimbal")).Name == "gimbal-limit", "at the limit");
            expect(detector.Update(3.0, Flying() with { GimbalLimit = 0.95 }).Count == 0 && detector.Update(4.0, Flying() with { GimbalLimit = 1.0 }).Count == 0, "no rearm above 90 percent");
            detector.Update(5.0, Flying() with { GimbalLimit = 0.5 });
            expect(detector.Update(6.0, Flying() with { GimbalLimit = 1.0 }).Single().Name == "gimbal-limit:2", "rearmed");
        });

        check("flight events: max Q has the time of the peak, counts a reentry and predicts the peak", () =>
        {
            var detector = new EventDetector([new EventName(EventKind.MaxQ, 1.0)]);
            DetectedEvent? found = null;
            double? predictedAt15 = null;
            double foundAt = 0.0;
            for (int i = 0; i <= 600 && found == null; i++)
            {
                double t = i * 0.1;
                var events = detector.Update(t, Flying() with
                {
                    DynamicPressure = Q(t), Stamp = new EventStamp(1000 + t * 0.25, i, 0.25),
                });
                if (Math.Abs(t - 15.0) < 1e-9 && detector.Predictions.TryGetValue("maxq", out double seconds))
                    predictedAt15 = seconds;
                found = events.FirstOrDefault(e => e.Name == "maxq");
                foundAt = t;
            }

            expect(found != null && Near(found.TakeTime, 20.0, 1e-9) && foundAt > 23.0 && foundAt < 24.0, $"peak time {found?.TakeTime} found at {foundAt}");
            expect(found?.Stamp is { GameSeconds: 1005, Frame: 200, Speed: 0.25 }, "the delayed event keeps the peak clock and picture");
            expect(predictedAt15 is double p && Near(p, 5.0, 1.0), "prediction " + predictedAt15);
            expect(!detector.Predictions.ContainsKey("maxq:2"), "no prediction for an unwatched max Q");

            for (int i = 240; i <= 1000; i++)
                expect(detector.Update(i * 0.1, Flying() with { DynamicPressure = Q(i * 0.1) }).Count == 0, "the fall after the peak");
            var second = new List<DetectedEvent>();
            for (int i = 0; i <= 400; i++)
                second.AddRange(detector.Update(101.0 + i * 0.1, Flying() with { DynamicPressure = Q(i * 0.1) * 0.5 }));
            expect(second.Single().Name == "maxq:2" && Near(second[0].TakeTime, 121.0, 1e-9), "a reentry gives the second max Q");

            var late = new EventDetector([]);
            for (int i = 0; i <= 300; i++)
                expect(late.Update(i * 0.1, Flying() with { DynamicPressure = Q(25.0 + i * 0.1) }).All(e => e.Name != "maxq"), "a take after the peak sees no max Q");

            var low = new MaxQTracker();
            bool any = false;
            for (int i = 0; i <= 400; i++)
                any |= low.Update(i * 0.1, Q(i * 0.1) / 40.0, false) != null;
            expect(!any, "a peak below the minimum gives no max Q");

            var ground = new MaxQTracker();
            any = false;
            for (int i = 0; i <= 400; i++)
                any |= ground.Update(i * 0.1, Q(i * 0.1), true) != null;
            expect(!any, "no max Q with ground contact");
        });

        check("flight events: q rises to a pressure and has a prediction", () =>
        {
            var detector = new EventDetector([new EventName(EventKind.Q, 20000.0)]);
            DetectedEvent? found = null;
            bool predicted = false;
            for (int i = 0; i <= 300 && found == null; i++)
            {
                double t = i * 0.1;
                found = detector.Update(t, Flying() with { DynamicPressure = Q(t) }).FirstOrDefault(e => e.Name == "q:20000");
                predicted |= detector.Predictions.ContainsKey("q:20000");
            }

            expect(found != null && found.TakeTime > 8.4 && found.TakeTime < 8.6, "q reached at " + found?.TakeTime);
            expect(predicted, "prediction");
        });

        check("flight events: a shot at max Q starts when the event comes, not at the past peak", () =>
        {
            ShotFile file = ShotFileJson.Parse("""
                { "camera": [ { "type": "tripod", "position": [1,2,3], "lookAt": [4,5,6], "duration": 100 },
                              { "type": "tripod", "position": [1,2,3], "lookAt": [4,5,6], "duration": 5, "at": { "event": "maxq" } },
                              { "type": "tripod", "position": [1,2,3], "lookAt": [4,5,6], "duration": 5, "at": { "event": "maxq", "delay": 10 } } ] }
                """).File;
            var runner = new TimelineRunner(file.DefaultTake, file.Events);
            runner.Update(0.0, new Dictionary<string, double>());
            TimelineState state = runner.Update(23.5, new Dictionary<string, double> { ["maxq"] = 20.0 });
            expect(state.ShotIndex == 1 && state.LocalTime == 0.0, "starts now " + state.LocalTime);
            expect(runner.Starts.Shots[1] == 23.5 && runner.Starts.Shots[2] == 30.0, "the later shot keeps the time of the peak");
        });

        check("flight events: a replay finds q and max Q from the samples, and a recorded max Q wins", () =>
        {
            var samples = new List<ReplaySample>();
            for (int i = 0; i <= 400; i++)
            {
                double t = i * 0.1;
                samples.Add(new ReplaySample(t, new V3(6.4e6, 0, 0), new ReplayRotation(0, 0, 0, 1), [], 0f, 1.0f, new V3(Math.Sqrt(2.0 * Q(t)), 0, 0)));
            }

            var flight = new ReplayFlight("game", "system", "body", 0.0, [new ReplayActor(0, "rocket", [new ReplaySegment(0.0, "<v/>", samples)])], []);
            expect(Near(ReplayEvents.DynamicPressure(samples[150]), Q(15.0), 1e-6), "pressure from density and air velocity");
            expect(ReplayEvents.MaxQ(flight, 1) is double peak && Near(peak, 20.0, 1e-9) && ReplayEvents.MaxQ(flight, 2) == null, "max Q of an older recording");
            var events = ReplayEvents.ForTake(flight, [new EventName(EventKind.MaxQ, 1.0), new EventName(EventKind.Q, 20000.0)], 6.4e6);
            expect(events.Single(e => e.Name == "maxq").Time == 20.0, "maxq for the take");
            expect(events.Single(e => e.Name == "q:20000").Time is double qt && qt > 8.4 && qt < 8.6, "q for the take");

            var recorded = flight with { Events = [new ReplayEvent(19.5, "maxq") { Detail = "peak 30.00 kPa" }] };
            expect(ReplayEvents.ForTake(recorded, [new EventName(EventKind.MaxQ, 1.0)], 6.4e6).Single().Time == 19.5, "the recorded max Q");
        });

        check("flight events: the replay file keeps an event detail and checks its length", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "cinecam-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "events.ccreplay");
            try
            {
                var samples = new List<ReplaySample> { new(0.0, V3.UnitX, new ReplayRotation(0, 0, 0, 1), []), new(5.0, V3.UnitX, new ReplayRotation(0, 0, 0, 1), []) };
                var flight = new ReplayFlight("game", "system", "body", 0.0, [new ReplayActor(0, "rocket", [new ReplaySegment(0.0, "<v/>", samples)])], [])
                {
                    Events = [new ReplayEvent(1.0, "control-lost") { Detail = "probe core unpowered" }, new ReplayEvent(2.0, "explosion"), new ReplayEvent(3.0, "maxq")],
                };
                ReplayFile.Save(path, flight);
                ReplayFlight loaded = ReplayFile.Load(path);
                expect(loaded.Events.Count == 3 && loaded.Events[0].Detail == "probe core unpowered" && loaded.Events[1].Detail == null, "round trip");
                bool rejected = false;
                try { ReplayFile.Save(path, flight with { Events = [new ReplayEvent(1.0, "eva") { Detail = new string('x', ReplayFile.DetailLimit + 1) }] }); }
                catch (InvalidDataException) { rejected = true; }
                expect(rejected, "a detail over the limit");
                expect(EventText.Clip(new string('x', 500), ReplayFile.DetailLimit)!.Length == ReplayFile.DetailLimit && EventText.Clip("  ") == null, "clip");

                List<ReplayRenderRow> rows = ReplayRenderLog.EventRows(new Dictionary<string, double> { ["control-lost"] = 1.0, ["maxq"] = 3.0 },
                    new ReplayRenderJob(0.0, 5.0, 30.0), new Dictionary<string, string> { ["control-lost"] = "probe core unpowered" });
                expect(rows[0].Detail == "takeSeconds=1.000; probe core unpowered" && rows[1].Detail == "takeSeconds=3.000", "render rows carry the detail");
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
                if (Directory.Exists(directory))
                    Directory.Delete(directory);
            }
        });

        check("flight events: the live readout and the reason words", () =>
        {
            expect(EventText.Words("ProbeCoreUnpowered") == "probe core unpowered" && EventText.Words("GroundImpact") == "ground impact", "words");
            string text = ViewText.FlightState(Flying() with
            {
                Controllable = false, ControlReason = "no crew", ProbesFailed = 1, ProbePower = PowerWarning.Low,
                LifeSupport = SupplyWarning.Critical, MissionBody = "Luna", GimbalLimit = 0.5, DynamicPressure = 12345.0,
            });
            expect(text.Contains("control no (no crew)") && text.Contains("1 failed") && text.Contains("probe power low") && text.Contains("life support critical")
                && text.Contains("mission to Luna") && text.Contains("gimbal 50 %") && text.Contains("12.35 kPa"), text);
            expect(ViewText.FlightState(Flying()).Contains("control no control module") && ViewText.FlightState(Flying()).Contains("mission none"), "defaults");
        });
    }
}
