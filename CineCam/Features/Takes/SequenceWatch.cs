using System.Runtime.CompilerServices;
using CineCam.Core;
using CineCam.Director;
using KSA;

namespace CineCam.Features.Takes;

// Records which sequence each call of SequenceList.ActivateNextSequence fired. The game removes a
// fired sequence at once when all its parts left the vehicle, which lowers ActiveSequence again in
// the same call, so a per-frame sample of the active sequence can miss the fire. The patch only reads.
internal static class SequenceWatch
{
    private const int KeepEntries = 64;

    private sealed class Log
    {
        public long Serial;
        public readonly List<(long serial, int number)> Entries = new();
    }

    private static readonly ConditionalWeakTable<Vehicle, Log> Logs = new();

    public static bool Available { get; set; }

    public static void Reset() => Available = false;

    public static long Cursor(Vehicle vehicle) => Logs.TryGetValue(vehicle, out Log? log) ? log.Serial : 0;

    // The sequence numbers fired after the cursor, in order. The cursor moves to the newest one.
    public static IReadOnlyList<int>? Since(Vehicle vehicle, ref long cursor)
    {
        if (!Logs.TryGetValue(vehicle, out Log? log) || log.Serial <= cursor)
            return null;
        var result = new List<int>();
        foreach ((long serial, int number) in log.Entries)
        {
            if (serial > cursor)
                result.Add(number);
        }

        cursor = log.Serial;
        return result.Count > 0 ? result : null;
    }

    public static void Prefix(SequenceList __instance, out SequenceSnapshot __state)
    {
        try
        {
            __state = Snapshot(__instance);
        }
        catch (Exception ex)
        {
            __state = default;
            LogHelper.ErrorOnce("sequence-watch-prefix", $"[CineCam] The sequence watch failed: {ex.Message}");
        }
    }

    public static void Postfix(SequenceList __instance, Vehicle vehicle, SequenceSnapshot __state)
    {
        try
        {
            int? fired = SequenceFire.Fired(__state, Snapshot(__instance));
            if (fired == null || vehicle == null)
                return;
            Brutal.Logging.DefaultCategory.Log.Info(
                $"[CineCam] Sequence {fired.Value} fired (active {__state.Active} to {__instance.ActiveSequence}, next {__state.Next} to {__instance.GetNextSequenceNumber()}).");
            Log log = Logs.GetOrCreateValue(vehicle);
            log.Serial++;
            log.Entries.Add((log.Serial, fired.Value));
            if (log.Entries.Count > KeepEntries)
                log.Entries.RemoveAt(0);
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("sequence-watch", $"[CineCam] The sequence watch failed: {ex.Message}");
        }
    }

    private static SequenceSnapshot Snapshot(SequenceList list) =>
        new(list.GetNextSequenceNumber(), list.ActiveSequence, list.Count);
}
