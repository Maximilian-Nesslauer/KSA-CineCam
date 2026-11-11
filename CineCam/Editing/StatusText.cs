using System.Globalization;

namespace CineCam.Editing;

/// <summary>A lower <see cref="Priority"/> shows first.</summary>
public readonly record struct FooterItem(int Priority, string Text, bool Pending);

public readonly record struct FooterLine(string Shown, string Tip, bool Pending);

public static class StatusText
{
    /// <summary>From the most important.</summary>
    public static class Priority
    {
        public const int Render = 0;
        public const int Recording = 1;
        public const int Problem = 2;
        public const int Take = 3;
        public const int Replay = 4;
        public const int Quick = 5;
        public const int Note = 6;
    }

    /// <param name="seconds">Take time, or null when no take runs.</param>
    public static string TakeState(RunState state, double? seconds)
    {
        string time = seconds is double s ? " " + ViewTextSeconds(s) : "";
        return state switch
        {
            RunState.Idle => "Ready",
            RunState.Ready => "Ready at the checkpoint",
            RunState.Recording => "Running" + time,
            RunState.DryRun => "Dry run" + time,
            RunState.Preview => "Preview",
            RunState.Capturing => "Saving pictures" + time,
            RunState.Paused => seconds is double p ? "Paused at " + ViewTextSeconds(p) : "Paused",
            _ => "Loading the checkpoint",
        };
    }

    public static string LiveLine(string? takeName, string state, string next)
    {
        string name = string.IsNullOrEmpty(takeName) ? "No take" : $"\"{takeName}\"";
        return next.Length == 0 ? $"{name}  |  {state}" : $"{name}  |  {state}  |  {next}";
    }

    /// <summary>
    /// Shows at most <paramref name="max"/> parts by priority, and the tooltip lists every part when some are
    /// hidden. Pending is true when a shown part is pending.
    /// </summary>
    public static FooterLine Footer(IReadOnlyList<FooterItem> items, int max = 2)
    {
        // A result that two sources report shows once.
        var sorted = new List<FooterItem>(items.Count);
        foreach (FooterItem item in items)
        {
            if (item.Text.Length > 0 && !sorted.Exists(other => other.Text == item.Text))
                sorted.Add(item);
        }

        sorted = sorted.Select((item, index) => (item, index)).OrderBy(p => p.item.Priority).ThenBy(p => p.index).Select(p => p.item).ToList();
        var shown = new List<string>();
        bool pending = false;
        for (int i = 0; i < sorted.Count && i < Math.Max(1, max); i++)
        {
            shown.Add(sorted[i].Text);
            pending |= sorted[i].Pending;
        }

        string tip = sorted.Count > shown.Count ? string.Join("\n", sorted.Select(item => item.Text)) : "";
        return new FooterLine(string.Join("  |  ", shown), tip, pending);
    }

    /// <summary>Cuts after a whole word and adds "...", and returns an empty text when not even one word fits.</summary>
    public static string FitWords(string text, double room, Func<string, double> measure)
    {
        if (measure(text) <= room)
            return text;
        const string Dots = "...";
        string best = "";
        int at = 0;
        while (true)
        {
            int space = text.IndexOf(' ', at);
            if (space < 0)
                break;
            string candidate = text[..space].TrimEnd(' ', ',', ';', ':', '|', '-') + Dots;
            if (measure(candidate) > room)
                break;
            best = candidate;
            at = space + 1;
        }

        return best;
    }

    /// <summary>Each part fits whole or not at all, tried from the lowest priority on.</summary>
    /// <param name="widths">Gap included.</param>
    /// <param name="priorities">A part with priority 0 is always kept.</param>
    public static bool[] FitParts(IReadOnlyList<double> widths, IReadOnlyList<int> priorities, double room)
    {
        var keep = new bool[widths.Count];
        FitParts(widths, priorities, room, keep);
        return keep;
    }

    /// <summary>Writes into <paramref name="keep"/>, which has at least one entry per part, so a draw per frame allocates nothing.</summary>
    public static void FitParts(IReadOnlyList<double> widths, IReadOnlyList<int> priorities, double room, bool[] keep)
    {
        int count = widths.Count;
        Array.Clear(keep);
        Span<bool> tried = count <= 64 ? stackalloc bool[count] : new bool[count];
        double used = 0.0;
        for (int n = 0; n < count; n++)
        {
            int i = -1;
            for (int j = 0; j < count; j++)
            {
                if (!tried[j] && (i < 0 || priorities[j] < priorities[i]))
                    i = j;
            }

            tried[i] = true;
            if (priorities[i] == 0 || used + widths[i] <= room)
            {
                keep[i] = true;
                used += widths[i];
            }
        }
    }

    public static string NotReached(int count) => count.ToString(CultureInfo.InvariantCulture) + " NOT REACHED";

    private static string ViewTextSeconds(double seconds) => Math.Max(0.0, seconds).ToString("0.0", CultureInfo.InvariantCulture) + " s";
}
