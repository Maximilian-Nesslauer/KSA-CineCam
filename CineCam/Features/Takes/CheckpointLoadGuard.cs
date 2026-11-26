using System.Reflection;
using KSA;

namespace CineCam.Features.Takes;

internal static class CheckpointLoadGuard
{
    public const string Unavailable = "the system swap guard is unavailable on KSA build 5949";

    private sealed record Request(UncompressedSave Save, MethodInfo ApplyLoaded, Action BeforeApply);

    // Stock calls RequestLoad inline from UncompressedSave.Load on the frame thread.
    [ThreadStatic]
    private static Request? _request;

    public static bool Available { get; set; }

    public static bool Required => IsAffectedBuild(VersionInfo.Current.Revision);

    public static bool IsAffectedBuild(int revision) => revision == 5949;

    public static string? Load(UncompressedSave save, MethodInfo? applyLoaded, Action beforeApply)
    {
        if (!Available || applyLoaded == null)
            return Unavailable;

        var request = new Request(save, applyLoaded, beforeApply);
        UniverseData previousData = save.UniverseData;
        Request? previous = _request;
        _request = request;
        try
        {
            save.Load();
            return ReferenceEquals(_request, request) ? "the game did not submit the exact checkpoint load" : null;
        }
        catch (Refused ex)
        {
            // An earlier stock swap can still hold ApplyLoaded, which reads this field when it runs.
            save.UniverseData = previousData;
            return ex.Message;
        }
        finally
        {
            _request = previous;
        }
    }

    public static void Prefix(string __0, string __1, ref Action __2)
    {
        if (_request is not { } request || __0 != request.Save.Id || !__2.HasSingleTarget
            || !ReferenceEquals(__2.Target, request.Save) || __2.Method != request.ApplyLoaded)
            return;

        _request = null;
        if (string.IsNullOrEmpty(__1))
            throw new Refused("the checkpoint does not identify a system");
        switch (SaveSystemSwap.PlanLoad(__1))
        {
            case SaveLoadPlan.Load:
            case SaveLoadPlan.LoadIntoRunning:
                Action apply = __2;
                // Let stock select the immediate path before ending the take or stopping warp.
                __2 = () =>
                {
                    request.BeforeApply();
                    apply();
                };
                break;
            case SaveLoadPlan.Swap:
                // NotInstalled would make UncompressedSave.Load report that an installed system is missing.
                throw new Refused($"the checkpoint requires a swap to system \"{__1}\", which can crash KSA build 5949");
            case SaveLoadPlan.NotInstalled:
                throw new Refused($"the checkpoint system \"{__1}\" is not installed");
            default:
                throw new Refused($"the game returned an unknown load plan for system \"{__1}\"");
        }
    }

    private sealed class Refused(string reason) : Exception(reason);
}
