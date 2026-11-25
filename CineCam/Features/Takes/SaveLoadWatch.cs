using KSA;

namespace CineCam.Features.Takes;

internal static class SaveLoadWatch
{
    public static bool Available { get; set; }

    public static int Completed { get; private set; }
    private static string? _lastAppliedId;

    public static void Reset()
    {
        Available = false;
        Completed = 0;
        _lastAppliedId = null;
    }

    public static bool AppliedSince(string saveId, int before) =>
        Completed != before && string.Equals(_lastAppliedId, saveId, StringComparison.Ordinal);

    public static void Postfix(UncompressedSave __instance, bool __runOriginal)
    {
        if (!__runOriginal)
            return;
        _lastAppliedId = __instance.Id;
        Completed++;
    }
}
