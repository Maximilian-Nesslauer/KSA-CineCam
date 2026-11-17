using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Editing;

namespace CineCam.Tests;

internal static class LocalizationTests
{
    public static void Run(Action<string, Action> check, Action<bool, string> expect)
    {
        using Stream enStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CineCam.Localization.en.json")!;
        using Stream deStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CineCam.Localization.de.json")!;
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(enStream)!;
        var german = JsonSerializer.Deserialize<Dictionary<string, string>>(deStream)!;
        UiLanguage previous = UiText.Language;
        string Id(string fallback) => english.Single(p => p.Value == fallback).Key;
        try
        {
            if (Environment.GetEnvironmentVariable("CINECAM_PACKAGE") is { Length: > 0 } package)
            {
                check("locale: the release archive contains both usable language resources", () =>
                {
                    using var archive = System.IO.Compression.ZipFile.OpenRead(package);
                    using Stream dll = archive.GetEntry("CineCam/CineCam.dll")!.Open();
                    using var buffer = new MemoryStream();
                    dll.CopyTo(buffer);
                    Assembly assembly = Assembly.Load(buffer.ToArray());
                    foreach (string language in new[] { "en", "de" })
                    {
                        using Stream stream = assembly.GetManifestResourceStream("CineCam.Localization." + language + ".json")!;
                        var table = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
                        expect(table.Count == english.Count, "packaged table differs: " + language);
                    }
                });
            }
            check("locale: the embedded catalogues have complete compatible entries", () =>
            {
                expect(english.Count > 1200 && english.Keys.Order().SequenceEqual(german.Keys.Order()), "incomplete catalogues");
                foreach (var pair in english)
                    expect(UiText.Compatible(pair.Value, german[pair.Key]), "invalid placeholders: " + pair.Key + " " + pair.Value);
            });
            check("locale: auto follows UI culture with English fallback", () =>
            {
                expect(UiText.Resolve(UiLanguage.Auto, CultureInfo.GetCultureInfo("de-AT")) == "de", "German auto");
                expect(UiText.Resolve(UiLanguage.Auto, CultureInfo.GetCultureInfo("fr-FR")) == "en", "unsupported auto");
                expect(UiText.Resolve(UiLanguage.English, CultureInfo.GetCultureInfo("de-DE")) == "en", "explicit English");
                expect(UiText.Resolve(UiLanguage.German, CultureInfo.InvariantCulture) == "de", "explicit German");
                expect(UiText.Compatible("{{{0,8:F1}}} {1}", "{1} {{{0,8:F1}}}"), "escaped or reordered placeholders");
                expect(!UiText.Compatible("{0,8:F1}", "{0:F1}"), "alignment changed");
            });
            check("locale: absent and malformed translations use the code fallback", () =>
            {
                UiText.Language = UiLanguage.German;
                expect(UiText.Render(UiText.Format("missing", $"Value {42:0.0}")) == "Value 42.0", "missing id");
                foreach (string bad in new[] { "Wert {1}", "Wert {0}", "Wert {0:F3}", "Wert {0:0.0} {0:0.0}", "Wert {" })
                    expect(UiText.Fallback("x", "Value {0:0.0}", new Dictionary<string, string> { ["x"] = bad }) == "Value {0:0.0}", "unsafe template: " + bad);
            });
            check("locale: English stays canonical and formatting translates only at drawing", () =>
            {
                UiText.Language = UiLanguage.German;
                string name = "Ready {0}##name%";
                string message = UiText.Format(Id("Recording {0}."), $"Recording {name}.");
                expect(message == "Recording Ready {0}##name%.", "protocol changed");
                expect(UiText.Render(message) == "Ready {0}##name% wird aufgenommen.", "name or format changed");
                UiText.Language = UiLanguage.English;
                expect(UiText.Render(message) == message, "English fallback changed");
            });
            check("locale: cached messages follow a language change and names stay exact", () =>
            {
                string ready = UiText.MessageText(Id("Ready"), "Ready");
                string userName = new("Ready".AsSpan());
                string line = StatusText.LiveLine(userName, ready, "cue:Ready");
                UiText.Language = UiLanguage.German;
                expect(UiText.Render(line) == "\"Ready\"  |  Bereit  |  cue:Ready", "name or cue translated");
                UiText.Language = UiLanguage.English;
                expect(UiText.Render(line) == "\"Ready\"  |  Ready  |  cue:Ready", "stale language");
            });
            check("locale: controls keep the same identity in both languages", () =>
            {
                string label = UiText.MessageText(Id("STOP##preview"), "STOP##preview");
                UiText.Language = UiLanguage.English;
                string first = UiText.Control(label);
                UiText.Language = UiLanguage.German;
                string second = UiText.Control(label);
                expect(first == "STOP###STOP##preview" && second == "STOPP###STOP##preview", "wrong visible control");
                expect(UiText.Identity(first) == UiText.Identity(second), "control identity changed");
                expect(UiText.Identity(label) != UiText.Identity("STOP##other"), "distinct controls collided");
            });
            check("locale: nested status text and footer retain translations", () =>
            {
                UiText.Language = UiLanguage.German;
                string status = StatusText.TakeState(RunState.Paused, 12.5);
                var footer = StatusText.Footer([new(0, status, true), new(1, StatusText.TakeState(RunState.Idle, null), false)]);
                expect(status == "Paused at 12.5 s", "status protocol changed");
                expect(UiText.Render(footer.Shown) == "Pausiert bei 12.5 s  |  Bereit", "footer lost provenance");
            });
            check("locale: parsed problem text retains safe formatted diagnostics", () =>
            {
                UiText.Language = UiLanguage.German;
                string diagnostic;
                try { ShotFileJson.Parse("{\"takes\":[{\"name\":\"Ready\",\"camera\":[{\"type\":\"chase\",\"duration\":0}]}]}"); throw new Exception("accepted invalid file"); }
                catch (ShotFileException error) { diagnostic = error.Message; }
                string sentence = UiText.Render(ProblemText.Plain(diagnostic, ["Ready"], 0));
                expect(sentence.Contains("Einstellung 1") && sentence.Contains("L\u00e4nge") && sentence.Contains("3600"), sentence);
                expect(!sentence.Contains("must be"), sentence);
                expect(UiText.Render(EventDisplay.Name("ignition:2")) == "Z\u00fcndung 2", "event display not translated");
                expect(UiText.Render("ignition:2") == "ignition:2", "event identifier translated");
            });
            check("locale: clipping measures translated prefixes and keeps literal hash pairs", () =>
            {
                UiText.Language = UiLanguage.German;
                static double Width(ReadOnlySpan<char> span)
                {
                    double width = 0;
                    foreach (char c in span)
                        width += c == 'W' ? 3 : 1;
                    return width;
                }
                string fallback = "STOP##preview";
                string label = UiText.MessageText(Id(fallback), fallback);
                expect(StatusText.Fit(label, 10, Width) == "STOPP##...", "clipped the English label or lost the hash pair");
                expect(StatusText.Fit("Pad##B", 6, Width) == "Pad##B", "full user name did not fit");
                expect(StatusText.Fit("WideEventName", 9, Width) == "Wide...", "did not measure glyph widths");
                expect(StatusText.Fit("WideEventName", 5, Width) == "", "showed a prefix that did not fit");
                expect(StatusText.Fit("X\U0001f680Name", 5, Width) == "X...", "split a surrogate pair");
            });
            check("locale: legacy event aliases keep a literal name and a distinct built-in choice", () =>
            {
                const string json = """{"events":{"maxq":"liftoff","Pad##B":"ignition:2"},"camera":[{"type":"chase","duration":1,"offset":[0,0,1]}]}""";
                ShotFile file = ShotFileJson.Parse(json).File;
                var choices = EventDisplay.Choices(file.Events).ToDictionary(choice => choice.Key, choice => choice.Label);
                UiText.Language = UiLanguage.English;
                expect(choices["maxq"] == "maxq = Lift-off" && choices["maxq:1"] == "Max Q", "legacy alias or built-in was mislabeled");
                expect(EventName.TryResolve("maxq", file.Events, out EventName alias, out _) && alias.Kind == EventKind.Liftoff, "alias changed");
                expect(EventName.TryResolve("maxq:1", file.Events, out EventName builtin, out _) && builtin == new EventName(EventKind.MaxQ, 1), "built-in choice resolves to the wrong event");
                UiText.Language = UiLanguage.German;
                expect(UiText.Render(choices["Pad##B"]) == "Pad##B = Z\u00fcndung 2", "literal alias name did not survive");
                file.Events["maxq:1"] = "ignition";
                expect(EventDisplay.Choices(file.Events).All(choice => choice.Label != "Max Q"), "offered a built-in with both spellings shadowed");
            });
            check("control: select take uses the editor name including spaces and long names", () =>
            {
                string longName = "Pad##B " + new string('x', 520);
                string path = Path.GetTempFileName();
                try
                {
                    File.WriteAllText(path, $$"""
                        {"takes":[
                          {"name":"Ascent","camera":[{"type":"chase","duration":1,"offset":[0,0,1]}]},
                          {"name":" Ascent ","camera":[{"type":"chase","duration":1,"offset":[0,0,1]}]},
                          {"name":"{{longName}}","camera":[{"type":"chase","duration":1,"offset":[0,0,1]}]}
                        ]}
                        """);
                    var editor = new EditSession();
                    expect(editor.Load(path), editor.Message);
                    foreach ((string request, string selected) in new[] { (" ascent ", " Ascent "), ("Ascent", "Ascent"), (longName, longName) })
                    {
                        var command = new ReplayCommand(ReplayOperation.SelectTake, Name: request);
                        string? name = ControlRules.TakeName(command.Name);
                        expect(name == request && name != null && editor.SelectTakeByName(name) && editor.Take?.Name == selected, request);
                    }
                    expect(ControlRules.TakeName(null) == null && ControlRules.TakeName(" \t") == null, "accepted a missing take name");
                }
                finally { File.Delete(path); }
            });
            check("locale: validation keeps the pre-roll field path and translates its message", () =>
            {
                const string json = """
                    {"events":{"Ready##{0}":"liftoff"},"takes":[{"name":"Ascent","preRoll":{"until":"Ready##{0}"},
                    "camera":[{"type":"chase","duration":1,"offset":[0,0,1]}]}]}
                    """;
                UiText.Language = UiLanguage.German;
                ShotFile file = ShotFileJson.Parse(json).File;
                ValidationResult result = ShotFileValidator.Validate(file);
                ProblemLine line = ProblemText.DescribeAll(result, ["Ascent"], 0).Single();
                expect(line.Raw == "Take \"Ascent\": preRoll.until: no shot starts on the event \"Ready##{0}\", so the loop would never end", line.Raw);
                expect(line.Item == ProblemItem.Take && line.TakeIndex == 0, "lost take location");
                string sentence = UiText.Render(line.Sentence);
                expect(sentence.Contains("Keine Einstellung beginnt bei \"Ready##{0}\"") && !sentence.Contains("preRoll.until"), sentence);
            });
            check("locale: reader warnings keep their indexed field and German message", () =>
            {
                const string json = """
                    {"takes":[{"name":"Pad##B","camera":[{"type":"chase","duration":1,"offset":[0,0,1],"fitBy":"height"}]}]}
                    """;
                ShotFileReadResult read = ShotFileJson.Parse(json);
                string raw = read.Warnings.Single();
                UiText.Language = UiLanguage.German;
                ProblemLine line = ProblemText.Describe(raw, false, ["Pad##B"], 0);
                expect(raw == "takes[0].camera[0].fitBy: the fit measure has no effect unless \"fit\" is on", raw);
                expect(line.Item == ProblemItem.Shot && line.ItemIndex == 0, "lost shot location");
                string sentence = UiText.Render(line.Sentence);
                expect(sentence.Contains("Das Einrahmungsma\u00df wirkt nur bei aktivem \"fit\""), sentence);
            });
            check("locale: all enum choices and unknown values are readable", () =>
            {
                UiText.Language = UiLanguage.German;
                expect(UiText.Render(UiText.EnumValue(TriggerKind.After)) == "Danach", "trigger choice");
                expect(UiText.Render(UiText.EnumValue(ShotSubject.Separated)) == "Getrennt", "subject choice");
                expect(UiText.Render(UiText.EnumValue((ShotType)999)) == "999", "unknown enum fallback");
            });
            check("locale: persisted event detail keeps captured protocol values exact", () =>
            {
                UiText.Language = UiLanguage.German;
                string detail = StoredEventText.Describe(EventKind.MaxQ, "peak 20.50 kPa, found 1.2 s later");
                expect(detail == "peak 20.50 kPa, found 1.2 s later", "stored detail changed");
                expect(UiText.Render(detail) == "Maximum 20.50 kPa, 1.2 s sp\u00e4ter erkannt", "stored formatting lost");
                expect(UiText.Render(StoredEventText.Describe(EventKind.ControlLost, "probe core unpowered")) == "Sondenkern ohne Strom", "control reason");
                expect(UiText.Render(StoredEventText.Label("arrival", "low")) == "Am Missionsziel angekommen (low)", "translated a body ID");
                expect(UiText.Render(StoredEventText.Label("eva", "probe core unpowered")) == "EVA (probe core unpowered)", "translated an EVA ID");
                expect(UiText.Render(StoredEventText.Label("life-support-low", "low")).EndsWith("(knapp)"), "life support reason stayed English");
                expect(UiText.Render(StoredEventText.Label("custom", "custom {0}##cue%")) == "custom (custom {0}##cue%)", "unknown detail changed");
            });
            check("locale: persisted explosions translate known causes and keep captured part names", () =>
            {
                UiText.Language = UiLanguage.German;
                string whole = StoredEventText.Label("explosion", "the whole vehicle, ground impact");
                expect(whole == "Explosion (the whole vehicle, ground impact)", "English protocol changed");
                expect(UiText.Render(whole) == "Explosion (das ganze Fahrzeug, Bodenaufprall)", "whole-vehicle details stayed English");
                expect(UiText.Render(StoredEventText.Label("explosion:2", "low, Pad##B, collision")) == "Explosion 2 (low, Pad##B, Kollision)", "part name or cause changed");
                expect(UiText.Render(StoredEventText.Label("explosion", "Pad##B, unknown")) == "Explosion (Pad##B, unknown)", "unknown cause changed");
            });
            check("locale: the ground tracker identifies the new body in German", () =>
            {
                UiText.Language = UiLanguage.German;
                string body = "Pad##B";
                string message = UiText.Format(Id("the vehicle left the sphere of influence for {0}"), $"the vehicle left the sphere of influence for {body}");
                expect(UiText.Render(message) == "das Fahrzeug hat die Einflusssph\u00e4re gewechselt und ist jetzt bei Pad##B", "named the new body as the one left");
            });
            check("locale: the scripted game fixture is valid and keeps user identifiers", () =>
            {
                ShotFile file = ShotFileJson.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "LocalizationShots.json"))).File;
                expect(ShotFileValidator.Validate(file).IsValid, "invalid screenshot fixture");
                UiText.Language = UiLanguage.German;
                expect(UiText.Render(file.DefaultTake.Name) == "Ready", "take name");
                expect(UiText.Render(file.DefaultTake.Shots[0].Name!) == "Ignition", "shot name");
                expect(UiText.Render(file.DefaultTake.Actions[0].Name) == "overlay-in", "cue name");
                expect(file.DefaultTake.Shots[1].At.Event == "ignition", "event identifier");
            });
        }
        finally { UiText.Language = previous; }
    }
}
