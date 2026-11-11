namespace CineCam.Director;

/// <summary>
/// The names of the game saves that a take uses as its checkpoint. The rules copy those of the game
/// for a save name, so that a valid name is stored and loaded as it is.
/// </summary>
public static class CheckpointName
{
    public const int MaxLength = 64;

    public const string Prefix = "CineCam";

    public const string RuleText = "a save name has letters, digits, spaces, \"-\" and \"_\" only, at most 64 characters, and no space at its start or end";

    private static readonly string[] DeviceNames =
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Makes any text a valid save name, with the same result as the game gives. An empty result stays empty.</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var result = new System.Text.StringBuilder();
        foreach (char c in text)
        {
            if (result.Length == MaxLength)
                break;
            bool allowed = char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_';
            if (allowed && (c != ' ' || (result.Length != 0 && result[^1] != ' ')))
                result.Append(c);
        }

        while (result.Length > 0 && result[^1] == ' ')
            result.Length--;
        string name = result.ToString();
        return Array.Exists(DeviceNames, d => d.Equals(name, StringComparison.OrdinalIgnoreCase)) ? name + "_" : name;
    }

    public static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name) && string.Equals(name, Sanitize(name), StringComparison.Ordinal);

    /// <summary>
    /// Two take names can differ in punctuation only, so a number makes a name unique. The comparison
    /// ignores case, because the save is a folder on Windows.
    /// </summary>
    public static string ForTake(string takeName, IEnumerable<string?> usedByOthers)
    {
        string baseName = Sanitize(Prefix + " " + takeName);
        var used = new HashSet<string>(usedByOthers.Where(n => !string.IsNullOrEmpty(n))!, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(baseName))
            return baseName;

        for (int number = 2; ; number++)
        {
            string suffix = " " + number;
            string stem = baseName.Length + suffix.Length > MaxLength ? baseName[..(MaxLength - suffix.Length)].TrimEnd() : baseName;
            string candidate = stem + suffix;
            if (!used.Contains(candidate))
                return candidate;
        }
    }
}
