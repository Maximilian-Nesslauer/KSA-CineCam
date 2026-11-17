using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CineCam.Director;

namespace CineCam.Localization;

public static class StoredEventText
{
    private static readonly (EventKind Kind, string Id, string English)[] Templates =
    [
        (EventKind.MaxQ, "text.12590f0abb89", "peak {0:F2} kPa, found {1:F1} s later"),
        (EventKind.Docked, "text.2976533afa4f", "docked ports {0} to {1}"),
        (EventKind.Undocked, "text.2976533afa4f", "docked ports {0} to {1}"),
        (EventKind.GimbalLimit, "text.335e0f8d3664", "deflection {0:F0} percent of the limit"),
        (EventKind.Ignition, "text.51de4d008e70", "already on at the start"),
        (EventKind.Soi, "text.7b833f8425b7", "from {0} to {1}"),
        (EventKind.Sequence, "text.a150a438cc6b", "fired; its parts left the vehicle at once"),
        (EventKind.ProbeFailed, "text.b1976078e491", "failed probe cores {0} to {1}"),
        (EventKind.Incapacitated, "text.ce91fbe71a64", "kittens incapacitated {0} to {1}"),
        (EventKind.Ignition, "text.e53e2a96516f", "another engine lit while the engines ran"),
        (EventKind.PowerLow, "text.f06a21e7fb12", "out of power"),
        (EventKind.PowerLow, "detail.low", "low"),
        (EventKind.Liftoff, "text.f06b2b90809e", "already flying at the start"),
        (EventKind.ControlLost, "detail.controllable", "controllable"),
        (EventKind.ControlLost, "detail.crew.incapacitated", "crew incapacitated"),
        (EventKind.ControlLost, "detail.probe.core.unpowered", "probe core unpowered"),
        (EventKind.ControlLost, "detail.probe.core.off", "probe core off"),
        (EventKind.ControlLost, "detail.probe.core.failed", "probe core failed"),
        (EventKind.ControlLost, "detail.probe.core.storm.standby", "probe core storm standby"),
        (EventKind.ControlLost, "detail.probe.core.rebooting", "probe core rebooting"),
        (EventKind.ControlLost, "detail.no.crew", "no crew"),
        (EventKind.LifeSupportLow, "detail.low", "low"),
        (EventKind.LifeSupportLow, "detail.critical", "critical"),
        (EventKind.LifeSupportLow, "detail.out", "out"),
    ];

    private static readonly ILookup<EventKind, (string Id, string English, Regex Pattern, int Count)> Patterns =
        Templates.ToLookup(entry => entry.Kind, entry => Build(entry.Id, entry.English));

    private static (string Id, string English, Regex Pattern, int Count) Build(string id, string english)
    {
        var pattern = new StringBuilder("\\A");
        int start = 0, count = 0;
        foreach (Match slot in Regex.Matches(english, @"\{(\d+)(?::[^}]+)?\}"))
        {
            pattern.Append(Regex.Escape(english[start..slot.Index]));
            int index = int.Parse(slot.Groups[1].Value, CultureInfo.InvariantCulture);
            pattern.Append("(?<a").Append(index).Append(">.*?)");
            count = Math.Max(count, index + 1);
            start = slot.Index + slot.Length;
        }
        pattern.Append(Regex.Escape(english[start..])).Append("\\z");
        return (id, english, new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking), count);
    }

    public static string Label(string eventName, string? detail)
    {
        string name = EventDisplay.Name(eventName);
        if (detail == null)
            return name;
        if (EventName.TryParse(eventName, out EventName parsed, out _))
            detail = Describe(parsed.Kind, detail);
        return UiText.Format("", $"{name} ({detail})");
    }

    public static string Describe(EventKind kind, string detail)
    {
        if (kind == EventKind.Explosion)
            return DescribeExplosion(detail);

        foreach (var entry in Patterns[kind])
        {
            Match match = entry.Pattern.Match(detail);
            if (!match.Success) continue;
            if (entry.Count == 0) return UiText.MessageText(entry.Id, entry.English);
            object?[] arguments = Enumerable.Range(0, entry.Count).Select(i => (object?)match.Groups["a" + i].Value).ToArray();
            return UiText.Format(entry.Id, System.Runtime.CompilerServices.FormattableStringFactory.Create(entry.English, arguments));
        }
        return detail;
    }

    private static string DescribeExplosion(string detail)
    {
        int separator = detail.LastIndexOf(", ", StringComparison.Ordinal);
        if (separator < 0)
            return detail;

        string? cause = detail[(separator + 2)..] switch
        {
            "ground impact" => UiText.MessageText("detail.explosion.ground", "ground impact"),
            "ocean impact" => UiText.MessageText("detail.explosion.ocean", "ocean impact"),
            "collision" => UiText.MessageText("detail.explosion.collision", "collision"),
            "excessive g force" => UiText.MessageText("detail.explosion.gforce", "excessive g force"),
            "aerodynamic forces" => UiText.MessageText("detail.explosion.aero", "aerodynamic forces"),
            "hydrodynamic forces" => UiText.MessageText("detail.explosion.hydro", "hydrodynamic forces"),
            _ => null,
        };
        if (cause == null)
            return detail;

        string subject = detail[..separator];
        return subject == "the whole vehicle"
            ? UiText.Format("text.97ae53330ffa", $"the whole vehicle, {cause}")
            : UiText.Format("", $"{subject}, {cause}");
    }
}
