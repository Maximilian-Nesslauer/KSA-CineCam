using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Editing;

namespace CineCam.Tests;

internal static class BacklogTests
{
    public static void Run(Action<string, Action> check, Action<bool, string> expect)
    {
        check("timeline layout: a run that a reset ended early keeps the later event times of an older run", () =>
        {
            TimelineLayout.RunLog full = Run(
                "1.000,event,ignition", "3.000,event,liftoff", "40.000,event,altitude:5000", "60.000,event,burnout", "70.000,take,end");
            TimelineLayout.RunLog early = Run("1.200,event,ignition", "3.100,event,liftoff", "12.000,take,end");
            Dictionary<string, double> merged = TimelineLayout.MergeRuns([early, full]);
            expect(merged.Count == 4, "four events: " + string.Join(", ", merged.Keys));
            expect(merged["ignition"] == 1.2 && merged["liftoff"] == 3.1, "the newest run wins for its events");
            expect(merged["altitude:5000"] == 40.0 && merged["burnout"] == 60.0, "the events after the early end stay");
        });

        check("timeline layout: an event that the newer run passed without seeing it is left out", () =>
        {
            TimelineLayout.RunLog older = Run("2.000,event,ignition", "5.000,event,sequence:2", "30.000,event,burnout", "40.000,take,end");
            TimelineLayout.RunLog newer = Run("2.000,event,ignition", "20.000,take,end");
            Dictionary<string, double> merged = TimelineLayout.MergeRuns([newer, older]);
            expect(!merged.ContainsKey("sequence:2"), "the stage did not come in the newer run before its end");
            expect(merged.ContainsKey("burnout"), "the burnout after the end of the newer run stays");
        });

        check("timeline layout: an unfinished run and a run without an end time stop nothing they should not", () =>
        {
            TimelineLayout.RunLog crashed = Run("1.000,event,ignition");
            TimelineLayout.RunLog untimed = Run("1.500,event,ignition", "x,take,end");
            TimelineLayout.RunLog oldest = Run("1.700,event,ignition", "9.000,event,liftoff", "20.000,take,end");
            expect(!crashed.Finished && untimed.Finished && untimed.EndSeconds == null, "the end rows");
            Dictionary<string, double> merged = TimelineLayout.MergeRuns([crashed, untimed, oldest]);
            expect(merged.Count == 1 && merged["ignition"] == 1.5, "a run with no end time is the last one used");
            expect(TimelineLayout.MergeRuns([crashed]).Count == 0, "no finished run gives no times");
        });
    }

    public static void RunProblemText(Action<string, Action> check, Action<bool, string> expect)
    {
        check("problem text: the replay gets the plain sentence of a planner and a reader message", () =>
        {
            string[] names = ["launch", "landing"];
            string shot = ProblemText.Plain("camera[3].duration: the value 0 is out of range (must be greater than 0 and at most 3600)", names, 0);
            expect(shot == "Shot 4: the length must be above 0 s and at most 3600 s.", shot);
            string other = ProblemText.Plain("takes[1].speed[0].to: this field is required", names, 0);
            expect(other == "Take \"landing\", speed key 1: the target is missing.", other);
            string loop = ProblemText.Plain("preRoll.until: no shot starts on the event \"liftoff\", so the loop would never end", names, 0);
            expect(!loop.Contains("preRoll.until", StringComparison.Ordinal) && loop.EndsWith('.'), loop);
            expect(ProblemText.Plain("the starts belong to another take", names, 0) == "The starts belong to another take.", "a text without a path");
        });
    }

    public static void RunFitBy(Action<string, Action> check, Action<bool, string> expect)
    {
        check("fit by height: the field of view frames the length along the nose axis only", () =>
        {
            ShotFile file = ShotFileJson.Parse("""
            { "camera": [
                { "type": "chase", "offset": [-100, 0, 0], "lookAt": [20, 0, 0], "duration": 5, "fit": 0.5, "fitBy": "height", "smoothing": 0 },
                { "type": "chase", "offset": [-100, 0, 0], "lookAt": [20, 0, 0], "duration": 5, "fit": 0.5, "smoothing": 0 } ] }
            """).File;
            expect(file.DefaultTake.Shots[0].FitBy == FitMeasure.Height && file.DefaultTake.Shots[1].FitBy == FitMeasure.Size, "fitBy is read");
            string text = ShotFileJson.Write(file);
            expect(text.Contains("\"fitBy\": \"height\"", StringComparison.Ordinal) && ShotFileJson.Write(ShotFileJson.Parse(text).File) == text,
                "fitBy round trips: " + text);
            expect(text.IndexOf("fitBy", StringComparison.Ordinal) == text.LastIndexOf("fitBy", StringComparison.Ordinal), "the default size is not written");

            var world = new WorldSample
            {
                Vehicle = new VehicleSample(false, true, 0, 0, 0),
                VehicleFrame = new Basis3(V3.Zero, V3.UnitX, V3.UnitY, V3.UnitZ),
                VehicleHeight = 10.0,
                VehicleWidth = 30.0,
            };
            double distance = (new V3(5, 0, 0) - new V3(-100, 0, 0)).Length;
            CameraPose height = new TakeDirector(file.DefaultTake, file).Update(0.0, world).Pose!.Value;
            expect(Math.Abs(height.Fov - PoseMath.FitFov(10.0, 0.0, distance, 0.5)) < 1e-6, "the height alone: " + height.Fov);
            file.DefaultTake.Shots.RemoveAt(0);
            CameraPose size = new TakeDirector(file.DefaultTake, file).Update(0.0, world).Pose!.Value;
            expect(size.Fov > height.Fov + 1.0, $"a wide vehicle gets a wider view by size: {size.Fov} against {height.Fov}");
        });

        check("fit by height: a bad value is an error and a fit measure without a fit is a warning", () =>
        {
            bool rejected = false;
            try
            {
                ShotFileJson.Parse("""{ "camera": [ { "type": "chase", "offset": [1,0,0], "duration": 5, "fit": 0.5, "fitBy": "width" } ] }""");
            }
            catch (ShotFileException ex)
            {
                rejected = ex.FieldPath == "camera[0].fitBy";
            }

            expect(rejected, "an unknown measure");
            ShotFileReadResult result = ShotFileJson.Parse("""{ "camera": [ { "type": "chase", "offset": [1,0,0], "duration": 5, "fitBy": "height" } ] }""");
            expect(result.Warnings.Any(w => w.Contains("fitBy", StringComparison.Ordinal)), "a warning: " + string.Join("; ", result.Warnings));
            Shot copy = ShotEdit.Clone(ShotFileJson.Parse("""{ "camera": [ { "type": "track", "position": [1,0,0], "duration": 5, "fit": 0.4, "fitBy": "height" } ] }""").File.DefaultTake.Shots[0]);
            expect(copy.FitBy == FitMeasure.Height, "a copy of the shot keeps the measure");
        });
    }

    public static void RunPauseAt(Action<string, Action> check, Action<bool, string> expect)
    {
        check("pause at: a take keeps its rehearsal pause time through the file, a copy and the editor", () =>
        {
            const string json = """
            { "takes": [ { "name": "launch", "default": true, "pauseAt": 12.5,
              "camera": [ { "type": "tripod", "position": [1, 2, 3], "lookAt": [4, 5, 6], "duration": 5 } ] } ] }
            """;
            ShotFile file = ShotFileJson.Parse(json).File;
            expect(file.DefaultTake.PauseAt == 12.5, "read: " + file.DefaultTake.PauseAt);
            string text = ShotFileJson.Write(file);
            expect(text.Contains("\"pauseAt\": 12.5", StringComparison.Ordinal) && ShotFileJson.Write(ShotFileJson.Parse(text).File) == text, "round trip: " + text);
            expect(ShotEdit.Clone(file.DefaultTake, "copy").PauseAt == 12.5, "a copy of the take keeps it");
            file.DefaultTake.PauseAt = null;
            expect(!ShotFileJson.Write(file).Contains("pauseAt", StringComparison.Ordinal), "off is not written");

            foreach (string bad in new[] { "0", "-1", "90000", "\"soon\"" })
            {
                bool rejected = false;
                try
                {
                    ShotFileJson.Parse(json.Replace("12.5", bad, StringComparison.Ordinal));
                }
                catch (ShotFileException ex)
                {
                    rejected = ex.FieldPath.EndsWith("pauseAt", StringComparison.Ordinal);
                }

                expect(rejected, "rejected: " + bad);
            }

            string path = Path.Combine(Path.GetTempPath(), "cinecam-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, json);
                var session = new EditSession();
                expect(session.Load(path), session.Message);
                session.SetPauseAt(0.0);
                expect(session.Take!.PauseAt == RehearsalEdit.MinPauseAt && session.Dirty, "the window keeps the time in range");
                session.SetPauseAt(null);
                expect(session.Take!.PauseAt == null, "off");
            }
            finally
            {
                File.Delete(path);
            }
        });

        check("pause at: a run pauses once when it reaches the time, and a preview or a capture never", () =>
        {
            expect(!RehearsalEdit.PauseAtDue(null, 100.0, false, true), "no pause time");
            expect(!RehearsalEdit.PauseAtDue(10.0, 9.99, false, true), "before the time");
            expect(RehearsalEdit.PauseAtDue(10.0, 10.0, false, true), "at the time");
            expect(RehearsalEdit.PauseAtDue(10.0, 10.5, false, true), "a frame after the time");
            expect(!RehearsalEdit.PauseAtDue(10.0, 10.5, true, true), "paused already");
            expect(!RehearsalEdit.PauseAtDue(10.0, 10.5, false, false), "a preview or an offline capture");
        });
    }

    public static void RunControl(Action<string, Action> check, Action<bool, string> expect)
    {
        check("local control: a quit is refused while anything writes files, in the order of the checks", () =>
        {
            expect(ControlRules.QuitBlock(default) == null, "an idle game ends");
            expect(ControlRules.QuitBlock(new QuitState(true, true, true, true, true, true))?.Code == "rendering", "a render first");
            expect(ControlRules.QuitBlock(new QuitState(false, true, false, false, false, false))?.Code == "capture_busy", "the last pictures");
            expect(ControlRules.QuitBlock(new QuitState(false, false, true, true, false, false))?.Code == "recording", "a recording");
            expect(ControlRules.QuitBlock(new QuitState(false, false, false, true, false, false))?.Code == "unsaved_flight", "a flight to save");
            expect(ControlRules.QuitBlock(new QuitState(false, false, false, false, true, false))?.Code == "busy", "a take");
            expect(ControlRules.QuitBlock(new QuitState(false, false, false, false, false, true))?.Code == "busy", "a reset");
        });

        check("local control: the new commands read from the wire form of the client", () =>
        {
            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase, false) },
            };
            ReplayCommand? reload = System.Text.Json.JsonSerializer.Deserialize<ReplayCommand>("""{"operation":"reload","discard":true}""", options);
            ReplayCommand? select = System.Text.Json.JsonSerializer.Deserialize<ReplayCommand>("""{"operation":"selectTake","name":"landing"}""", options);
            ReplayCommand? quit = System.Text.Json.JsonSerializer.Deserialize<ReplayCommand>("""{"operation":"quit"}""", options);
            expect(reload is { Operation: ReplayOperation.Reload, Discard: true }, "reload");
            expect(select is { Operation: ReplayOperation.SelectTake, Name: "landing" }, "select");
            expect(quit is { Operation: ReplayOperation.Quit, Discard: null }, "quit");
        });

        check("local control: a reload reads the file again and keeps the selected take", () =>
        {
            const string json = """
            { "takes": [
              { "name": "launch", "default": true, "camera": [ { "type": "tripod", "position": [1, 2, 3], "lookAt": [4, 5, 6], "duration": 5 } ] },
              { "name": "landing", "camera": [ { "type": "tripod", "position": [1, 2, 3], "lookAt": [4, 5, 6], "duration": 7 } ] } ] }
            """;
            string path = Path.Combine(Path.GetTempPath(), "cinecam-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, json);
                var session = new EditSession();
                expect(session.Load(path) && session.Take!.Name == "launch", "the default take first");
                expect(session.SelectTakeByName("LANDING") && session.Take!.Name == "landing", "a name in any case");
                expect(!session.SelectTakeByName("orbit") && session.Take!.Name == "landing", "an unknown name keeps the take");
                session.Take!.Shots[0].Duration = 9.0;
                session.Changed();
                File.WriteAllText(path, json.Replace("\"duration\": 7", "\"duration\": 8", StringComparison.Ordinal));
                expect(session.Reload() && !session.Dirty, session.Message);
                expect(session.Take!.Name == "landing" && session.Take.Shots[0].Duration == 8.0, "the file wins and the take stays selected");
                File.WriteAllText(path, json.Replace("\"name\": \"landing\"", "\"name\": \"orbit\"", StringComparison.Ordinal));
                expect(session.Reload() && session.Take!.Name == "launch", "a take that is gone gives the default take");
                File.WriteAllText(path, "{ not json");
                expect(!session.Reload() && session.MessageIsError && session.Document!.Takes.Count == 2, "a bad file keeps the document");
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    private static TimelineLayout.RunLog Run(params string[] rows)
    {
        var text = new System.Text.StringBuilder("takeSeconds,gameSeconds,missionSeconds,simSpeed,frame,kind,name,detail\n");
        foreach (string row in rows)
        {
            string[] parts = row.Split(',');
            text.Append(parts[0]).Append(",0,0,1,,").Append(parts[1]).Append(',').Append(parts[2]).Append(",\n");
        }

        return TimelineLayout.ReadRun(new StringReader(text.ToString()));
    }
}
