using System.Globalization;
using CineCam.Director;
using CineCam.Editing;

// The take and render controls: the sound and picture options of a take, the picture grab of the
// capture, the plan of exact picture times and the file name of a still.
static class TakeRenderTests
{
    public static void Run(Action<string, Action> check, Action<bool, string> expect)
    {
        const string full = """
        {
          "capture": { "mode": "offline", "grab": "clean" },
          "takes": [
            {
              "name": "a",
              "sound": { "music": 0, "effects": 0.5, "later": 1 },
              "picture": { "exposure": 2.5, "cloudAmbientLight": true, "cloudShadows": false, "reflections": true },
              "exactApsides": true,
              "camera": [ { "type": "tripod", "position": [1, 2, 3], "lookAt": [0, 0, 0], "duration": 5 } ]
            }
          ]
        }
        """;

        check("The take reads its sound, picture and exact apsides, and the capture its grab", () =>
        {
            ShotFile file = ShotFileJson.Parse(full).File;
            Take take = file.Takes[0];
            expect(file.Capture.Grab == CaptureGrab.Clean, "grab");
            expect(take.Sound is { Music: 0.0, Effects: 0.5 }, "sound");
            expect(take.Sound!.Extras.ContainsKey("later"), "an unknown sound field is kept");
            expect(take.Picture is { Exposure: 2.5, CloudAmbientLight: true, CloudShadows: false, Reflections: true }, "picture");
            expect(take.ExactApsides, "exact apsides");
        });

        check("The new fields survive a write and a read", () =>
        {
            ShotFile file = ShotFileJson.Parse(ShotFileJson.Write(ShotFileJson.Parse(full).File)).File;
            Take take = file.Takes[0];
            expect(file.Capture.Grab == CaptureGrab.Clean, "grab");
            expect(take.Sound is { Music: 0.0, Effects: 0.5 } && take.Sound.Extras.ContainsKey("later"), "sound");
            expect(take.Picture is { Exposure: 2.5, CloudAmbientLight: true, CloudShadows: false, Reflections: true }, "picture");
            expect(take.ExactApsides, "exact apsides");
        });

        check("A file without the new fields reads as before and writes none of them", () =>
        {
            const string old = """{ "camera": [ { "type": "tripod", "position": [1, 2, 3], "lookAt": [0, 0, 0], "duration": 5 } ] }""";
            ShotFile file = ShotFileJson.Parse(old).File;
            Take take = file.DefaultTake;
            expect(take.Sound == null && take.Picture == null && !take.ExactApsides, "defaults");
            expect(file.Capture.Grab == CaptureGrab.Swapchain && file.Capture.IsDefault, "capture default");
            string written = ShotFileJson.Write(file);
            foreach (string key in new[] { "sound", "picture", "exactApsides", "grab", "capture" })
                expect(!written.Contains($"\"{key}\"", StringComparison.Ordinal), $"no {key} in the written file");
        });

        check("A file with one take keeps the sound and picture at the top level", () =>
        {
            const string single = """
            { "sound": { "music": 0.25 }, "picture": { "exposure": 1 },
              "camera": [ { "type": "tripod", "position": [1, 2, 3], "lookAt": [0, 0, 0], "duration": 5 } ] }
            """;
            ShotFile file = ShotFileJson.Parse(single).File;
            expect(file.DefaultTake.Sound?.Music == 0.25 && file.DefaultTake.Picture?.Exposure == 1.0, "read");
            expect(!file.Extras.ContainsKey("sound") && !file.Extras.ContainsKey("picture"), "not kept as unknown fields");
            ShotFile again = ShotFileJson.Parse(ShotFileJson.Write(file)).File;
            expect(again.DefaultTake.Sound?.Music == 0.25 && again.DefaultTake.Picture?.Exposure == 1.0, "round trip");
        });

        check("Bad values of the new fields name their path", () =>
        {
            (string Json, string Path)[] bad =
            [
                ("""{ "sound": { "music": 1.5 }, "camera": [ { "type": "tripod", "duration": 5 } ] }""", "sound.music"),
                ("""{ "sound": { "effects": -0.1 }, "camera": [ { "type": "tripod", "duration": 5 } ] }""", "sound.effects"),
                ("""{ "picture": { "exposure": 0 }, "camera": [ { "type": "tripod", "duration": 5 } ] }""", "picture.exposure"),
                ("""{ "picture": { "cloudShadows": "off" }, "camera": [ { "type": "tripod", "duration": 5 } ] }""", "picture.cloudShadows"),
                ("""{ "exactApsides": 1, "camera": [ { "type": "tripod", "duration": 5 } ] }""", "exactApsides"),
                ("""{ "capture": { "grab": "window" }, "camera": [ { "type": "tripod", "duration": 5 } ] }""", "capture.grab"),
            ];
            foreach (var (json, path) in bad)
            {
                try
                {
                    ShotFileJson.Parse(json);
                    expect(false, $"{path} was accepted");
                }
                catch (ShotFileException ex)
                {
                    expect(ex.Message.Contains(path, StringComparison.Ordinal), $"{path}: {ex.Message}");
                }
            }
        });

        check("A copied take keeps its sound, picture and exact apsides as copies", () =>
        {
            Take source = ShotFileJson.Parse(full).File.Takes[0];
            Take copy = ShotEdit.Clone(source, "b");
            expect(copy.Sound is { Music: 0.0, Effects: 0.5 } && !ReferenceEquals(copy.Sound, source.Sound), "sound");
            expect(copy.Picture is { Exposure: 2.5 } && !ReferenceEquals(copy.Picture, source.Picture), "picture");
            expect(copy.ExactApsides, "exact apsides");
        });

        check("The exact step lands a picture on a target and never takes less than half a step for a far target", () =>
        {
            expect(ExactStep.Next(1.0, 5.0) == null, "a far target keeps the regular step");
            expect(ExactStep.Next(1.0, 2.0) == 1.0, "two steps of one");
            expect(ExactStep.Next(1.0, 1.5) == 0.75, "the way is shared by two pictures");
            expect(ExactStep.Next(1.0, 0.75) == 0.75, "the second picture lands");
            expect(ExactStep.Next(1.0, 1.0) == 1.0, "a target one step ahead lands with the regular step");
            expect(ExactStep.Next(1.0, 0.2) == 0.2, "a target that appears close lands at once");
            expect(ExactStep.Next(1.0, 1e-9) == null, "a target that is reached needs no step");
            expect(ExactStep.Next(0.0, 0.5) == null && ExactStep.Next(double.NaN, 0.5) == null, "no regular step");

            // Walks a capture with a step of 0.4 s toward a target at 3.3 s.
            double now = 0.0;
            var steps = new List<double>();
            while (now < 3.3 - ExactStep.Reached)
            {
                double step = ExactStep.Next(0.4, 3.3 - now) ?? 0.4;
                expect(step >= 0.2 - 1e-12, "no step below half the regular step");
                steps.Add(step);
                now += step;
            }

            expect(Math.Abs(now - 3.3) < 1e-9, string.Create(CultureInfo.InvariantCulture, $"a picture lands on the target, now {now}"));
            expect(steps.Count == 9, $"nine pictures, got {steps.Count}");
        });

        check("The nearest target is the first one ahead", () =>
        {
            expect(ExactStep.Nearest(10.0, [5.0, 12.0, 11.0, double.NaN]) == 11.0, "nearest ahead");
            expect(ExactStep.Nearest(10.0, [10.0, 9.0]) == null, "none ahead");
        });

        check("Sound levels take the quieter share", () =>
        {
            expect(SoundLevels.Of(null).IsFull, "no sound option is full");
            SoundLevels take = SoundLevels.Of(new TakeSound { Music = 0.0, Effects = 0.5 });
            expect(take == new SoundLevels(1.0, 0.0, 0.5) && !take.IsFull, "take levels");
            expect(take.Min(SoundLevels.Muted) == SoundLevels.Muted, "a mute wins");
            expect(SoundLevels.Full.Min(take) == take, "full keeps the take");
            expect(SoundLevels.Of(new TakeSound { Music = 2.0 }).Music == 1.0, "a share is clamped");
        });

        check("A still gets a dated name and the collector moves the new picture to it", () =>
        {
            expect(OfflineOutput.StillName(new DateTime(2026, 11, 17, 12, 5, 1), 4) == "still-20261117-120501-x4.png", "name");
            string root = Path.Combine(Path.GetTempPath(), "cinecam-still-" + Guid.NewGuid().ToString("N"));
            string shots = Path.Combine(root, "screenshots");
            string stills = Path.Combine(root, "stills");
            Directory.CreateDirectory(shots);
            try
            {
                byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0];
                File.WriteAllBytes(Path.Combine(shots, "ksa_old.png"), png);
                var collector = new ScreenshotCollector(shots, stills);
                string target = Path.Combine(stills, "still.png");
                expect(collector.TryCollectAs(target) == CollectResult.Waiting, "an old picture is not taken");
                File.WriteAllBytes(Path.Combine(shots, "ksa_new.png"), png);
                expect(collector.TryCollectAs(target) == CollectResult.Done && File.Exists(target), "the new picture is moved");
                expect(File.Exists(Path.Combine(shots, "ksa_old.png")), "the old picture stays");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        });
    }
}
