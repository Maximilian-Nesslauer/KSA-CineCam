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

    public static UncompressedSave? ExactCheckpoint(string saveId, string checkpointFolder, GameSave? entry,
        Func<string, IEnumerable<string>>? enumerateChildren = null)
    {
        if (entry is not UncompressedSave save)
            return null;

        string registeredFolder = Path.TrimEndingDirectorySeparator(save.Directory.FullName);
        if (save.Id == saveId && registeredFolder == checkpointFolder)
            return save;

        if (!OperatingSystem.IsWindows()
            || !string.Equals(save.Id, saveId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(registeredFolder, checkpointFolder, StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(checkpointFolder) is not { } parent
            || parent != Path.GetDirectoryName(registeredFolder))
            return null;

        // Windows can allow multiple casing variants in one directory, so require one actual child.
        string name = Path.GetFileName(checkpointFolder);
        try
        {
            IEnumerable<string> children = enumerateChildren?.Invoke(parent) ?? Directory.EnumerateDirectories(parent);
            string? match = null;
            foreach (string child in children)
            {
                if (!string.Equals(Path.GetFileName(child), name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (match != null)
                    return null;
                match = child;
            }
            return match == registeredFolder ? save : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
