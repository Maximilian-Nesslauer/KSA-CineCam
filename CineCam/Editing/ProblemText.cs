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
        ["duration"] = "length",
        ["at"] = "start",
        ["event"] = "start event",
        ["time"] = "start time",
        ["delay"] = "delay",
        ["transition"] = "blend",
        ["seconds"] = "blend length",
        ["ramp"] = "ramp",
        ["to"] = "target",
        ["fov"] = "field of view",
        ["fit"] = "fit",
        ["avoidSun"] = "sun angle",
        ["aimOffset"] = "aim offset",
        ["lookAt"] = "look point",
        ["startDeg"] = "start angle",
        ["degPerSec"] = "turn speed",
        ["keepOut"] = "keep-out boxes",
        ["keepSize"] = "keep size",
        ["preRoll"] = "pre-roll",
        ["until"] = "end event",
        ["loop"] = "loop",
        ["autoStartDelay"] = "start delay",
        ["hideUiAt"] = "hide event",
        ["syncFlash"] = "white frame",
        ["fps"] = "frames per second",
        ["points"] = "path",
        ["t"] = "point share",
        ["sequence"] = "sequence",
        ["checkpoint"] = "checkpoint",
        ["name"] = "name",
        ["camera"] = "camera track",
        ["speed"] = "speed track",
        ["actions"] = "action track",
    };

    private static readonly HashSet<string> SecondFields = new(StringComparer.Ordinal)
    {
        "duration", "time", "delay", "seconds", "ramp", "autoStartDelay", "smoothing",
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
            take = IndexOf(takeNames, named.Groups["name"].Value);
            rest = rest[named.Length..];
        }

        Match indexed = TakeIndex.Match(rest);
        if (indexed.Success && int.TryParse(indexed.Groups["i"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int i))
        {
            take = i;
            rest = rest[indexed.Length..];
            if (rest.StartsWith(": ", StringComparison.Ordinal))
                rest = rest[2..];
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
            ProblemItem kind = itemMatch.Groups["name"].Value switch
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
            ProblemItem.Shot => $"shot {itemIndex + 1}",
            ProblemItem.SpeedKey => $"speed key {itemIndex + 1}",
            ProblemItem.Action => $"action {itemIndex + 1}",
            _ => "",
        };
        string prefix = take >= 0 ? TakeLabel(take, takeNames, selectedTake, alone: itemLabel.Length == 0) : "";
        if (itemLabel.Length > 0)
            prefix = prefix.Length == 0 ? Capital(itemLabel) : prefix + ", " + itemLabel;

        string body = Body(field, key, message);
        return new ProblemLine(Sentence(prefix, body), raw ?? "", isError, take, item, itemIndex);
    }

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
        message = text[(colon + 2)..];
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
                string name = m.Groups["name"].Value;
                key = name;
                words.Add(name == "points" ? $"point {index + 1}" : $"{Words(name)} {index + 1}");
                continue;
            }

            // A trigger field reads better as "start event" than as "start, start event".
            if (segment == "at" && s + 1 < segments.Length)
                continue;
            key = segment;
            words.Add(Words(segment));
        }

        return string.Join(", ", words);
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
        string subject = field.Length == 0 ? "the value" : "the " + field;
        Match range = OutOfRange.Match(message);
        if (range.Success)
        {
            string low = range.Groups["kind"].Value == "greater than" ? "above" : "at least";
            return $"{subject} must be {low} {range.Groups["min"].Value}{unit} and at most {range.Groups["max"].Value}{unit}";
        }

        return message switch
        {
            "this field is required" => $"{subject} is missing",
            "expected a number" => $"{subject} must be a number",
            "expected text" => $"{subject} must be a text",
            "expected true or false" => $"{subject} must be on or off",
            "unknown field" => $"the field \"{field}\" is not known",
            _ => field.Length == 0 ? message : $"{subject}: {message}",
        };
    }

    private static string TakeLabel(int take, IReadOnlyList<string> takeNames, int selectedTake, bool alone)
    {
        if (take == selectedTake && !alone)
            return "";
        string name = take < takeNames.Count ? takeNames[take] : (take + 1).ToString(CultureInfo.InvariantCulture);
        return take == selectedTake ? "This take" : $"Take \"{name}\"";
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
        string text = prefix.Length == 0 ? Capital(body) : prefix + ": " + body;
        text = text.TrimEnd();
        return text.EndsWith('.') || text.EndsWith('?') || text.EndsWith('!') ? text : text + ".";
    }

    private static string Capital(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
