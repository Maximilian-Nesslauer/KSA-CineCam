using CineCam.Director;
using CineCam.Director.Replay;

/// <summary>The parachute channels and the explosions of a recording.</summary>
static class ReplayFxTests
{
    public static void Run(Action<string, Action> check, Action<bool, string> expect)
    {
        static ReplayExplosion Explosion(double time, int seed = 7) =>
            new(time, "FireballVolume", "Earth", new V3(6371000, 0, 0), new V3(1, 2, 3), 0.5f, 101325f, seed);

        check("replay fx: the schedule makes the volumes of a forward move once", () =>
        {
            var schedule = new ReplayExplosionSchedule([Explosion(1), Explosion(2), Explosion(2), Explosion(5)]);
            (int from, int to) = schedule.Advance(0, out bool reset);
            expect(reset && from == to, "the first move resets and makes nothing before the first volume");
            (from, to) = schedule.Advance(2, out reset);
            expect(!reset && from == 0 && to == 3, "a forward move makes the volumes up to and at its time");
            (from, to) = schedule.Advance(2, out reset);
            expect(!reset && from == to, "the same time makes nothing again");
            (from, to) = schedule.Advance(4.9, out reset);
            expect(!reset && from == to, "nothing between the volumes");
            (from, to) = schedule.Advance(6, out reset);
            expect(!reset && from == 3 && to == 4, "the last volume");
        });

        check("replay fx: a seek builds the volumes that burn at its time and none after it", () =>
        {
            var schedule = new ReplayExplosionSchedule([Explosion(1), Explosion(130), Explosion(140), Explosion(200)]);
            schedule.Advance(150, out _);
            (int from, int to) = schedule.Advance(135, out bool reset);
            expect(reset && from == 0 && to == 2, "a backward move considers long-lived earlier volumes");
            schedule.Jump();
            (from, to) = schedule.Advance(135, out reset);
            expect(reset && from == 0 && to == 2, "a jump to the same time rebuilds them again");
            (from, to) = schedule.Advance(0.5, out reset);
            expect(reset && from == to, "a seek before every volume shows none");
            schedule.Jump();
            (from, to) = schedule.Advance(250, out reset);
            expect(reset && from == 0 && to == 4, "a forward seek lets the renderer check every template lifetime");
        });

        check("replay fx: the effect log adds only the items up to the last sample, with one marker per moment", () =>
        {
            var flight = new ReplayFlight("game", "system", "Earth", 0, [], []) { Explosions = [], Destructions = [] };
            var log = new ReplayEffectLog();
            log.Add(Explosion(1));
            log.Add(Explosion(3));
            log.Add(new ReplayDestruction(1, "GroundImpact", 1e9f, 100, 101325, 0), "Rocket");
            log.Add(new ReplayDestruction(1, "GroundImpact", 1e9f, 100, 101325, 0, "Tank"), "Rocket");
            long bytes = log.Flush(flight, 2);
            expect(bytes > 0 && flight.Explosions!.Count == 1 && flight.Destructions!.Count == 2, "the items up to the time");
            expect(flight.Markers.Count == 1 && flight.Markers[0].Label == "Explosion: Rocket" && flight.Markers[0].Time == 1, "one marker");
            expect(log.Pending == 1, "the later explosion waits");
            log.Flush(flight, 3);
            expect(flight.Explosions.Count == 2 && log.Pending == 0, "it goes in with the next sample");
        });

        check("replay fx: terminal effects outlive a destroyed actor without reading another pose", () =>
        {
            var actor = new ReplayActor(0, "Rocket", [new ReplaySegment(0, "<parts/>",
                [new ReplaySample(0, V3.Zero, new ReplayRotation(0, 0, 0, 1), []),
                 new ReplaySample(1, V3.Zero, new ReplayRotation(0, 0, 0, 1), [])])]) { End = 1 };
            var flight = new ReplayFlight("game", "system", "Earth", 0, [actor], [])
            {
                EndTime = 190, Explosions = [Explosion(1.1)], Destructions = [],
            };
            expect(ReplayTimeline.Duration(flight) == 190 && ReplayTimeline.Sample(actor, 130) == null,
                "the timeline continues without a frozen vehicle");
            var world = new ReplayWorld(flight, 600000, segment => ReplayShape.FromBounds(V3.Zero, new V3(10, 1, 1), segment.CenterOfMass));
            var player = new ReplayTakePlayer(world);
            player.Seek(130);
            expect(player.Time == 130 && player.Sample != null && world.Motion(130).VelocityCcf == V3.Zero,
                "the player and camera continue through the tail without a drawable actor");
            expect(ReplayWorld.SurfaceVelocityCcf(actor, 0.99) == V3.Zero, "velocity sampling near an exact actor end stays valid");
            string path = Path.Combine(Path.GetTempPath(), "cinecam-tail-" + Guid.NewGuid().ToString("N") + ".ccreplay");
            try
            {
                ReplayFile.Save(path, flight);
                ReplayFlight loaded = ReplayFile.Load(path);
                expect(ReplayTimeline.Duration(loaded) == 190 && loaded.Explosions!.Single().Time == 1.1, "the tail survives a round trip");
                byte[] bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Version = ReplayFile.CurrentVersion, Flight = loaded });
                using var stream = new MemoryStream(bytes);
                expect(RecordingHeaderReader.Read(stream).Duration == 190, "the library lists the effect tail duration");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        });

        check("replay fx: parachute kinds are their own group and the state steps", () =>
        {
            expect(ReplayChannelKind.IsChute(ReplayChannelKind.ChuteFill) && !ReplayChannelKind.IsKnown(ReplayChannelKind.ChuteFill), "groups");
            expect(!ReplayChannelKind.IsChute(ReplayChannelKind.Heat), "a look kind is no parachute kind");
            expect(ReplayChannelKind.IsStep(ReplayChannelKind.ChuteState), "the state steps");
            expect(ReplayChannelKind.Quantize(ReplayChannelKind.ChuteState, 4.6f) == 5f, "the state is a whole number");
            var channel = new ReplayChannel(ReplayChannelKind.ChuteState, [0, 1], [2f, 3f]);
            expect(channel.ValueAt(0.9) == 2f && channel.ValueAt(1) == 3f, "no blend between states");

            var writer = new ReplayChannelWriter([ReplayChannelKind.ChuteState, ReplayChannelKind.ChuteFill, ReplayChannelKind.ChuteDirectionX]);
            writer.Prepare(0, [0f, 0f, 1f]);
            writer.Commit();
            writer.Prepare(1.0 / 30, [2f, 0f, 1.001f]);
            writer.Commit();
            expect(writer.Channels[0].Times.Count == 2 && writer.Channels[2].Times.Count == 1, "a small direction change writes no key");
        });

        check("replay fx: the file keeps parachutes and explosions, and reads a file without them", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "cinecam-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "test.ccreplay");
            try
            {
                var identity = new ReplayRotation(0, 0, 0, 1);
                List<ReplayChannel> chutes = [new(ReplayChannelKind.ChuteState, [0, 1], [0f, 5f]), new(ReplayChannelKind.ChuteFill, [0, 1], [0f, 1f])];
                var segment = new ReplaySegment(0, "<tree/>", [new(0, V3.Zero, identity, []), new(2, V3.Zero, identity, [])])
                {
                    Chutes = chutes,
                    ChuteKey = 42,
                };
                var plain = new ReplayFlight("game", "system", "Earth", 0, [new ReplayActor(0, "Rocket", [segment with { Chutes = null, ChuteKey = null }])], []);
                ReplayFile.Save(path, plain);
                ReplayFlight old = ReplayFile.Load(path);
                expect(old.Explosions == null && old.Destructions == null && old.Actors[0].Segments[0].Chutes == null, "a file without them");

                var flight = new ReplayFlight("game", "system", "Earth", 0, [new ReplayActor(0, "Rocket", [segment])], [])
                {
                    Explosions = [Explosion(1.5), Explosion(0.5, 0)],
                    Destructions = [new ReplayDestruction(0.5, "Collision", 1e9f, 10, 0, 0, "Tank")],
                };
                ReplayFile.Save(path, flight);
                ReplayFlight loaded = ReplayFile.Load(path);
                ReplaySegment read = loaded.Actors[0].Segments[0];
                expect(read.Chutes!.Count == 2 && read.ChuteKey == 42 && read.Chutes[1].ValueAt(0.5) == 0.5f, "parachute channels");
                expect(loaded.Explosions!.Select(item => item.Time).SequenceEqual([0.5, 1.5]) && loaded.Explosions![1].Seed == 7, "explosions in time order");
                expect(loaded.Destructions!.Single().Part == "Tank" && loaded.Destructions![0].ActorId == 0, "destruction");

                bool Rejected(ReplayFlight bad)
                {
                    try { ReplayFile.Save(path, bad); } catch (InvalidDataException) { return true; }
                    return false;
                }
                expect(Rejected(flight with { Explosions = [Explosion(3)] }), "an explosion after the end");
                expect(Rejected(flight with { Explosions = [Explosion(1) with { Position = new V3(double.NaN, 0, 0) }] }), "a position that is not finite");
                expect(Rejected(flight with { Explosions = [Explosion(1) with { Template = "" }] }), "an explosion without template");
                expect(Rejected(flight with { Destructions = [new ReplayDestruction(1, "", 0, 0, 0)] }), "a destruction without cause");
                var lookKind = segment with { Chutes = [new(ReplayChannelKind.Heat, [0], [0f])] };
                expect(Rejected(flight with { Actors = [new ReplayActor(0, "Rocket", [lookKind])] }), "a look kind among the parachutes");
                var chuteKind = segment with { Chutes = null, Channels = [new(ReplayChannelKind.ChuteFill, [0], [0f])] };
                expect(Rejected(flight with { Actors = [new ReplayActor(0, "Rocket", [chuteKind])] }), "a parachute kind among the look channels");
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
                if (Directory.Exists(directory))
                    Directory.Delete(directory);
            }
        });

        check("replay fx: too many parachute channels drop only the parachutes of the segment", () =>
        {
            var identity = new ReplayRotation(0, 0, 0, 1);
            List<ReplayChannel> many = Enumerable.Range(0, ReplayFile.ChannelLimit + 1)
                .Select(_ => new ReplayChannel(ReplayChannelKind.ChuteFill, [0], [0f])).ToList();
            var segment = new ReplaySegment(0, "<tree/>", [new(0, V3.Zero, identity, [])])
            {
                Channels = [new(ReplayChannelKind.Heat, [0], [0f])],
                Chutes = many,
                ChuteKey = 1,
            };
            string directory = Path.Combine(Path.GetTempPath(), "cinecam-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "test.ccreplay");
            try
            {
                ReplayFile.Save(path, new ReplayFlight("game", "system", "Earth", 0, [new ReplayActor(0, "Rocket", [segment])], []));
                ReplaySegment read = ReplayFile.Load(path).Actors[0].Segments[0];
                expect(read.Chutes == null && read.ChuteKey == null && read.Channels!.Count == 1, "the look channels stay");
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
                if (Directory.Exists(directory))
                    Directory.Delete(directory);
            }
        });
    }
}
