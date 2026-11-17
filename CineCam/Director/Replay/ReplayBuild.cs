using System.Globalization;
using System.Text.RegularExpressions;

namespace CineCam.Director.Replay;

/// <summary>How the game build of a recording relates to the build that runs.</summary>
public enum ReplayBuildAge { Same, Older, Newer, Unknown }

/// <summary>
/// The game build of a recording against the running build. A recording opens on its own build, and on a
/// later build only after the player confirms it, because parts, terrain and the star system can differ.
/// A recording of a later build or of a build whose order is unknown never opens.
/// </summary>
public static partial class ReplayBuild
{
    // The format of KSA.VersionInfo.VersionString, "v{Year}.{Month}.{Day}.{Revision}" with an optional
    // "-{Suffix}". VersionInfo.Parse accepts the same text with or without the "v".
    [GeneratedRegex(@"^v?(?<Year>[0-9]{1,9})\.(?<Month>[0-9]{1,9})\.(?<Day>[0-9]{1,9})\.(?<Revision>[0-9]{1,9})(?:-(?<Suffix>[^+]+))?(?:\+.*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public readonly record struct Numbers(int Year, int Month, int Day, int Revision, string Suffix) : IComparable<Numbers>
    {
        /// <summary>Compares the date and the revision. The suffix does not give an order.</summary>
        public int CompareTo(Numbers other)
        {
            int result = Year.CompareTo(other.Year);
            if (result == 0) result = Month.CompareTo(other.Month);
            if (result == 0) result = Day.CompareTo(other.Day);
            if (result == 0) result = Revision.CompareTo(other.Revision);
            return result;
        }
    }

    public static bool TryParse(string? text, out Numbers numbers)
    {
        numbers = default;
        if (string.IsNullOrWhiteSpace(text) || Pattern().Match(text.Trim()) is not { Success: true } match)
            return false;
        if (!Int(match, "Year", out int year) || !Int(match, "Month", out int month)
            || !Int(match, "Day", out int day) || !Int(match, "Revision", out int revision))
            return false;
        numbers = new Numbers(year, month, day, revision,
            match.Groups["Suffix"].Success ? match.Groups["Suffix"].Value : "");
        return true;

        static bool Int(Match match, string group, out int value) =>
            int.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Two builds with the same date and revision but another suffix have an unknown order.</summary>
    public static ReplayBuildAge Compare(string? recorded, string? current)
    {
        if (recorded != null && current != null && string.Equals(recorded.Trim(), current.Trim(), StringComparison.Ordinal))
            return ReplayBuildAge.Same;
        if (!TryParse(recorded, out Numbers made) || !TryParse(current, out Numbers now))
            return ReplayBuildAge.Unknown;
        int order = made.CompareTo(now);
        return order < 0 ? ReplayBuildAge.Older
            : order > 0 ? ReplayBuildAge.Newer
            : string.Equals(made.Suffix, now.Suffix, StringComparison.Ordinal) ? ReplayBuildAge.Same
            : ReplayBuildAge.Unknown;
    }

    /// <summary>The revision number of a build for a narrow column, or the whole text when it has another form.</summary>
    public static string Short(string? build) =>
        TryParse(build, out Numbers numbers)
            ? UiText.Concat(numbers.Revision.ToString(CultureInfo.InvariantCulture), (numbers.Suffix.Length > 0 ? UiText.Concat("-", numbers.Suffix) : ""))
            : string.IsNullOrWhiteSpace(build) ? "-" : build.Trim();

    /// <summary>
    /// Why a recording does not open on this build, or null when it opens. A recording of an older build
    /// opens only when <paramref name="olderConfirmed"/> is true.
    /// </summary>
    public static (string Code, string Message)? OpenBlock(string? recorded, string current, bool olderConfirmed) =>
        Compare(recorded, current) switch
        {
            ReplayBuildAge.Same => null,
            ReplayBuildAge.Older when olderConfirmed => null,
            ReplayBuildAge.Older => ("older_game_build",
                UiText.Format("text.9088e46890d2", $"This recording is from KSA {Name(recorded)}, an older build than {current}. Confirm to open it.")),
            ReplayBuildAge.Newer => ("game_version_mismatch",
                UiText.Format("text.bfc5d8073b6d", $"This recording is from KSA {Name(recorded)}, a later build than {current}. Update the game to open it.")),
            _ => ("game_version_mismatch",
                UiText.Format("text.200938a61450", $"This recording is from KSA {Name(recorded)}, and the order of that build and {current} is not known, so it does not open.")),
        };

    /// <summary>The text that the confirm of an older recording shows.</summary>
    public static string OlderWarning(string? recorded, string current) =>
        UiText.Concat(UiText.Concat(UiText.Format("text.22e5886e9ce3", $"This recording is from KSA {Name(recorded)}, and this game is {current}. The replay builds the vehicles from the parts of this build, "), UiText.MessageText("text.17081fe29c2d", "so a part that changed can look different, and a part whose modules changed shows without its recorded state. ")), UiText.MessageText("text.cc9e6f599b3b", "The terrain and the star system are those of this build."));

    /// <summary>The line of the replay block that names both builds.</summary>
    public static string BuildLine(string? recorded, string current) =>
        Compare(recorded, current) == ReplayBuildAge.Same
            ? UiText.Format("text.61e20397a605", $"Recorded on KSA {Name(recorded)}, the build of this game.")
            : UiText.Format("text.686392591aef", $"Recorded on KSA {Name(recorded)}. This game is {current}.");

    private static string Name(string? build) => string.IsNullOrWhiteSpace(build) ? UiText.MessageText("text.f16599b08b8d", "(unknown build)") : build.Trim();
}

/// <summary>
/// What an open replay left out because the recording does not fit the parts of this build. The counts are
/// segments, a stretch of a recorded vehicle with one part tree.
/// </summary>
public sealed class ReplayFitReport
{
    public int Segments { get; set; }

    /// <summary>Segments whose recorded part state does not fit the decoded part tree.</summary>
    public int PartStateLeftOut { get; set; }

    /// <summary>Segments whose recorded engines do not fit the engines of the decoded part tree.</summary>
    public int ExhaustLeftOut { get; set; }

    /// <summary>Connections to a part connector that this build no longer has, dropped as the game drops them from a saved vehicle.</summary>
    public int ConnectorsDropped { get; set; }

    public bool IsClean => PartStateLeftOut == 0 && ExhaustLeftOut == 0 && ConnectorsDropped == 0;

    /// <summary>A sentence for the replay block, or an empty text when nothing was left out.</summary>
    public string Text()
    {
        var parts = new List<string>();
        if (PartStateLeftOut > 0)
            parts.Add(UiText.Format("text.c66462816309", $"{PartStateLeftOut} of {Segments} vehicle segments show their parts without the recorded state (panels, legs, gimbals, heat, lights), because the part modules differ from the recording"));
        if (ExhaustLeftOut > 0)
            parts.Add(UiText.Format("text.6bfb61332584", $"{ExhaustLeftOut} of {Segments} vehicle segments show no engine plumes, because their engines differ from the recording"));
        if (ConnectorsDropped > 0)
            parts.Add(UiText.Format("text.b1a8569fb19e", $"{ConnectorsDropped} part connections to connectors that the parts no longer have were dropped"));
        return parts.Count == 0 ? "" : UiText.Concat(UiText.Join(". ", parts), ".");
    }
}

/// <summary>
/// The check of the recorded part channels of a segment against the channels that this build gives for its
/// decoded part tree, in the order of PartTreeIterator with the sub parts.
/// </summary>
public static class ReplayPartLayout
{
    /// <summary>
    /// Null when the recorded channels have the kinds of the tree in the same order and, when the segment has
    /// its key, the parts that own them are the same. Otherwise the reason of the mismatch.
    /// </summary>
    public static string? Mismatch(ReplaySegment segment, IReadOnlyList<string> kinds, uint key)
    {
        if (segment.Channels is not { } channels)
            return UiText.MessageText("text.4f17961f3264", "the segment has no part channels");
        if (channels.Count != kinds.Count)
            return UiText.Format("text.aa7d997f0c05", $"the recording has {channels.Count} part channels and the parts of this build give {kinds.Count}");
        for (int i = 0; i < channels.Count; i++)
        {
            if (!string.Equals(channels[i].Kind, kinds[i], StringComparison.Ordinal))
                return UiText.Format("text.ebdb6236abb6", $"part channel {i + 1} is \"{channels[i].Kind}\" in the recording and \"{kinds[i]}\" with the parts of this build");
        }
        if (segment.PartKey is uint recorded && recorded != key)
            return UiText.MessageText("text.9e62a3eefcb3", "the parts that own the part channels differ from the recording");
        return null;
    }

    /// <summary>
    /// True when every sample of the segment has no engine (no engine burns) or one engine for each nozzle of
    /// the decoded tree.
    /// </summary>
    public static bool NozzlesFit(IEnumerable<ReplaySample> samples, int nozzles) =>
        samples.All(sample => sample.Nozzles.Length == 0 || sample.Nozzles.Length == nozzles);
}
