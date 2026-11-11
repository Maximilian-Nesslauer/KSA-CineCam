namespace CineCam.Core;

// Debug log toggles per feature, all on in DEBUG builds and all off in Release builds.
internal static class DebugConfig
{
#if DEBUG
    public static bool Performance = true;
#else
    public static bool Performance = false;
#endif

    public static bool Any => Performance;
}
