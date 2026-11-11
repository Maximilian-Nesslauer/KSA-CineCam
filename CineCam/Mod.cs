using Brutal.Logging;
using CineCam.Core;
using CineCam.Features;
using CineCam.Features.Autotest;
using CineCam.Features.Quick;
using CineCam.Features.Replay;
using CineCam.Features.Takes;
using CineCam.Features.Ui;
using CineCam.Features.TimeOfDay;
using CineCam.Patches;
using HarmonyLib;
using KSA;
using StarMap.API;
using StarMap.API.Services;

namespace CineCam;

[StarMapMod]
public sealed class Mod
{
    private static Harmony? _harmony;

    private const string TestedGameVersion = "v2026.11.10.5801";

    private readonly IModMenuService _menu;
    private IDisposable? _menuItem;

    public Mod(IModMenuService menu)
    {
        _menu = menu;
    }

    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        string gameVersion = VersionInfo.Current.VersionString;
        DefaultCategory.Log.Info($"[CineCam] Game version: {gameVersion}");
        if (gameVersion != TestedGameVersion)
            DefaultCategory.Log.Warning(
                $"[CineCam] Tested against {TestedGameVersion}, current is {gameVersion}. " +
                "Some features may not work correctly.");

        CineCamSettings.Load();

        _harmony = new Harmony("com.maxi.cinecam");
        CineCamPatches.Apply(_harmony);
        ReplayPatches.Apply();
        CineCamCommands.TryRegister();
        AutotestRunner.TryStart();

        // StarMap throws when another mod already uses the label, and the mod must still load.
        try
        {
            _menuItem = _menu.AddModMenuItem("CineCam", () =>
            {
                if (Program.IsLoaded)
                    CineCamWindow.RequestToggle();
            });
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Warning($"[CineCam] The Mods menu item could not be added: {ex.Message}");
        }

        DefaultCategory.Log.Info("[CineCam] Loaded and patched.");
    }

    // Runs after the game interface is drawn, and only while the interface is shown.
    [StarMapAfterGui]
    public void Draw(double dt)
    {
        try
        {
            if (!ReplaySession.Available)
                ReplaySession.UpdateFlight();
            // The frame hook tries the registration again while the terminal does not exist yet.
            // Without that hook this draw tries it, so the commands can give their reasons.
            if (!CineCamPatches.FrameAvailable)
                CineCamCommands.TryRegister();
            CineCamWindow.DrawActive(Program.MainViewport);
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("aftergui-" + ex.GetType().Name, $"[CineCam] Error while the window draws: {ex}");
        }
    }

    [StarMapUnload]
    public void Unload()
    {
        _menuItem?.Dispose();
        _menuItem = null;

        // The take and the time move end first, so the camera, the sim speed and the game
        // interface are back before the patches go.
        AutotestRunner.Shutdown();
        ReplayPatches.Shutdown();
        ReplayBridge.Shutdown();
        SunWarp.Cancel();
        TakeRunner.Shutdown();
        QuickCamera.Shutdown();
        CineCamWindow.Shutdown();
        CineCamCommands.Unregister();

        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;

        CineCamPatches.Reset();
        LogHelper.Reset();
#if DEBUG
        PerfTracker.Reset();
#endif
        DefaultCategory.Log.Info("[CineCam] Unloaded.");
    }
}
