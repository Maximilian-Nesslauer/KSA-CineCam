using CineCam.Director;

namespace CineCam.Editing;

public sealed record ValidationResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;

    public static readonly ValidationResult Ok = new(Array.Empty<string>(), Array.Empty<string>());
}

public static class ShotFileValidator
{
    /// <summary>
    /// Writes the file and reads it again with the same reader that a take uses, then builds the timeline of
    /// every take, so the problems are those that a take would find, with the path of the bad field.
    /// </summary>
    public static ValidationResult Validate(ShotFile file)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        ShotFile reread;
        try
        {
            ShotFileReadResult result = ShotFileJson.Parse(ShotFileJson.Write(file));
            reread = result.File;
            warnings.AddRange(result.Warnings);
        }
        catch (ShotFileException ex)
        {
            errors.Add(ex.Message);
            return new ValidationResult(errors, warnings);
        }

        foreach (Take take in reread.Takes)
        {
            try
            {
                _ = new TakeDirector(take, reread);
            }
            catch (ArgumentException ex)
            {
                errors.Add($"Take \"{take.Name}\": {ex.Message}");
            }

            string? site = take.Site ?? reread.Site;
            if (take.Anchor != null && !string.IsNullOrWhiteSpace(site))
                warnings.Add($"Take \"{take.Name}\": the anchor has no effect, because the take uses the Landmark \"{site}\"");
        }

        return new ValidationResult(errors, warnings);
    }

    /// <summary>
    /// A running take uses a deep copy, so an edit never changes a take that already started. Throws when the
    /// file is not valid.
    /// </summary>
    public static ShotFile Copy(ShotFile file) => ShotFileJson.Parse(ShotFileJson.Write(file)).File;
}
