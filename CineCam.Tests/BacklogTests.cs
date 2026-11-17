using CineCam.Director;
using CineCam.Editing;

namespace CineCam.Tests;

internal static class BacklogTests
{
    public static void Run(Action<string, Action> check, Action<bool, string> expect)
    {
        check("timeline layout: a run that a reset ended early keeps the later event times of an older run", () =>
        {
            TimelineLayout.RunLog full = Run(
                "1.000,event,ignition", "3.000,event,liftoff", "40.000,event,altitude:5000", "60.000,event,burnout", "70.000,take,end");
            TimelineLayout.RunLog early = Run("1.200,event,ignition", "3.100,event,liftoff", "12.000,take,end");
            Dictionary<string, double> merged = TimelineLayout.MergeRuns([early, full]);
            expect(merged.Count == 4, "four events: " + string.Join(", ", merged.Keys));
            expect(merged["ignition"] == 1.2 && merged["liftoff"] == 3.1, "the newest run wins for its events");
            expect(merged["altitude:5000"] == 40.0 && merged["burnout"] == 60.0, "the events after the early end stay");
        });

        check("timeline layout: an event that the newer run passed without seeing it is left out", () =>
        {
            TimelineLayout.RunLog older = Run("2.000,event,ignition", "5.000,event,sequence:2", "30.000,event,burnout", "40.000,take,end");
            TimelineLayout.RunLog newer = Run("2.000,event,ignition", "20.000,take,end");
            Dictionary<string, double> merged = TimelineLayout.MergeRuns([newer, older]);
            expect(!merged.ContainsKey("sequence:2"), "the stage did not come in the newer run before its end");
            expect(merged.ContainsKey("burnout"), "the burnout after the end of the newer run stays");
        });

        check("timeline layout: an unfinished run and a run without an end time stop nothing they should not", () =>
        {
            TimelineLayout.RunLog crashed = Run("1.000,event,ignition");
            TimelineLayout.RunLog untimed = Run("1.500,event,ignition", "x,take,end");
            TimelineLayout.RunLog oldest = Run("1.700,event,ignition", "9.000,event,liftoff", "20.000,take,end");
            expect(!crashed.Finished && untimed.Finished && untimed.EndSeconds == null, "the end rows");
            Dictionary<string, double> merged = TimelineLayout.MergeRuns([crashed, untimed, oldest]);
            expect(merged.Count == 1 && merged["ignition"] == 1.5, "a run with no end time is the last one used");
            expect(TimelineLayout.MergeRuns([crashed]).Count == 0, "no finished run gives no times");
        });
    }

    private static TimelineLayout.RunLog Run(params string[] rows)
    {
        var text = new System.Text.StringBuilder("takeSeconds,gameSeconds,missionSeconds,simSpeed,frame,kind,name,detail\n");
        foreach (string row in rows)
        {
            string[] parts = row.Split(',');
            text.Append(parts[0]).Append(",0,0,1,,").Append(parts[1]).Append(',').Append(parts[2]).Append(",\n");
        }

        return TimelineLayout.ReadRun(new StringReader(text.ToString()));
    }
}
