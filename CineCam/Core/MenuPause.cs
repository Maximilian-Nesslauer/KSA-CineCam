using System.Reflection;
using Brutal.Logging;
using HarmonyLib;
using KSA;

namespace CineCam.Core;

// The pause of the game menus. Program.UpdateMenuPause saves the sim speed in
// _menuPauseRestoreSpeed when a menu opens, holds the speed at 0 while it is open, and sets the
// saved speed again when it closes. A speed that the mod gives back during that pause must go
// into the saved value, because a speed set directly is replaced by 0 in the next frame.
internal static class MenuPause
{
    private static readonly FieldInfo? ActiveField = AccessTools.DeclaredField(typeof(Program), "_menuPauseActive");
    private static readonly FieldInfo? SpeedField = AccessTools.DeclaredField(typeof(Program), "_menuPauseRestoreSpeed");

    public static bool Active => ActiveField?.FieldType == typeof(bool) && ActiveField.GetValue(null) is true;

    // The speed that the game sets when the menu closes, or null when it cannot be read.
    public static double? RestoreSpeed =>
        SpeedField?.FieldType == typeof(double) && SpeedField.GetValue(null) is double speed ? speed : null;

    // Sets the speed that the game sets when the menu closes. Returns false when the field is not found.
    public static bool Hand(double speed)
    {
        if (SpeedField?.FieldType != typeof(double))
        {
            DefaultCategory.Log.Warning("[CineCam] The menu pause speed could not be set. Set the sim speed after the menu closes.");
            return false;
        }

        SpeedField.SetValue(null, speed);
        return true;
    }
}
