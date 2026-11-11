using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CineCam.Director;

/// <summary>
/// The two takes of the in-game self test, as shot file text. The sizes follow the height of the
/// vehicle, so the same plan frames any rocket that stands on a pad. The code uses no game type.
/// </summary>
public static class AutotestPlan
{
    public const string TakeAName = "autotestA";
    public const string TakeBName = "autotestB";

    /// <summary>Length of each shot of take A in seconds.</summary>
    public const double ShotSeconds = 4.0;

    public static readonly string[] ShotNames = { "orbit", "crane", "tripod", "track", "chase", "part" };

    /// <summary>The take time of the sim speed key to the slow speed and of the key back to normal.</summary>
    public const double SlowKeyAt = 6.0;
    public const double NormalKeyAt = 14.0;
    public const double SlowSpeed = 0.5;

    public const double OfflineFps = 15.0;

    /// <summary>The take time at which take B starts the engines and fires the next sequence.</summary>
    public const double IgnitionAt = 7.0;
    public const double NextSequenceAt = 29.0;

    /// <summary>The take time at which the test stops take B when it has not ended by itself.</summary>
    public const double OfflineLimitSeconds = 80.0;

    /// <summary>The delays after the lift-off, in take seconds, of the shots and speed keys of take B.</summary>
    public const double ChaseDelay = 1.0;
    public const double TrackDelay = 11.0;
    public const double NormalSpeedDelay = 3.0;
    public const double FastSpeedDelay = 8.0;

    public static double MiddleOfShot(int index) => index * ShotSeconds + ShotSeconds * 0.5;

    // The camera distance that suits the vehicle.
    private static double Reach(double height) => Math.Max(height * 1.1, 12.0);

    public static string TakeAJson(double vehicleHeight, string partId)
    {
        double h = Math.Max(vehicleHeight, 5.0);
        double r = Reach(h);
        var sb = new StringBuilder();
        sb.Append("{\n  \"fps\": 60,\n  \"hideUi\": false,\n  \"takes\": [\n    {\n      \"name\": ").Append(Quote(TakeAName)).Append(",\n      \"default\": true,\n      \"camera\": [\n");
        string[] shots =
        {
            Obj($"\"type\": \"orbit\", \"target\": \"site\", \"center\": [0, 0, 0], \"radius\": {N(r)}, \"height\": {N(h * 0.4)}, \"startDeg\": 0, \"degPerSec\": 10, \"lookAt\": {Vec(h * 0.5, 0, 0)}, \"fov\": 40", 0),
            Obj($"\"type\": \"crane\", \"target\": \"vehicle\", \"from\": 2, \"to\": {N(h * 0.8)}, \"radius\": {N(r * 0.9)}, \"azimuth\": 200, \"ease\": \"inOut\", \"fov\": 40", 1),
            Obj($"\"type\": \"tripod\", \"target\": \"site\", \"position\": {Vec(3, -r * 1.2, -r * 0.8)}, \"lookAt\": {Vec(h * 0.4, 0, 0)}, \"fov\": 30", 2),
            Obj($"\"type\": \"track\", \"target\": \"site\", \"position\": {Vec(3, -r * 1.5, r)}, \"keepSize\": true, \"fov\": 35", 3),
            Obj($"\"type\": \"chase\", \"target\": \"vehicle\", \"offset\": {Vec(h * 0.35, r * 0.6, 0)}, \"lookAt\": {Vec(h * 0.35, 0, 0)}, \"smoothing\": 0.5, \"fov\": 45, \"transition\": {{ \"type\": \"blend\", \"seconds\": 1.0 }}", 4),
            Obj($"\"type\": \"part\", \"target\": \"vehicle\", \"part\": {Quote(partId)}, \"offset\": {Vec(h * 0.05 + 1.0, r * 0.3, 0)}, \"lookAt\": {Vec(0, 0, 0)}, \"smoothing\": 0.5, \"fov\": 40", 5),
        };
        sb.Append(string.Join(",\n", shots)).Append("\n      ],\n      \"speed\": [\n");
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"        {{ \"at\": {SlowKeyAt}, \"to\": {SlowSpeed}, \"ramp\": 0.5, \"ease\": \"inOut\" }},\n"));
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"        {{ \"at\": {NormalKeyAt}, \"to\": 1, \"ramp\": 0.5, \"ease\": \"inOut\" }}\n"));
        sb.Append("      ]\n    }\n  ]\n}\n");
        return sb.ToString();
    }

    /// <param name="holdEvent">The event that starts the last shot, for example sequence:2.</param>
    public static string TakeBJson(double vehicleHeight, string holdEvent, string captureFolder)
    {
        double h = Math.Max(vehicleHeight, 5.0);
        double r = Reach(h);
        var sb = new StringBuilder();
        sb.Append("{\n  \"fps\": ").Append(N(OfflineFps)).Append(",\n  \"hideUi\": true,\n  \"capture\": { \"mode\": \"offline\", \"fps\": ")
            .Append(N(OfflineFps)).Append(", \"folder\": ").Append(Quote(captureFolder.Replace('\\', '/'))).Append(" },\n");
        sb.Append("  \"takes\": [\n    {\n      \"name\": ").Append(Quote(TakeBName)).Append(",\n      \"default\": true,\n      \"camera\": [\n");
        string[] shots =
        {
            $"        {{ \"type\": \"orbit\", \"target\": \"site\", \"center\": [0, 0, 0], \"radius\": {N(r)}, \"height\": {N(h * 0.4)}, \"startDeg\": 0, \"degPerSec\": 6, \"lookAt\": {Vec(h * 0.5, 0, 0)}, \"duration\": {N(IgnitionAt)}, \"fov\": 40 }}",
            $"        {{ \"type\": \"tripod\", \"target\": \"site\", \"position\": {Vec(3, -r * 1.2, -r * 0.7)}, \"lookAt\": {Vec(h * 0.4, 0, 0)}, \"duration\": 8, \"fov\": 30, \"at\": {{ \"event\": \"ignition\" }} }}",
            $"        {{ \"type\": \"chase\", \"target\": \"vehicle\", \"offset\": {Vec(h * 0.3, r * 0.6, -h * 0.05)}, \"lookAt\": {Vec(h * 0.3, 0, 0)}, \"smoothing\": 0.8, \"duration\": 12, \"fov\": 45, \"transition\": {{ \"type\": \"blend\", \"seconds\": 1.0 }}, \"at\": {{ \"event\": \"liftoff\", \"delay\": {N(ChaseDelay)} }} }}",
            $"        {{ \"type\": \"track\", \"target\": \"site\", \"position\": {Vec(3, -r * 3.0, -r * 2.0)}, \"keepSize\": true, \"duration\": 6, \"fov\": 35, \"transition\": {{ \"type\": \"blend\", \"seconds\": 1.0 }}, \"at\": {{ \"event\": \"liftoff\", \"delay\": {N(TrackDelay)} }} }}",
            $"        {{ \"type\": \"chase\", \"target\": \"vehicle\", \"offset\": {Vec(h * 0.2, r * 0.8, 0)}, \"lookAt\": {Vec(h * 0.2, 0, 0)}, \"smoothing\": 0.5, \"duration\": 5, \"fov\": 40, \"transition\": {{ \"type\": \"blend\", \"seconds\": 0.5 }}, \"at\": {{ \"event\": {Quote(holdEvent)} }} }}",
        };
        sb.Append(string.Join(",\n", shots)).Append("\n      ],\n      \"speed\": [\n");
        sb.Append("        { \"at\": { \"event\": \"ignition\" }, \"to\": 0.25, \"ramp\": 0.5, \"ease\": \"inOut\" },\n");
        sb.Append($"        {{ \"at\": {{ \"event\": \"liftoff\", \"delay\": {N(NormalSpeedDelay)} }}, \"to\": 1, \"ramp\": 2, \"ease\": \"inOut\" }},\n");
        sb.Append($"        {{ \"at\": {{ \"event\": \"liftoff\", \"delay\": {N(FastSpeedDelay)} }}, \"to\": 2, \"ramp\": 2, \"ease\": \"inOut\" }}\n");
        sb.Append("      ]\n    }\n  ]\n}\n");
        return sb.ToString();
    }

    private static string Obj(string fields, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"        {{ {fields}, \"duration\": {ShotSeconds}, \"at\": {index * ShotSeconds} }}");

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Vec(double x, double y, double z) => $"[{N(x)}, {N(y)}, {N(z)}]";

    private static string Quote(string text) => JsonSerializer.Serialize(text);
}
