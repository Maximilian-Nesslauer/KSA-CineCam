using System.Globalization;
using System.Text.RegularExpressions;

namespace CineCam.Director;

public static class TakeFolders
{
    public static string SafeName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 || c == ' ' ? '_' : c).ToArray());
    }

    public static string FolderName(DateTime time, string takeName) =>
        time.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + SafeName(takeName);

    public static int NextRun(IEnumerable<string> existingFolderNames, string takeName)
    {
        var pattern = new Regex(@"^\d{8}-\d{6}-" + Regex.Escape(SafeName(takeName)) + @"(~\d+)?$", RegexOptions.IgnoreCase);
        return existingFolderNames.Count(n => pattern.IsMatch(n)) + 1;
    }

    /// <summary>Two runs in the same second get a number.</summary>
    public static string Unique(string name, Func<string, bool> exists)
    {
        if (!exists(name))
            return name;
        for (int number = 2; ; number++)
        {
            string candidate = name + "~" + number.ToString(CultureInfo.InvariantCulture);
            if (!exists(candidate))
                return candidate;
        }
    }
}
