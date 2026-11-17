using System.Runtime.CompilerServices;
using Brutal.Logging;
using CineCam.Core;
using KSA;

namespace CineCam.Features;

// The part picker of the life support transfer window colours the parts of the vehicle
// (Part.HighlightedForPicking) and refreshes the colours only in PartPicking.OnDrawUi, which the
// game skips while Program.DrawUI is false. The hover colours of LifeSupportWindow
// (Part.HighlightedForHabitatZone) are refreshed only in Program.OnDrawUiFrame. So when a take hides
// the interface, both would stay on the vehicle for the whole take.
internal static class GameUiGuard
{
    private static bool _broken;

    public static void ClearPartHighlights()
    {
        if (_broken)
            return;
        try
        {
            ClearCore();
        }
        catch (Exception ex)
        {
            _broken = true;
            LogHelper.WarnOnce("ui-guard", $"[CineCam] The part highlights of the game could not be cleared: {ex.Message}");
        }
    }

    // True when the part picker is active. A game build without the picker reads as false.
    public static bool PickActive()
    {
        if (_broken)
            return false;
        try
        {
            return PickActiveCore();
        }
        catch (Exception ex)
        {
            _broken = true;
            LogHelper.WarnOnce("ui-guard", $"[CineCam] The part picker of the game could not be read: {ex.Message}");
            return false;
        }
    }

    // The game members are in their own methods, so a missing member throws at the call in the
    // try block above and not when the caller is compiled.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ClearCore()
    {
        if (PartPicking.IsActive)
            PartPicking.Cancel();
        // LifeSupportWindow.UpdateHighlights colours the parts that the window marked as hovered in
        // the frame before and then forgets that mark. The second call has no mark left and clears
        // every colour.
        LifeSupportWindow.UpdateHighlights();
        LifeSupportWindow.UpdateHighlights();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool PickActiveCore() => PartPicking.IsActive;

    public static void Reset() => _broken = false;
}
