using System.Buffers.Binary;
using CineCam.Director.Replay;
using CineCam.Director.Sound;

namespace CineCam.Tests;

static class SoundThreadingTests
{
    private const int WaitMilliseconds = 5000;

    public static void Run(Action<string, Action> check, Action<bool, string> expect)
    {
        check("sound capture: audio published after the gap check stays after its silence", () =>
        {
            string path = Temp();
            using var start = new ReadGate();
            using var empty = new ReadGate();
            SoundCapture? capture = null;
            try
            {
                capture = new SoundCapture(path, 8000, ringSeconds: 0.01);
                int phase = 0;
                capture.BeforeReadForTest = (read, written) =>
                {
                    if (phase == 0)
                    {
                        phase = 1;
                        start.Wait();
                    }
                    else if (phase == 1 && read == written)
                    {
                        phase = 2;
                        empty.Wait();
                    }
                };
                expect(start.Entered.Wait(WaitMilliseconds), "the consumer stopped before its first read");
                const int beforeFrames = SoundCapture.ChunkFrames * 4;
                const int gapFrames = SoundCapture.ChunkFrames;
                const int afterFrames = 1024;
                float[] before = Samples(beforeFrames, 1);
                capture.Push(before, 1, before.Length, 1);
                capture.Push(Samples(gapFrames, beforeFrames + 1), 1, gapFrames, 2);
                start.Release.Set();
                expect(empty.Entered.Wait(WaitMilliseconds), "the ring drained with the gap still pending");

                // The consumer has checked for a gap. This push publishes both the gap and later audio.
                capture.Push(Samples(afterFrames, beforeFrames + gapFrames + 1), 1, afterFrames, 3);
                empty.Release.Set();
                SoundCaptureResult result = capture.Stop();
                expect(result.Error == null, result.Error ?? "");
                expect(result.Frames == beforeFrames + gapFrames + afterFrames, "all frames keep their place");
                expect(result.DroppedFrames == gapFrames, "exactly one chunk overflowed");
                expect(!start.TimedOut && !empty.TimedOut, "both gates were released by the test");
                AssertSamples(path, result.Frames, frame =>
                    frame < beforeFrames || frame >= beforeFrames + gapFrames ? frame + 1 : 0, expect);
            }
            finally
            {
                start.Release.Set();
                empty.Release.Set();
                Finish(capture);
                File.Delete(path);
            }
        });

        check("sound capture: a timed out stop leaves finalization with the writer", () =>
        {
            string path = Temp();
            using var blocked = new ReadGate();
            var slot = new SoundWriterSlot();
            SoundCapture? capture = null;
            try
            {
                capture = new SoundCapture(path, 8000, ringSeconds: 0.01);
                slot.Attach(capture);
                capture.BeforeReadForTest = (_, _) => blocked.Wait();
                expect(blocked.Entered.Wait(WaitMilliseconds), "the writer stopped before reading");
                const int beforeFrames = SoundCapture.ChunkFrames * 4;
                const int tailFrames = SoundCapture.ChunkFrames * 2;
                capture.Push(Samples(beforeFrames, 1), 1, beforeFrames, 1);
                capture.Push(Samples(tailFrames, beforeFrames + 1), 1, tailFrames, 2);

                SoundCaptureResult pending = slot.Stop(0)!;
                expect(pending.Error == "the sound writer did not end in time", "the first stop reports a timeout");
                expect(slot.Active == null, "the mixer no longer sees the stopped capture");
                expect(QuitBlock() == "sound_pending", "quit is refused while the stopped writer still owns the WAV");
                expect(slot.Stop(0) == null && slot.IsFinalizing, "another stop leaves the pending writer alone");
                bool refused = false;
                try { slot.Attach(capture); }
                catch (InvalidOperationException) { refused = true; }
                expect(refused, "the slot cannot be reused while the file is open");
                expect(!CanOpenExclusively(path), "the live writer still owns its stream after a timeout");
                expect(capture.Stop(0).Error == pending.Error, "a second stop can time out while cleanup is pending");
                capture.Push([999], 1, 1, 3);
                blocked.Release.Set();
                expect(capture.JoinWriterForTest(WaitMilliseconds), "the writer finishes without another stop call");
                expect(CanOpenExclusively(path), "the writer closed the stream on its own");
                expect(QuitBlock() == null, "quit becomes available after the WAV is closed");

                SoundCaptureResult result = capture.Stop();
                expect(result.Error == null && !result.Full, result.Error ?? "the file is not full");
                expect(result.Frames == beforeFrames + tailFrames && result.DroppedFrames == tailFrames, "the pending tail is finalized");
                expect(capture.Stop() == result, "a repeated stop returns the same final result");
                expect(!blocked.TimedOut, "the test released the writer");
                AssertSamples(path, result.Frames, frame => frame < beforeFrames ? frame + 1 : 0, expect);
            }
            finally
            {
                blocked.Release.Set();
                Finish(capture);
                File.Delete(path);
            }

            string? QuitBlock() => ControlRules.QuitBlock(new QuitState(false, false, false, false, false, false)
                { SoundWrite = slot.IsFinalizing })?.Code;
        });

        check("sound capture: a writer failure still closes and finalizes the stream", () =>
        {
            string path = Temp();
            SoundCapture? capture = null;
            try
            {
                capture = new SoundCapture(path, 8000);
                capture.BeforeReadForTest = (_, _) => throw new IOException("test writer failure");
                expect(capture.JoinWriterForTest(WaitMilliseconds), "the failed writer exits");
                expect(CanOpenExclusively(path), "the failed writer closes the file before stop");
                SoundCaptureResult result = capture.Stop();
                expect(result.Error == "test writer failure", "the original failure is retained");
                expect(result.Frames == 0 && result.DroppedFrames == 0, "no samples were written");
                expect(capture.Stop() == result, "failure cleanup is not repeated");
                AssertSamples(path, 0, _ => 0, expect);
            }
            finally
            {
                Finish(capture);
                File.Delete(path);
            }
        });
    }

    private static string Temp() => Path.Combine(Path.GetTempPath(), "cinecam-threading-" + Guid.NewGuid().ToString("N") + ".wav");

    private static float[] Samples(int count, int first)
    {
        var samples = new float[count];
        for (int i = 0; i < count; i++)
            samples[i] = first + i;
        return samples;
    }

    private static void AssertSamples(string path, long frames, Func<int, float> expected, Action<bool, string> expect)
    {
        byte[] bytes = File.ReadAllBytes(path);
        expect(bytes.Length == WavWriter.HeaderBytes + frames * 8, "the file length matches the final frame count");
        expect(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) == bytes.Length - 8, "the final RIFF size");
        expect(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(46)) == frames, "the final fact frame count");
        expect(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(54)) == frames * 8, "the final data size");
        for (int frame = 0; frame < frames; frame++)
        {
            float left = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(WavWriter.HeaderBytes + frame * 8));
            float right = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(WavWriter.HeaderBytes + frame * 8 + 4));
            expect(left == expected(frame) && right == expected(frame), $"frame {frame} holds {left}, {right}, expected {expected(frame)}");
        }
    }

    private static bool CanOpenExclusively(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void Finish(SoundCapture? capture)
    {
        if (capture == null)
            return;
        capture.Stop();
        if (!capture.JoinWriterForTest(WaitMilliseconds))
            throw new InvalidOperationException("The test writer did not exit after release.");
    }

    private sealed class ReadGate : IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new(false);
        internal readonly ManualResetEventSlim Release = new(false);
        internal volatile bool TimedOut;

        internal void Wait()
        {
            Entered.Set();
            if (!Release.Wait(WaitMilliseconds))
                TimedOut = true;
        }

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }
}
