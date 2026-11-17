using CineCam.Api;
using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Features;
using CineCam.Features.Replay;

int failed = 0;
void Check(string name, Action action)
{
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Expect(bool condition, string text) { if (!condition) throw new Exception(text); }

ReplayScene Scene(double duration, double eventTime, double start)
{
    ApiFeed.Shutdown();
    var flight = new ReplayFlight("game", "system", "body", 1000,
        [new ReplayActor(0, "vehicle", [new ReplaySegment(0, "parts",
            [new ReplaySample(0, new V3(600010, 0, 0), new ReplayRotation(0, 0, 0, 1), []),
             new ReplaySample(duration, new V3(600010, 0, 0), new ReplayRotation(0, 0, 0, 1), [])])])], [])
        { Events = [new ReplayEvent(eventTime, "mark")] };
    var world = new ReplayWorld(flight, 600000, segment => ReplayShape.FromBounds(V3.Zero, new V3(10, 1, 1), null));
    var file = new ShotFile();
    var take = new Take { Name = "test" };
    take.Shots.Add(new Shot { Type = ShotType.Tripod, Duration = duration, Position = new V3(10, 20, 30), LookAt = new V3(1, 0, 0) });
    take.Actions.Add(new TakeAction { Kind = ActionKind.Cue, Name = "overlay-in", At = Trigger.AtTime(eventTime) });
    file.Takes.Add(take);
    var player = new ReplayTakePlayer(world);
    player.Load(take, file, ReplaySite.At(world.Start));
    player.Seek(start);
    player.SetPlaying(true);
    var scene = new ReplayScene(player);
    ReplaySession.Scene = scene;
    ReplaySession.TakeName = take.Name;
    ApiFeed.ReplayOpened("recording");
    return scene;
}

List<string> Observe()
{
    var seen = new List<string>();
    CineCamEvents.TakeStarted += (_, _, _) => seen.Add("start");
    CineCamEvents.FlightEvent += (name, _, _) => seen.Add("event:" + name);
    CineCamEvents.ShotStarted += (index, _, _) => seen.Add("shot:" + index);
    CineCamEvents.Cue += (name, _, _) => seen.Add("cue:" + name);
    CineCamEvents.TakeEnded += (_, _, _) => seen.Add("end");
    CineCamEvents.ReplayClosed += _ => seen.Add("closed");
    return seen;
}

void Tick(ReplayScene scene, double dt)
{
    var player = scene.Player;
    double before = player.Time;
    int jumps = player.Jumps, shot = player.Frame?.ShotIndex ?? -1;
    bool playing = player.IsPlaying;
    player.Advance(dt);
    ApiFeed.ReplayFrame(scene, before, jumps, playing, shot);
}
void Close()
{
    ReplaySession.Scene = null;
    ApiFeed.ReplayClosed();
}

Check("first playback interval retains final events and cues", () =>
{
    var scene = Scene(1, 1, 0.99);
    var seen = Observe();
    Tick(scene, 1);
    Expect(seen.SequenceEqual(["start", "event:mark", "shot:0", "cue:overlay-in", "end"]), string.Join(",", seen));
});
Check("an active starting shot follows the interval flight events", () =>
{
    var scene = Scene(2, 0.75, 0.5);
    var seen = Observe();
    Tick(scene, 0.5);
    Expect(seen.SequenceEqual(["start", "event:mark", "shot:0", "cue:overlay-in"]), string.Join(",", seen));
});
Check("an existing run publishes its end interval once", () =>
{
    var scene = Scene(1, 1, 0.4);
    var seen = Observe();
    Tick(scene, 0.1);
    seen.Clear();
    Tick(scene, 1);
    Expect(seen.SequenceEqual(["event:mark", "cue:overlay-in", "end"]), string.Join(",", seen));
});
Check("closing in TakeStarted does not restart a disposed replay", () =>
{
    var scene = Scene(2, 0.75, 0.5);
    var seen = Observe();
    CineCamEvents.TakeStarted += (_, _, _) => Close();
    Tick(scene, 0.5);
    Expect(seen.SequenceEqual(["start", "end", "closed"]) && !CineCamEvents.IsTakeRunning, string.Join(",", seen));
});
Check("closing in a flight event suppresses stale shot and cue dispatch", () =>
{
    var scene = Scene(2, 0.75, 0.5);
    var seen = Observe();
    CineCamEvents.FlightEvent += (_, _, _) => Close();
    Tick(scene, 0.5);
    Expect(seen.SequenceEqual(["start", "event:mark", "end", "closed"]) && !CineCamEvents.IsTakeRunning, string.Join(",", seen));
});
Check("closing from a jump end prevents an old-scene restart", () =>
{
    var scene = Scene(2, 1.8, 0.2);
    var seen = Observe();
    Tick(scene, 0.1);
    seen.Clear();
    CineCamEvents.TakeEnded += (_, _, _) => Close();
    scene.Player.Seek(0.5);
    Tick(scene, 0.1);
    Expect(seen.SequenceEqual(["end", "closed"]) && !CineCamEvents.IsTakeRunning, string.Join(",", seen));
});
ApiFeed.Shutdown();
Console.WriteLine(failed == 0 ? "All API feed tests passed." : $"{failed} API feed tests failed.");
return failed == 0 ? 0 : 1;
