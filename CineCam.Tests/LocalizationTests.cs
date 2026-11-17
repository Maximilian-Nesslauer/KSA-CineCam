using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CineCam.Director;
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
                string detail = StoredEventText.Describe("peak 20.50 kPa, found 1.2 s later");
                expect(detail == "peak 20.50 kPa, found 1.2 s later", "stored detail changed");
                expect(UiText.Render(detail) == "Maximum 20.50 kPa, 1.2 s sp\u00e4ter erkannt", "stored formatting lost");
                expect(UiText.Render(StoredEventText.Describe("probe core unpowered")) == "Sondenkern ohne Strom", "control reason");
                expect(UiText.Render(StoredEventText.Describe("custom {0}##cue%")) == "custom {0}##cue%", "unknown detail changed");
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
