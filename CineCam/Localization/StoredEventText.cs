using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CineCam.Localization;

public static class StoredEventText
{
    private static readonly (string Id, string English)[] Templates =
    [
        ("text.12590f0abb89", "peak {0:F2} kPa, found {1:F1} s later"),
        ("text.2976533afa4f", "docked ports {0} to {1}"),
        ("text.335e0f8d3664", "deflection {0:F0} percent of the limit"),
        ("text.51de4d008e70", "already on at the start"),
        ("text.7b833f8425b7", "from {0} to {1}"),
        ("text.a150a438cc6b", "fired; its parts left the vehicle at once"),
        ("text.b1976078e491", "failed probe cores {0} to {1}"),
        ("text.ce91fbe71a64", "kittens incapacitated {0} to {1}"),
        ("text.e53e2a96516f", "another engine lit while the engines ran"),
        ("text.f06a21e7fb12", "out of power"),
        ("text.f06b2b90809e", "already flying at the start"),
        ("detail.controllable", "controllable"),
        ("detail.crew.incapacitated", "crew incapacitated"),
        ("detail.probe.core.unpowered", "probe core unpowered"),
        ("detail.probe.core.off", "probe core off"),
        ("detail.probe.core.failed", "probe core failed"),
        ("detail.probe.core.storm.standby", "probe core storm standby"),
        ("detail.probe.core.rebooting", "probe core rebooting"),
        ("detail.no.crew", "no crew"),
        ("detail.low", "low"),
        ("detail.critical", "critical"),
        ("detail.out", "out"),
    ];

    private static readonly (string Id, string English, Regex Pattern, int Count)[] Patterns = Templates.Select(Build).ToArray();

    private static (string Id, string English, Regex Pattern, int Count) Build((string Id, string English) entry)
    {
        var pattern = new StringBuilder("\\A");
        int start = 0, count = 0;
        foreach (Match slot in Regex.Matches(entry.English, @"\{(\d+)(?::[^}]+)?\}"))
        {
            pattern.Append(Regex.Escape(entry.English[start..slot.Index]));
            int index = int.Parse(slot.Groups[1].Value, CultureInfo.InvariantCulture);
            pattern.Append("(?<a").Append(index).Append(">.*?)");
            count = Math.Max(count, index + 1);
            start = slot.Index + slot.Length;
        }
        pattern.Append(Regex.Escape(entry.English[start..])).Append("\\z");
        return (entry.Id, entry.English, new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking), count);
    }

    // Only built-in event details enter this parser. Captured values remain protocol data.
    public static string Describe(string detail)
    {
        foreach (var entry in Patterns)
        {
            Match match = entry.Pattern.Match(detail);
            if (!match.Success) continue;
            if (entry.Count == 0) return UiText.MessageText(entry.Id, entry.English);
            object?[] arguments = Enumerable.Range(0, entry.Count).Select(i => (object?)match.Groups["a" + i].Value).ToArray();
            return UiText.Format(entry.Id, System.Runtime.CompilerServices.FormattableStringFactory.Create(entry.English, arguments));
        }
        return detail;
    }
}
