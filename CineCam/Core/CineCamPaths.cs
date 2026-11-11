using KSA;

namespace CineCam.Core;

// Every path is computed at the time of use from Constants.DocumentsFolderPath, because a
// launcher can move the documents folder before the mods load.
internal static class CineCamPaths
{
    public const string ShotFileVariable = "CINECAM_SHOTS";

    public static string Root => Path.Combine(Constants.DocumentsFolderPath, "CineCam");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string TakesFolder => Path.Combine(Root, "takes");

    public static string RendersFolder => Path.Combine(Root, "renders");

    // The game saves its own screenshots here (ScreenshotCapture.ScreenshotsFolderPath, which is private).
    public static string ScreenshotsFolder => Path.Combine(Constants.DocumentsFolderPath, "exports", "screenshots");

    // The environment variable lets automated tests use their own shot file.
    public static string ShotFile
    {
        get
        {
            string? custom = Environment.GetEnvironmentVariable(ShotFileVariable);
            return string.IsNullOrWhiteSpace(custom) ? Path.Combine(Root, "shots.json") : custom;
        }
    }
}
