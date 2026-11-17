using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CineCam.Editing;

public enum ProblemItem { None, Take, Shot, SpeedKey, Action }

/// <summary><see cref="TakeIndex"/> and <see cref="ItemIndex"/> are -1 when the text names none.</summary>
public sealed record ProblemLine(string Sentence, string Raw, bool IsError, int TakeIndex, ProblemItem Item, int ItemIndex);

/// <summary>
/// Turns "takes[1].camera[3].duration: the value 0 is out of range ..." into "Shot 4: the length must be
/// above 0 s and at most 3600 s.". A text that does not have the form of a reader message stays as it is.
/// </summary>
public static class ProblemText
{
    private static readonly Regex TakeName = new("^Take \"(?<name>[^\"]*)\": ", RegexOptions.CultureInvariant);
    private static readonly Regex TakeIndex = new(@"^takes\[(?<i>\d+)\]\.?", RegexOptions.CultureInvariant);
    private static readonly Regex Indexed = new(@"^(?<name>[A-Za-z]+)\[(?<i>\d+)\]$", RegexOptions.CultureInvariant);
    private static readonly Regex OutOfRange = new(
        @"^the value (?<value>\S+) is out of range \(must be (?<kind>greater than|at least) (?<min>\S+) and at most (?<max>[^)\s]+)\)$",
        RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, string> FieldWords = new(StringComparer.Ordinal)
    {
        ["type"] = UiText.MessageText("problem.field.type", "type"),
        ["frame"] = UiText.MessageText("problem.field.frame", "frame"),
        ["up"] = UiText.MessageText("problem.field.up", "up direction"),
        ["subject"] = UiText.MessageText("problem.field.subject", "followed vehicle"),
        ["pick"] = UiText.MessageText("problem.field.pick", "vehicle selection"),
        ["aim"] = UiText.MessageText("problem.field.aim", "aim"),
        ["position"] = UiText.MessageText("problem.field.position", "position"),
        ["offset"] = UiText.MessageText("problem.field.offset", "offset"),
        ["center"] = UiText.MessageText("problem.field.center", "center"),
        ["radius"] = UiText.MessageText("problem.field.radius", "radius"),
        ["height"] = UiText.MessageText("problem.field.height", "height"),
        ["from"] = UiText.MessageText("problem.field.from", "start height"),
        ["azimuth"] = UiText.MessageText("problem.field.azimuth", "azimuth"),
        ["roll"] = UiText.MessageText("problem.field.roll", "roll"),
        ["smoothing"] = UiText.MessageText("problem.field.smoothing", "smoothing"),
        ["ease"] = UiText.MessageText("problem.field.ease", "ease"),
        ["part"] = UiText.MessageText("problem.field.part", "part"),
        ["kind"] = UiText.MessageText("problem.field.kind", "action type"),
        ["music"] = UiText.MessageText("problem.field.music", "music"),
        ["effects"] = UiText.MessageText("problem.field.effects", "sound effects"),
        ["exposure"] = UiText.MessageText("problem.field.exposure", "exposure"),
        ["cloudAmbient"] = UiText.MessageText("problem.field.cloudAmbient", "cloud ambient light"),
        ["cloudShadows"] = UiText.MessageText("problem.field.cloudShadows", "cloud shadows"),
        ["reflections"] = UiText.MessageText("problem.field.reflections", "reflections"),
        ["grab"] = UiText.MessageText("problem.field.grab", "picture source"),
        ["mode"] = UiText.MessageText("problem.field.mode", "capture mode"),
        ["scale"] = UiText.MessageText("problem.field.scale", "scale"),
        ["folder"] = UiText.MessageText("problem.field.folder", "output folder"),
        ["hideUi"] = UiText.MessageText("problem.field.hideUi", "hide interface"),
        ["min"] = UiText.MessageText("problem.field.min", "minimum corner"),
        ["max"] = UiText.MessageText("problem.field.max", "maximum corner"),
        ["body"] = UiText.MessageText("problem.field.body", "body"),
        ["latitude"] = UiText.MessageText("problem.field.latitude", "latitude"),
        ["longitude"] = UiText.MessageText("problem.field.longitude", "longitude"),
        ["altitude"] = UiText.MessageText("problem.field.altitude", "altitude"),
        ["anchor"] = UiText.MessageText("problem.field.anchor", "anchor"),
        ["site"] = UiText.MessageText("problem.field.site", "site"),
        ["default"] = UiText.MessageText("problem.field.default", "default take"),
        ["capture"] = UiText.MessageText("problem.field.capture", "capture"),
        ["look"] = UiText.MessageText("problem.field.look", "picture settings"),
        ["sound"] = UiText.MessageText("problem.field.sound", "sound"),
        ["exactApsides"] = UiText.MessageText("problem.field.exactApsides", "exact apsides"),

        ["duration"] = UiText.MessageText("text.0f82aca66af9", "length"),
        ["at"] = UiText.MessageText("text.cced28c6dc3f", "start"),
        ["event"] = UiText.MessageText("text.f2048544f2a9", "start event"),
        ["time"] = UiText.MessageText("text.59dd27a0dded", "start time"),
        [UiText.MessageText("text.6e16db102267", "delay")] = UiText.MessageText("text.6e16db102267", "delay"),
        ["transition"] = UiText.MessageText("text.727c1e414b34", "blend"),
        ["seconds"] = UiText.MessageText("text.53ba966b3549", "blend length"),
        [UiText.MessageText("text.f7d9f5a90479", "ramp")] = UiText.MessageText("text.f7d9f5a90479", "ramp"),
        ["to"] = UiText.MessageText("text.34a04005bcaf", "target"),
        ["fov"] = UiText.MessageText("text.9388e97798cb", "field of view"),
        [UiText.MessageText("text.da4270e3735a", "fit")] = UiText.MessageText("text.da4270e3735a", "fit"),
        ["fitBy"] = UiText.MessageText("text.5adcab33b52a", "fit measure"),
        ["avoidSun"] = UiText.MessageText("text.178365d268da", "sun angle"),
        ["aimOffset"] = UiText.MessageText("text.b078aa3a5f0a", "aim offset"),
        ["lookAt"] = UiText.MessageText("text.a037c3e141eb", "look point"),
        ["startDeg"] = UiText.MessageText("text.889cee3c26eb", "start angle"),
        ["degPerSec"] = UiText.MessageText("text.4d028be14fa5", "turn speed"),
        ["keepOut"] = UiText.MessageText("text.921b98371d1f", "keep-out boxes"),
        ["keepSize"] = UiText.MessageText("text.caa41a9ab2cd", "keep size"),
        ["preRoll"] = UiText.MessageText("text.3f535ac3d4cf", "pre-roll"),
        ["until"] = UiText.MessageText("text.da63b54bd2b9", "end event"),
        [UiText.MessageText("text.254637f72efc", "loop")] = UiText.MessageText("text.254637f72efc", "loop"),
        ["autoStartDelay"] = UiText.MessageText("text.f23feabd21ca", "start delay"),
        ["pauseAt"] = UiText.MessageText("text.dc81593392c8", "pause time"),
        ["hideUiAt"] = UiText.MessageText("text.94c14430304f", "hide event"),
        ["syncFlash"] = UiText.MessageText("text.18a477196ee6", "white frame"),
        ["fps"] = UiText.MessageText("text.7a76e3ef2445", "frames per second"),
        ["points"] = UiText.MessageText("problem.field.points", "path"),
        ["t"] = UiText.MessageText("text.7d3453f1d82f", "point share"),
        [UiText.MessageText("text.bf37c7c20871", "sequence")] = UiText.MessageText("text.bf37c7c20871", "sequence"),
        [UiText.MessageText("text.47320987f9a4", "checkpoint")] = UiText.MessageText("text.47320987f9a4", "checkpoint"),
        [UiText.MessageText("text.82a3537ff0db", "name")] = UiText.MessageText("text.82a3537ff0db", "name"),
        ["camera"] = UiText.MessageText("text.77d5d30555ba", "camera track"),
        ["speed"] = UiText.MessageText("text.077521466f18", "speed track"),
        ["actions"] = UiText.MessageText("text.06ae403e5d81", "action track"),
    };

    private static readonly HashSet<string> SecondFields = new(StringComparer.Ordinal)
    {
        "duration", "time", UiText.MessageText("text.6e16db102267", "delay"), "seconds", UiText.MessageText("text.f7d9f5a90479", "ramp"), "autoStartDelay", "smoothing", "pauseAt",
    };

    private static readonly Dictionary<string, string> FieldUnits = new(StringComparer.Ordinal)
    {
        ["fov"] = " deg",
        ["startDeg"] = " deg",
        ["azimuth"] = " deg",
        ["avoidSun"] = " deg",
        ["roll"] = " deg",
        ["degPerSec"] = " deg/s",
        ["radius"] = " m",
        ["height"] = " m",
        ["from"] = " m",
        ["altitude"] = " m",
    };

    /// <param name="selectedTake">The take that the editor shows, whose name is left out of the sentence.</param>
    public static ProblemLine Describe(string raw, bool isError, IReadOnlyList<string> takeNames, int selectedTake)
    {
        string rest = raw ?? "";
        int take = -1;
        Match named = TakeName.Match(rest);
        if (named.Success)
        {
            take = IndexOf(takeNames, named.Groups[UiText.MessageText("text.82a3537ff0db", "name")].Value);
            rest = UiText.WithoutPrefix(rest, named.Length);
        }

        Match indexed = TakeIndex.Match(rest);
        if (indexed.Success && int.TryParse(indexed.Groups["i"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int i))
        {
            take = i;
            rest = UiText.WithoutPrefix(rest, indexed.Length);
            if (rest.StartsWith(": ", StringComparison.Ordinal))
                rest = UiText.WithoutPrefix(rest, 2);
        }

        if (!SplitPath(rest, out string path, out string message))
        {
            string text = message.Length == 0 ? raw ?? "" : message;
            string head = take >= 0 ? TakeLabel(take, takeNames, selectedTake, alone: true) : "";
            return new ProblemLine(Sentence(head, text), raw ?? "", isError, take, take >= 0 ? ProblemItem.Take : ProblemItem.None, -1);
        }

        string[] segments = path.Split('.');
        ProblemItem item = take >= 0 ? ProblemItem.Take : ProblemItem.None;
        int itemIndex = -1;
        int first = 0;
        Match itemMatch = Indexed.Match(segments[0]);
        if (itemMatch.Success && int.TryParse(itemMatch.Groups["i"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
        {
            ProblemItem kind = itemMatch.Groups[UiText.MessageText("text.82a3537ff0db", "name")].Value switch
            {
                "camera" => ProblemItem.Shot,
                "speed" => ProblemItem.SpeedKey,
                "actions" => ProblemItem.Action,
                _ => ProblemItem.None,
            };
            if (kind != ProblemItem.None)
            {
                item = kind;
                itemIndex = n;
                first = 1;
            }
        }

        string field = FieldText(segments, first, out string key);
        string itemLabel = item switch
        {
            ProblemItem.Shot => UiText.Format("text.393c3e6752d4", $"shot {itemIndex + 1}"),
            ProblemItem.SpeedKey => UiText.Format("text.aae56731752a", $"speed key {itemIndex + 1}"),
            ProblemItem.Action => UiText.Format("text.7b05fad10713", $"action {itemIndex + 1}"),
            _ => "",
        };
        string prefix = take >= 0 ? TakeLabel(take, takeNames, selectedTake, alone: itemLabel.Length == 0) : "";
        if (itemLabel.Length > 0)
            prefix = prefix.Length == 0 ? Capital(itemLabel) : UiText.Concat(UiText.Concat(prefix, ", "), itemLabel);

        string body = Body(field, key, message);
        return new ProblemLine(Sentence(prefix, body), raw ?? "", isError, take, item, itemIndex);
    }

    /// <summary>The plain sentence of a reader or planner message, for a place that shows one line and no list.</summary>
    public static string Plain(string raw, IReadOnlyList<string> takeNames, int selectedTake) =>
        Describe(raw, true, takeNames, selectedTake).Sentence;

    public static List<ProblemLine> DescribeAll(ValidationResult result, IReadOnlyList<string> takeNames, int selectedTake)
    {
        var lines = new List<ProblemLine>(result.Errors.Count + result.Warnings.Count);
        foreach (string error in result.Errors)
            lines.Add(Describe(error, true, takeNames, selectedTake));
        foreach (string warning in result.Warnings)
            lines.Add(Describe(warning, false, takeNames, selectedTake));
        return lines;
    }

    // A text whose part before the first ": " has a space or a quote is no path.
    private static bool SplitPath(string text, out string path, out string message)
    {
        int colon = text.IndexOf(": ", StringComparison.Ordinal);
        if (colon <= 0 || text.AsSpan(0, colon).IndexOfAny(' ', '"') >= 0)
        {
            path = "";
            message = text;
            return false;
        }

        path = text[..colon];
        message = UiText.WithoutPrefix(text, colon + 2);
        return true;
    }

    // Gives "point 3, point share" for points[2].t. The key is the last named field, which picks the unit.
    private static string FieldText(string[] segments, int first, out string key)
    {
        var words = new List<string>();
        key = "";
        for (int s = first; s < segments.Length; s++)
        {
            string segment = segments[s];
            Match m = Indexed.Match(segment);
            if (m.Success && int.TryParse(m.Groups["i"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                string name = m.Groups[UiText.MessageText("text.82a3537ff0db", "name")].Value;
                key = name;
                words.Add(name == "points" ? UiText.Format("text.339e3e7f9fe8", $"point {index + 1}") : UiText.Format("", $"{Words(name)} {index + 1}"));
                continue;
            }

            // A trigger field reads better as "start event" than as "start, start event".
            if (segment == "at" && s + 1 < segments.Length)
                continue;
            key = segment;
            words.Add(Words(segment));
        }

        return UiText.Join(", ", words);
    }

    private static string Words(string key)
    {
        if (FieldWords.TryGetValue(key, out string? words))
            return words;
        var text = new StringBuilder(key.Length + 4);
        foreach (char c in key)
        {
            if (char.IsUpper(c) && text.Length > 0)
                text.Append(' ');
            text.Append(char.ToLowerInvariant(c));
        }

        return text.ToString();
    }

    private static string Body(string field, string key, string message)
    {
        string unit = SecondFields.Contains(key) ? " s" : FieldUnits.TryGetValue(key, out string? u) ? u : "";
        string subject = field.Length == 0 ? UiText.MessageText("text.eb69abcc54d8", "the value") : UiText.Concat(UiText.MessageText("text.6e5ce6afa65b", "the "), field);
        Match range = OutOfRange.Match(message);
        if (range.Success)
        {
            string low = range.Groups["kind"].Value == "greater than" ? UiText.MessageText("text.88c7f08d0be5", "above") : UiText.MessageText("text.e9213756a2e6", "at least");
            return UiText.Format("text.6295ac00f149", $"{subject} must be {low} {range.Groups["min"].Value}{unit} and at most {range.Groups["max"].Value}{unit}");
        }

        return message switch
        {
            "this field is required" => UiText.Format("text.574c66dbc6f4", $"{subject} is missing"),
            "expected a number" => UiText.Format("text.6176acdbdb05", $"{subject} must be a number"),
            "expected text" => UiText.Format("text.faa4c1fec3e2", $"{subject} must be a text"),
            "expected true or false" => UiText.Format("text.0e9708657553", $"{subject} must be on or off"),
            "unknown field" => UiText.Format("text.4f92be615fa8", $"the field \"{field}\" is not known"),
            _ => field.Length == 0 ? message : UiText.Format("", $"{subject}: {message}"),
        };
    }

    private static string TakeLabel(int take, IReadOnlyList<string> takeNames, int selectedTake, bool alone)
    {
        if (take == selectedTake && !alone)
            return "";
        string name = take < takeNames.Count ? takeNames[take] : (take + 1).ToString(CultureInfo.InvariantCulture);
        return take == selectedTake ? UiText.MessageText("text.edeb95ba450f", "This take") : UiText.Format("text.a67859ff2f31", $"Take \"{name}\"");
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static string Sentence(string prefix, string body)
    {
        string text = prefix.Length == 0 ? Capital(body) : UiText.Concat(UiText.Concat(prefix, ": "), body);
        text = UiText.TrimEnd(text);
        return text.EndsWith('.') || text.EndsWith('?') || text.EndsWith('!') ? text : UiText.Concat(text, ".");
    }

    private static string Capital(string text) =>
        UiText.Capital(text);
}
