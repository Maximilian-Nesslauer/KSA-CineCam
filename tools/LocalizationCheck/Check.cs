using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CineCam.Director;
using CineCam.Localization;
using KSA;
using StarMap.API;

namespace LocalizationCheck;

[StarMapMod]
public sealed class Check
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<string> Pictures = [];
    private static readonly List<object> Evidence = [];
    private static readonly string[] Pages = ["Quick", "Editor", "Editor", "Editor", "Editor", "Editor", "Settings"];
    private static readonly string[] Tabs = ["Shots", "Shots", "Speed", "Actions", "PauseEdit", "TakeSetup", "Shots"];
    private static Type Window => typeof(UiText).Assembly.GetType("CineCam.Features.Ui.CineCamWindow", true)!;
    private static string Output => Environment.GetEnvironmentVariable("CINECAM_LOCALIZATION_EVIDENCE") ?? throw new InvalidOperationException("Missing evidence path");
    private static int _step = -1;
    private static double _next;
    private static string? _pending;
    private static ScreenshotCollector? _collector;
    private static bool _done;

    [StarMapAfterGui]
    public void Draw(double dt)
    {
        if (_done) return;
        try
        {
            if (Clock.Elapsed.TotalSeconds > 180) throw new TimeoutException("The scripted picture check timed out.");
            if (!Program.IsLoaded || Program.ControlledVehicle == null) return;
            if (_pending != null)
            {
                if (_collector!.TryCollect(Pictures.Count + 1, out string? image) == CollectResult.Done)
                {
                    string destination = Path.Combine(Output, _pending);
                    File.Move(image!, destination, true);
                    Pictures.Add(_pending);
                    _pending = null;
                    _next = Clock.Elapsed.TotalSeconds + 1.2;
                    if (_step < Pages.Length * 2) Select();
                }
                return;
            }
            if (Clock.Elapsed.TotalSeconds < _next) return;
            if (_step < 0)
            {
                Directory.CreateDirectory(Output);
                Type settings = typeof(UiText).Assembly.GetType("CineCam.Core.CineCamSettings", true)!;
                settings.GetMethod("SetExperimentalReplay", Flags)!.Invoke(null, [true]);
                object instance = Window.GetProperty("Instance", Flags)!.GetValue(null)!;
                Window.GetMethod("Open", Flags)!.Invoke(instance, null);
                _step = 0;
                Select();
                return;
            }
            if (_step >= Pages.Length * 2)
            {
                Finish(null);
                return;
            }
            string locale = _step < Pages.Length ? "en" : "de";
            int page = _step % Pages.Length;
            string name = $"{locale}-{page + 1:00}-{Pages[page]}-{Tabs[page]}.png";
            string screenshots = Path.Combine(KSA.Constants.DocumentsFolderPath, "exports", "screenshots");
            Directory.CreateDirectory(screenshots);
            _collector = new ScreenshotCollector(screenshots, Path.Combine(Output, "collect"));
            typeof(Program).GetMethod("Screenshot", Flags)!.Invoke(null, [1, "ui"]);
            _pending = name;
            Evidence.Add(new { locale, page = Pages[page], tab = Tabs[page], ready = UiText.Render(UiText.MessageText("text.5fa7aac5375c", "Ready")), game = VersionInfo.Current.VersionString });
            _step++;
        }
        catch (Exception error) { Finish(error.ToString()); }
    }

    private static void Select()
    {
        UiText.Language = _step < Pages.Length ? UiLanguage.English : UiLanguage.German;
        int page = _step % Pages.Length;
        FieldInfo main = Window.GetField("_page", Flags)!;
        main.SetValue(null, Enum.Parse(main.FieldType, Pages[page]));
        FieldInfo tab = Window.GetField("_liveTab", Flags)!;
        tab.SetValue(null, Enum.Parse(tab.FieldType, Tabs[page]));
        _next = Clock.Elapsed.TotalSeconds + 1.2;
    }

    private static void Finish(string? error)
    {
        _done = true;
        Directory.CreateDirectory(Output);
        File.WriteAllText(Path.Combine(Output, "result.json"), JsonSerializer.Serialize(new { passed = error == null && Pictures.Count == Pages.Length * 2, error, pictures = Pictures, evidence = Evidence }, new JsonSerializerOptions { WriteIndented = true }));
        Program.Instance?.RequestExit(error == null ? 0 : 1);
    }
}
