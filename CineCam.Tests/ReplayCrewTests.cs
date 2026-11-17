using CineCam.Director;
using CineCam.Director.Replay;

/// <summary>The recorded kittens of a replay: the file fields, their checks, the slot rule and the animation clock.</summary>
static class ReplayCrewTests
{
    public static void Run(Action<string, Action> check, Action<bool, string> expect)
    {
        static ReplaySample S(double time, double x, ReplayKitten? kitten = null) =>
            new(time, new V3(x, 0, 0), new ReplayRotation(0, 0, 0, 1), [], Kitten: kitten);

        static ReplayFlight Flight(ReplayActor kitten) => new("game", "system", "body", 100,
            [new ReplayActor(0, "vehicle", [new(0, "<vehicle/>", [S(0, 0), S(1, 1), S(2, 2)])]), kitten], []);

        static ReplayActor Kitten(string? character, params ReplaySample[] samples) =>
            new(1, "Jebcat", [new(1, "<pack/>", [.. samples])]) { Neighbor = true, Kitten = character, End = 2 };

        static string TempFile() => Path.Combine(Path.GetTempPath(), "cinecam-crew-" + Guid.NewGuid().ToString("N") + ".ccreplay");

        void ExpectRejected(ReplayFlight flight, string what)
        {
            string path = TempFile();
            try
            {
                bool rejected = false;
                try { ReplayFile.Save(path, flight); } catch (InvalidDataException) { rejected = true; }
                expect(rejected, what);
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        check("replay crew: the animation clock starts at the recording time and never steps back", () =>
        {
            var clock = new KittenClock();
            expect(clock.Advance(12.5) == 12.5, "the first step is the recording time, so the phase is the same in every render");
            expect(Math.Abs(clock.Advance(12.6) - 0.1) < 1e-12, "a frame step");
            expect(clock.Advance(12.6) == 0, "a paused replay gives no step");
            expect(clock.Advance(3) == 0, "a backward seek gives no step");
            expect(Math.Abs(clock.Advance(3.5) - 0.5) < 1e-12, "the animation goes on after a backward seek");
            expect(clock.Advance(double.NaN) == 0 && new KittenClock().Advance(-1) == 0, "no step from a time that is not finite or negative");
        });

        check("replay crew: a kitten on EVA keeps its character and animation state through the file", () =>
        {
            var walking = new ReplayKitten(1, ReplayKittenFlags.Move | ReplayKittenFlags.Sprint, 2.5f, 9.81f, 0, 1);
            var flying = new ReplayKitten(0, ReplayKittenFlags.Rcs, 0, 0, 0, 1) { Motion = [0.1f, -0.2f, 0.3f, 0, 0.01f, 0] };
            ReplayFlight flight = Flight(Kitten("Kitten_Default", S(1, 1, walking), S(1.5, 1.5, flying)));
            string path = TempFile();
            try
            {
                ReplayFile.Save(path, flight);
                ReplayFlight loaded = ReplayFile.Load(path);
                ReplayActor kitten = loaded.Actors[1];
                expect(kitten.Kitten == "Kitten_Default" && kitten.Neighbor && kitten.Name == "Jebcat", "the kitten actor");
                ReplayKitten? first = kitten.Segments[0].Samples[0].Kitten, second = kitten.Segments[0].Samples[1].Kitten;
                expect(first is { Mode: 1, Speed: 2.5f, Gravity: 9.81f, Motion: null } && first.Has(ReplayKittenFlags.Sprint) && !first.Has(ReplayKittenFlags.Rcs),
                    "the walking state");
                expect(second is { Mode: 0, Motion.Length: 6 } && second.Motion![1] == -0.2f && second.Has(ReplayKittenFlags.Rcs), "the MMU state with its motion");
                expect(loaded.Actors[0].Kitten == null && loaded.Actors[0].Segments[0].Samples[0].Kitten == null, "a vehicle has no kitten state");
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        });

        check("replay crew: a recording without kittens writes no kitten fields", () =>
        {
            ReplayFlight flight = Flight(new ReplayActor(1, "stage", [new(1, "<stage/>", [S(1, 5)])]));
            string json = System.Text.Json.JsonSerializer.Serialize(flight);
            expect(!json.Contains("\"Kitten\"", StringComparison.Ordinal), "the fields of an older file stay as they were");
        });

        check("replay crew: the reader rejects a broken kitten state", () =>
        {
            ExpectRejected(Flight(Kitten("Kitten_Default", S(1, 1, new ReplayKitten(1, 0, float.NaN, 0, 0, 1)))), "a speed that is not finite");
            ExpectRejected(Flight(Kitten("Kitten_Default", S(1, 1, new ReplayKitten(300, 0, 0, 0, 0, 1)))), "a mode out of range");
            ExpectRejected(Flight(Kitten("Kitten_Default", S(1, 1, new ReplayKitten(0, 0, 0, 0, 0, 1) { Motion = [1, 2, 3] }))), "a short motion");
            ExpectRejected(Flight(Kitten("Kitten_Default", S(1, 1, new ReplayKitten(0, 0, 0, 0, 0, 1) { Motion = [0, 0, 0, 0, 0, 1e9f] }))), "a motion out of range");
            ExpectRejected(Flight(Kitten(" ", S(1, 1))), "an empty character");
            ExpectRejected(Flight(Kitten(new string('k', 257), S(1, 1))), "a long character");
        });

        check("replay crew: an interpolated pose keeps the kitten state of the sample before it", () =>
        {
            var idle = new ReplayKitten(1, 0, 0, 9.81f, 0, 1);
            var walk = new ReplayKitten(1, ReplayKittenFlags.Move, 1.2f, 9.81f, 0, 1);
            ReplayActor kitten = Kitten("Kitten_Default", S(1, 1, idle), S(2, 2, walk));
            var pose = ReplayTimeline.Sample(kitten, 1.5);
            expect(pose is { } p && ReferenceEquals(p.Sample.Kitten, idle) && Math.Abs(p.Sample.Position.X - 1.5) < 1e-12, "the state of the earlier sample");
            expect(ReplayTimeline.Sample(kitten, 2.0) == null, "a kitten that boarded is not shown");
        });

        check("replay crew: kittens take a slot only while the reserve for the followed vehicle and its stages stays free", () =>
        {
            int reserve = ReplaySampling.FollowReserve + ReplaySampling.StageReserve;
            expect(ReplaySampling.CanAddKitten(1, 0, 0), "a kitten beside the first vehicle");
            expect(ReplaySampling.CanAddKitten(ReplaySampling.ActorLimit - reserve - 1, 0, 0), "the last free slot over the reserve");
            expect(!ReplaySampling.CanAddKitten(ReplaySampling.ActorLimit - reserve, 0, 0), "no slot of the reserve");
            expect(!ReplaySampling.CanAddKitten(ReplaySampling.ActorLimit - reserve - 1, 1, 0), "a split of the same tick counts");
            expect(!ReplaySampling.CanAddKitten(1, 0, ReplaySampling.KittenLimit), "the kitten limit");
        });
    }
}
