using System.Buffers.Binary;
using System.Diagnostics;
using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Director.Sound;

namespace CineCam.Tests;

/// <summary>The game sound recording of a take and the engine sounds of a replay.</summary>
static class SoundTests
{
    public static void Run(Action<string, Action> check, Action<bool, string> expect)
    {
        static bool Near(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance;

        string Temp(string name) => Path.Combine(Path.GetTempPath(), "cinecam-sound-" + Guid.NewGuid().ToString("N") + "-" + name);

        check("sound ring: writes all or nothing and reads across the wrap", () =>
        {
            var ring = new SoundRing(6);
            expect(ring.Capacity == 8, "the capacity is a power of two");
            expect(ring.TryWrite([1, 2, 3, 4, 5]), "five fit");
            expect(!ring.TryWrite([6, 7, 8, 9]), "four more do not fit");
            Span<float> read = stackalloc float[4];
            expect(ring.ReadInto(read) == 4 && read[0] == 1 && read[3] == 4, "the first four");
            expect(ring.TryWrite([6, 7, 8, 9, 10, 11]), "six fit after the read");
            Span<float> rest = stackalloc float[16];
            int count = ring.ReadInto(rest);
            expect(count == 7 && rest[0] == 5 && rest[6] == 11, $"the rest in order across the wrap ({count})");
            expect(ring.ReadInto(rest) == 0 && ring.Available == 0, "empty");
        });

        check("sound downmix: mono, stereo, 5.1 and an unknown layout", () =>
        {
            Span<float> target = stackalloc float[4];
            SoundDownmix.ToStereo([0.5f, -0.25f], 1, 2, target);
            expect(target[0] == 0.5f && target[1] == 0.5f && target[2] == -0.25f && target[3] == -0.25f, "mono goes to both sides");
            SoundDownmix.ToStereo([0.1f, 0.2f, 0.3f, 0.4f], 2, 2, target);
            expect(target[0] == 0.1f && target[3] == 0.4f, "stereo stays");
            // FL FR C LFE SL SR: the center goes to both sides, the low frequency channel is left out.
            SoundDownmix.ToStereo([1f, 0f, 1f, 5f, 0f, 1f], 6, 1, target);
            expect(Near(target[0], 1 + 0.70710677) && Near(target[1], 0.70710677 * 2), $"5.1 ({target[0]}, {target[1]})");
            SoundDownmix.ToStereo([3f, 3f, 3f], 3, 1, target);
            expect(target[0] == 3f && target[1] == 3f, "an unknown layout is the mean");
        });

        check("sound file: the WAV header and the samples read back", () =>
        {
            string path = Temp("header.wav");
            try
            {
                using (var writer = new WavWriter(path, 48000, 2))
                {
                    writer.Write([0.5f, -0.5f, 0.25f, -1.5f]);
                    writer.WriteSilence(10);
                    expect(writer.Frames == 12, "frames");
                }

                byte[] bytes = File.ReadAllBytes(path);
                expect(bytes.Length == WavWriter.HeaderBytes + 12 * 8, "file size");
                expect(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) == bytes.Length - 8, "RIFF size");
                WavInfo info = WavInfo.Read(path);
                expect(info.SampleRate == 48000 && info.Channels == 2 && info.Frames == 12, "format");
                expect(info.Peak == 1.5f && Near(info.Seconds, 12 / 48000.0), "peak and length");
            }
            finally
            {
                File.Delete(path);
            }
        });

        check("sound capture: the file holds every pushed block, and a pause leaves blocks out", () =>
        {
            string path = Temp("capture.wav");
            try
            {
                var capture = new SoundCapture(path, 48000, flushMilliseconds: 10);
                var block = new float[1024 * 2];
                for (int i = 0; i < block.Length; i++)
                    block[i] = 0.25f;
                long stamp = Stopwatch.GetTimestamp();
                expect(capture.FirstTimestamp == 0, "no block yet");
                // One second of a mixer thread that pushes blocks of 1024 frames.
                var mixer = new Thread(() =>
                {
                    for (int i = 0; i < 47; i++)
                    {
                        capture.Push(block, 2, 1024, stamp + i);
                        if (i == 20)
                            capture.Paused = true;
                        if (i == 30)
                            capture.Paused = false;
                    }
                });
                mixer.Start();
                mixer.Join();
                SoundCaptureResult result = capture.Stop();
                expect(capture.FirstTimestamp == stamp, "the time of the first block");
                expect(result.Error == null && result.DroppedFrames == 0, $"no loss ({result.Error})");
                expect(result.Frames == (47 - 10) * 1024, $"the paused blocks are left out ({result.Frames})");
                WavInfo info = WavInfo.Read(path);
                expect(info.Frames == result.Frames && info.Peak == 0.25f && Near(info.Rms, 0.25, 1e-6), "the samples");
            }
            finally
            {
                File.Delete(path);
            }
        });

        check("sound capture: lost blocks become silence at their place, so the timing holds", () =>
        {
            string path = Temp("gaps.wav");
            try
            {
                var capture = new SoundCapture(path, 8000, ringSeconds: 0.01, flushMilliseconds: 10);
                const int frames = 256;
                const int blocks = 2000;
                var block = new float[frames];
                for (int b = 0; b < blocks; b++)
                {
                    for (int i = 0; i < frames; i++)
                        block[i] = b * frames + i + 1;
                    capture.Push(block, 1, frames, 1);
                }

                SoundCaptureResult result = capture.Stop();
                expect(result.Error == null, result.Error ?? "");
                expect(result.DroppedFrames > 0, "the writer could not keep up, so blocks were lost");
                expect(result.Frames == (long)frames * blocks, $"every frame has a place ({result.Frames})");
                byte[] bytes = File.ReadAllBytes(path);
                long silent = 0;
                for (long f = 0; f < result.Frames; f++)
                {
                    float left = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(WavWriter.HeaderBytes + (int)f * 8));
                    if (left == 0f)
                        silent++;
                    else
                        expect(left == f + 1, $"frame {f} holds {left}");
                }

                expect(silent == result.DroppedFrames, $"the silence is the lost frames ({silent} of {result.DroppedFrames})");
            }
            finally
            {
                File.Delete(path);
            }
        });

        check("replay sound: the pitch follows the replay rate by the rule of the game", () =>
        {
            expect(ReplaySoundMath.SpeedPitch(SoundSpeedRule.All, 1.0) == 1.2f, "All at 1x is 1 + 1/5, as in the game");
            expect(ReplaySoundMath.SpeedPitch(SoundSpeedRule.All, 20.0) == 3f, "capped at 3");
            expect(ReplaySoundMath.SpeedPitch(SoundSpeedRule.All, 0.5) == 0.5f, "slow");
            expect(ReplaySoundMath.SpeedPitch(SoundSpeedRule.All, 0.1) == 0.25f, "not below 0.25");
            expect(ReplaySoundMath.SpeedPitch(SoundSpeedRule.All, 0.0) == 0f, "stopped");
            expect(ReplaySoundMath.SpeedPitch(SoundSpeedRule.OnlyLower, 2.0) == 1f, "OnlyLower keeps fast");
            expect(ReplaySoundMath.SpeedPitch(SoundSpeedRule.OnlyHigher, 0.5) == 1f, "OnlyHigher keeps slow");
            expect(ReplaySoundMath.SpeedPitch(SoundSpeedRule.Ignore, 0.0) == 1f, "Ignore");
            expect(ReplaySoundMath.Mode(true, false, true, 1.0) == ReplaySoundMode.Play, "play");
            expect(ReplaySoundMath.Mode(true, false, false, 0.0) == ReplaySoundMode.Paused, "pause");
            expect(ReplaySoundMath.Mode(true, false, true, 0.0) == ReplaySoundMode.Paused, "a still speed key holds");
            expect(ReplaySoundMath.Mode(true, true, true, 1.0) == ReplaySoundMode.Off, "render");
            expect(ReplaySoundMath.Mode(true, false, true, 12.0) == ReplaySoundMode.Off, "above 10x");
            expect(ReplaySoundMath.Mode(false, false, true, 1.0) == ReplaySoundMode.Off, "setting off");
        });

        static ReplayNozzle Nozzle(float? throttle, float duty = 1f) =>
            new(duty, 3000, 1e5f, 3000, 1.2f, 300, 1e7f, 3500, 1e5f, 1500, V3.UnitX, V3.UnitZ, V3.UnitX, V3.UnitZ, throttle);

        check("replay sound: the throttle is linear between burning samples", () =>
        {
            var identity = new ReplayRotation(0, 0, 0, 1);
            var samples = new List<ReplaySample>
            {
                new(0, V3.Zero, identity, [Nozzle(0.2f)]),
                new(1, V3.Zero, identity, [Nozzle(0.6f)]),
                new(2, V3.Zero, identity, []),
                new(3, V3.Zero, identity, [Nozzle(null)]),
            };
            expect(Near(ReplaySoundMath.Throttle(samples, 0.5, 0), 0.4), "between two burning samples");
            expect(Near(ReplaySoundMath.Throttle(samples, 1.5, 0), 0.6), "holds before a stop");
            expect(ReplaySoundMath.Throttle(samples, 2.5, 0) == 0f, "no engine list");
            expect(ReplaySoundMath.Throttle(samples, 3.5, 0) == 1f, "an older recording burns at full");
            expect(ReplaySoundMath.Throttle(samples, -1.0, 0) == 0.2f, "before the first sample");
            expect(ReplaySoundMath.Throttle(samples, 0.5, 1) == 0f, "a nozzle that is not in the list");
        });

        check("replay file: the throttle is optional and checked", () =>
        {
            string path = Temp("throttle.ccreplay");
            try
            {
                var identity = new ReplayRotation(0, 0, 0, 1);
                ReplayFlight Flight(params ReplayNozzle[] nozzles) => new("game", "system", "body", 100,
                    [new ReplayActor(1, "vehicle", [new ReplaySegment(0, "<a/>", [new ReplaySample(0, V3.Zero, identity, nozzles), new ReplaySample(1, V3.Zero, identity, [])])])], []);
                ReplayFile.Save(path, Flight(Nozzle(0.75f), Nozzle(null)));
                ReplayFlight loaded = ReplayFile.Load(path);
                ReplayNozzle[] read = loaded.Actors[0].Segments[0].Samples[0].Nozzles;
                expect(read[0].Throttle == 0.75f && read[1].Throttle == null, "the values and a missing value");
                bool rejected = false;
                try
                {
                    ReplayFile.Save(path, Flight(Nozzle(float.NaN)));
                }
                catch (InvalidDataException)
                {
                    rejected = true;
                }

                expect(rejected, "a throttle that is not a number is refused");
            }
            finally
            {
                File.Delete(path);
            }
        });
    }
}
