using CineCam.Director;
using CineCam.Director.Replay;

namespace CineCam.Tests;

/// <summary>The frame step of the cloud detail and the status light channels of the replay.</summary>
internal static class ReplayWorldTests
{
    public static void Run(Action<string, Action> check, Action<bool, string> expect)
    {
        check("replay look: the cloud step is the recording time of one frame, never negative, and 0 after a jump", () =>
        {
            var step = new ReplayFrameStep();
            step.Jump(10.0);
            expect(step.Step(1, 10.0) == 0, "the first frame after the start");
            expect(Math.Abs(step.Step(2, 10.5) - 0.5) < 1e-12, "a played frame");
            expect(Math.Abs(step.Step(2, 10.5) - 0.5) < 1e-12, "a second viewport in the same frame gets the same step");
            expect(step.Step(3, 10.5) == 0, "a paused frame");
            expect(step.Step(4, 9.0) == 0, "a step back");
            expect(Math.Abs(step.Step(5, 9.25) - 0.25) < 1e-12, "play goes on from the earlier time");
            step.Jump(100.0);
            expect(step.Step(6, 100.0) == 0, "the frame after a seek");
            expect(new ReplayFrameStep().Step(1, 5.0) == 0, "no time before the first frame");
            expect(new ReplayFrameStep().Step(0, 5.0) == 0, "frame number 0");
        });

        check("replay look: a power status channel stores a key only when the status changes and holds it", () =>
        {
            var writer = new ReplayChannelWriter([ReplayChannelKind.PowerStatus, ReplayChannelKind.Charge]);
            for (int i = 0; i <= 90; i++)
            {
                float status = i < 30 ? 3f : i < 60 ? 2f : 3f;
                float charge = 1f - i * 0.0001f;
                writer.Prepare(i / 30.0, [status, charge]);
                writer.Commit();
            }
            ReplayChannel power = writer.Channels[0], battery = writer.Channels[1];
            expect(power.Times.Count == 3, $"one key per change, {power.Times.Count}");
            expect(power.ValueAt(0.99) == 3f && power.ValueAt(1.0) == 2f && power.ValueAt(1.5) == 2f && power.ValueAt(2.5) == 3f, "a step channel");
            expect(battery.Times.Count is >= 2 and <= 3, $"a slow drain writes few keys, {battery.Times.Count}");
            expect(Math.Abs(battery.ValueAt(3.0) - 0.991f) < 5e-3, "the charge at the end");
        });

        check("replay look: the status kinds quantize to whole numbers and are known status light kinds", () =>
        {
            expect(ReplayChannelKind.Quantize(ReplayChannelKind.PowerStatus, 2.4f) == 2f, "round");
            expect(ReplayChannelKind.Quantize(ReplayChannelKind.PowerStatus, -3f) == 0f, "no negative status");
            expect(ReplayChannelKind.Quantize(ReplayChannelKind.PowerStatus, 1000f) == ReplayChannelKind.PowerStatusLimit, "upper limit");
            expect(ReplayChannelKind.Quantize(ReplayChannelKind.Charge, float.NaN) == 0f, "a battery without capacity");
            expect(ReplayChannelKind.IsStep(ReplayChannelKind.PowerStatus) && !ReplayChannelKind.IsStep(ReplayChannelKind.Charge), "step kinds");
            expect(ReplayChannelKind.IsKnown(ReplayChannelKind.PowerStatus) && ReplayChannelKind.IsKnown(ReplayChannelKind.Charge), "known kinds");
            expect(ReplayChannelKind.IsStatusLight(ReplayChannelKind.PowerStatus) && ReplayChannelKind.IsStatusLight(ReplayChannelKind.Charge)
                && !ReplayChannelKind.IsStatusLight(ReplayChannelKind.Light), "status light kinds");
        });

        check("replay look: status light channels survive a save and a load", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), "cinecam-status-" + Guid.NewGuid().ToString("N") + ".ccreplay");
            try
            {
                var samples = Enumerable.Range(0, 31).Select(i => new ReplaySample(i / 30.0, new V3(600000.0, 0, 0), new ReplayRotation(0, 0, 0, 1), [])).ToList();
                List<ReplayChannel> channels =
                [
                    new(ReplayChannelKind.Light, [0.0], [1f]),
                    new(ReplayChannelKind.PowerStatus, [0.0, 0.5], [1f, 3f]),
                    new(ReplayChannelKind.Charge, [0.0, 1.0], [1f, 0.75f]),
                ];
                var flight = new ReplayFlight("game", "system", "body", 0.0,
                    [new ReplayActor(0, "probe", [new ReplaySegment(0.0, "<probe/>", samples) { Channels = channels, PartKey = 7u }])], []);
                ReplayFile.Save(path, flight);
                ReplaySegment loaded = ReplayFile.Load(path).Actors[0].Segments[0];
                expect(loaded.Channels!.Select(channel => channel.Kind).SequenceEqual(["l", "p", "c"]) && loaded.PartKey == 7u, "kinds and key");
                expect(loaded.Channels![1].ValueAt(0.6) == 3f && Math.Abs(loaded.Channels[2].ValueAt(0.5) - 0.875f) < 1e-6, "values");
            }
            finally
            {
                File.Delete(path);
            }
        });
    }
}
