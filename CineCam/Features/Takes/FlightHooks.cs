using System.Runtime.CompilerServices;
using CineCam.Core;
using CineCam.Director;
using KSA;

namespace CineCam.Features.Takes;

/// <summary>
/// Records the EVAs, boardings and explosions of each vehicle for the event detector, which sees the vehicle only
/// once per frame. A reader keeps a cursor per vehicle, as for the fired sequences in <see cref="SequenceWatch"/>.
/// The patches run on the main thread (ExplosionSystem.SpawnForDestruction drops a call from another thread).
/// </summary>
internal static class FlightHooks
{
    private const int KeepEntries = 64;

    public enum Kind { Eva, Board, Explosion }

    public readonly record struct Entry(long Serial, Kind Kind, string Detail);

    private sealed class Log
    {
        public long Serial;
        public readonly List<Entry> Entries = new();
    }

    private static readonly ConditionalWeakTable<Vehicle, Log> Logs = new();
    private static readonly ConditionalWeakTable<KittenEva, Vehicle> Sources = new();

    public static bool EvaAvailable { get; set; }
    public static bool BoardAvailable { get; set; }
    public static bool ExplosionAvailable { get; set; }

    public static void Reset()
    {
        EvaAvailable = false;
        BoardAvailable = false;
        ExplosionAvailable = false;
        Logs.Clear();
        Sources.Clear();
    }

    /// <summary>The vehicle that the kitten left on its EVA, or null.</summary>
    public static Vehicle? SourceOf(KittenEva kitten) => Sources.TryGetValue(kitten, out Vehicle? source) ? source : null;

    public static long Cursor(Vehicle vehicle) => Logs.TryGetValue(vehicle, out Log? log) ? log.Serial : 0;

    /// <summary>The entries after the cursor, in order, or null. The cursor moves to the newest one.</summary>
    public static List<Entry>? Since(Vehicle vehicle, ref long cursor)
    {
        if (!Logs.TryGetValue(vehicle, out Log? log) || log.Serial <= cursor)
            return null;
        List<Entry>? result = null;
        foreach (Entry entry in log.Entries)
        {
            if (entry.Serial > cursor)
                (result ??= new List<Entry>()).Add(entry);
        }

        cursor = log.Serial;
        return result;
    }

    private static void Add(Vehicle vehicle, Kind kind, string detail)
    {
        Log log = Logs.GetOrCreateValue(vehicle);
        log.Serial++;
        log.Entries.Add(new Entry(log.Serial, kind, detail));
        if (log.Entries.Count > KeepEntries)
            log.Entries.RemoveAt(0);
    }

    #region Patches

    // EVADoor.SpawnEva returns early without an EVA in several cases. It gives the control to the new kitten only
    // at its end, so a new KittenEva under the control shows that the EVA happened.
    public static void EvaPrefix(out Vehicle? __state) => __state = Program.ControlledVehicle;

    public static void EvaPostfix(Vehicle vehicle, Vehicle? __state)
    {
        try
        {
            if (vehicle != null && Program.ControlledVehicle is KittenEva kitten && !ReferenceEquals(kitten, __state))
            {
                Add(vehicle, Kind.Eva, kitten.Id);
                Sources.AddOrUpdate(kitten, vehicle);
            }
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("flight-hooks-eva", $"[CineCam] The EVA watch failed: {ex.Message}");
        }
    }

    public static void BoardPostfix(KittenEva __instance, Vehicle host, CrewSeatResult __result)
    {
        try
        {
            if (host != null && __result == CrewSeatResult.Seated)
                Add(host, Kind.Board, __instance.Id);
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("flight-hooks-board", $"[CineCam] The boarding watch failed: {ex.Message}");
        }
    }

    public static void ExplosionPostfix(in ExplosionContext context)
    {
        try
        {
            if (!Program.IsMainThread() || context.Vehicle is not { } vehicle)
                return;
            string cause = EventText.Words(context.Cause.ToString());
            string detail = context.Part is { } part ? $"{part.DisplayName}, {cause}" : UiText.Format("text.97ae53330ffa", $"the whole vehicle, {cause}");
            Add(vehicle, Kind.Explosion, detail);
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("flight-hooks-explosion", $"[CineCam] The explosion watch failed: {ex.Message}");
        }
    }

    #endregion
}
