using KSA;

namespace CineCam.Features.Takes;

internal static class SaveLoadWatch
{
    public static bool Available { get; set; }

    public static int Completed { get; private set; }
    private static WeakReference<UncompressedSave>? _lastAppliedSave;

    public static void Reset()
    {
        Available = false;
        Completed = 0;
        _lastAppliedSave = null;
    }

    public static UncompressedSave? ExactCheckpoint(string saveId, string checkpointFolder, GameSave? entry)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return entry is UncompressedSave save
            && string.Equals(save.Id, saveId, comparison)
            && string.Equals(Path.TrimEndingDirectorySeparator(save.Directory.FullName), checkpointFolder, comparison)
            ? save : null;
    }

    public static bool AppliedSince(UncompressedSave save, int before) =>
        Completed != before && _lastAppliedSave != null && _lastAppliedSave.TryGetTarget(out var applied)
        && ReferenceEquals(applied, save);

    public static void Postfix(UncompressedSave __instance, bool __runOriginal)
    {
        if (!__runOriginal)
            return;
        _lastAppliedSave = new WeakReference<UncompressedSave>(__instance);
        Completed++;
    }
}
