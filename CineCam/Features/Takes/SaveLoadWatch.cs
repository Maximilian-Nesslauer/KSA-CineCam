using KSA;

namespace CineCam.Features.Takes;

// Counts finished save loads. UncompressedSave.Load also returns normally when the game refuses
// the load, for example with the vehicle editor open, or cannot read the save, and only a finished
// load replaces UniverseData.
internal static class SaveLoadWatch
{
    /// <summary>True when the patch is applied, else a reset compares the controlled vehicle instead.</summary>
    public static bool Available { get; set; }

    public static int Completed { get; private set; }

    public static void Reset()
    {
        Available = false;
        Completed = 0;
    }

    public static void Prefix(UncompressedSave __instance, out UniverseData __state) =>
        __state = __instance.UniverseData;

    public static void Postfix(UncompressedSave __instance, UniverseData __state)
    {
        if (!ReferenceEquals(__instance.UniverseData, __state))
            Completed++;
    }
}
