using Brutal.Logging;

namespace CineCam.Core;

// Warnings and errors use separate key sets, so WarnOnce(k) never silences a later ErrorOnce(k).
// Not thread-safe, call from the main thread only.
internal static class LogHelper
{
    private static readonly HashSet<string> _loggedWarnings = new();
    private static readonly HashSet<string> _loggedErrors = new();

    public static void WarnOnce(string key, string message)
    {
        if (_loggedWarnings.Add(key))
            DefaultCategory.Log.Warning(message);
    }

    public static void ErrorOnce(string key, string message)
    {
        if (_loggedErrors.Add(key))
            DefaultCategory.Log.Error(message);
    }

    public static void Reset()
    {
        _loggedWarnings.Clear();
        _loggedErrors.Clear();
    }
}
