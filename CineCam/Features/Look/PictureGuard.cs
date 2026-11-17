using System.Globalization;
using System.Runtime.CompilerServices;
using Brutal.Logging;
using CineCam.Core;
using CineCam.Director;
using KSA;
using GameProgram = KSA.Program;

namespace CineCam.Features.Look;

// Writes the picture settings of a take into GameSettings.Current.Graphics while the take runs or
// renders, and the values of the player back after it. The settings file is never written. The
// renderers read GameSettings.ShowCloudAmbientLight (CloudRenderer), the Off check of
// GraphicsSettings.CloudShadows (CloudShadowsRenderer.RenderScreenShadows) and
// GameSettings.ShowReflections in every frame, so these changes show at once. The exposure reaches
// the composite pass only through Program.SetTonemapperSettings. The quality of the cloud shadows is
// fixed when the CloudRenderer is made, so a take can turn the cloud shadows off, but not on when the
// player has them off.
internal static class PictureGuard
{
    private sealed class Saved(GameSettings owner)
    {
        public GameSettings Owner { get; } = owner;
        public float Exposure { get; } = owner.Graphics.TonemapExposure;
        public bool CloudAmbientLight { get; } = owner.Graphics.CloudAmbientLight;
        public GameSettings.CloudShadows CloudShadows { get; } = owner.Graphics.CloudShadows;
        public bool Reflections { get; } = owner.Graphics.Reflections;
        public bool WroteExposure { get; set; }
        public bool WroteAmbient { get; set; }
        public bool WroteShadows { get; set; }
        public bool WroteReflections { get; set; }
    }

    private static Saved? _saved;
    private static bool _broken;
    private static bool _warnedShadows;

    /// <summary>True when Program.SetTonemapperSettings was found, so the exposure can change.</summary>
    public static bool ExposureAvailable { get; set; }

    public static bool SaveGuarded { get; set; }

    public static void SavePrefix(GameSettings __instance, out Action? __state)
    {
        Action? resumeStats = FrameStats.BeforeSave(__instance);
        __state = resumeStats;
        Action? resumePicture = BeforeSave(__instance);
        if (resumePicture != null)
            __state = resumeStats == null ? resumePicture : resumeStats + resumePicture;
    }

    public static Exception? SaveFinalizer(Exception? __exception, Action? __state)
    {
        __state?.Invoke();
        return __exception;
    }

    // GameSettings.Save serializes the instance directly, including calls from CrewClock.Toggle.
    public static Action? BeforeSave(GameSettings owner)
    {
        if (_saved is not { } saved || !ReferenceEquals(saved.Owner, owner))
            return null;
        GameSettings.GraphicsSettings graphics = owner.Graphics;
        float exposure = graphics.TonemapExposure;
        bool ambient = graphics.CloudAmbientLight;
        GameSettings.CloudShadows shadows = graphics.CloudShadows;
        bool reflections = graphics.Reflections;
        if (saved.WroteExposure)
            graphics.TonemapExposure = saved.Exposure;
        if (saved.WroteAmbient)
            graphics.CloudAmbientLight = saved.CloudAmbientLight;
        if (saved.WroteShadows)
            graphics.CloudShadows = saved.CloudShadows;
        if (saved.WroteReflections)
            graphics.Reflections = saved.Reflections;
        return () =>
        {
            if (!ReferenceEquals(_saved, saved) || !ReferenceEquals(GameSettings.Current, owner))
                return;
            if (saved.WroteExposure)
                graphics.TonemapExposure = exposure;
            if (saved.WroteAmbient)
                graphics.CloudAmbientLight = ambient;
            if (saved.WroteShadows)
                graphics.CloudShadows = shadows;
            if (saved.WroteReflections)
                graphics.Reflections = reflections;
        };
    }

    public static void Update(TakePicture? wanted)
    {
        if (_broken)
            return;
        if (wanted == null || wanted.IsEmpty)
        {
            Restore();
            return;
        }

        try
        {
            UpdateCore(wanted);
        }
        catch (Exception ex)
        {
            _broken = true;
            LogHelper.WarnOnce("picture", $"[CineCam] The picture settings of the take could not be set: {ex.Message}");
            Restore();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void UpdateCore(TakePicture wanted)
    {
        GameSettings current = GameSettings.Current;
        if (_saved != null && !ReferenceEquals(_saved.Owner, current))
        {
            // The player applied new settings, which hold the values of the player again.
            _saved = null;
        }

        Saved saved = _saved ??= new Saved(current);
        GameSettings.GraphicsSettings graphics = current.Graphics;
        bool tonemap = false;

        float exposure = wanted.Exposure is double e && ExposureAvailable ? (float)e : saved.Exposure;
        if (wanted.Exposure.HasValue && !ExposureAvailable)
            LogHelper.WarnOnce("picture-exposure", "[CineCam] The exposure of the take is not used, because this game build has no Program.SetTonemapperSettings.");
        if (graphics.TonemapExposure != exposure && (wanted.Exposure.HasValue || saved.WroteExposure))
        {
            graphics.TonemapExposure = exposure;
            tonemap = true;
        }

        saved.WroteExposure = wanted.Exposure.HasValue && ExposureAvailable;

        graphics.CloudAmbientLight = Pick(wanted.CloudAmbientLight, saved.CloudAmbientLight, saved.WroteAmbient, graphics.CloudAmbientLight);
        saved.WroteAmbient = wanted.CloudAmbientLight.HasValue;

        graphics.Reflections = Pick(wanted.Reflections, saved.Reflections, saved.WroteReflections, graphics.Reflections);
        saved.WroteReflections = wanted.Reflections.HasValue;

        GameSettings.CloudShadows shadows = graphics.CloudShadows;
        if (wanted.CloudShadows == false)
        {
            shadows = GameSettings.CloudShadows.Off;
        }
        else if (saved.WroteShadows)
        {
            shadows = saved.CloudShadows;
        }

        if (wanted.CloudShadows == true && saved.CloudShadows == GameSettings.CloudShadows.Off && !_warnedShadows)
        {
            _warnedShadows = true;
            DefaultCategory.Log.Warning("[CineCam] The take wants the cloud shadows on, but they are off in the game settings. Turn them on there and start the game again, because the game sets their quality at its start.");
        }

        graphics.CloudShadows = shadows;
        saved.WroteShadows = wanted.CloudShadows == false;

        if (tonemap)
            ApplyTonemap();
    }

    // A value that the take does not pin is left alone, except one that the take pinned before and
    // now gives back.
    private static bool Pick(bool? wanted, bool saved, bool wrote, bool now) =>
        wanted ?? (wrote ? saved : now);

    public static void Restore()
    {
        if (_saved is not { } saved)
            return;
        _saved = null;
        try
        {
            RestoreCore(saved);
        }
        catch (Exception ex)
        {
            _broken = true;
            LogHelper.WarnOnce("picture-restore", $"[CineCam] The picture settings of the player could not be set back: {ex.Message}");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RestoreCore(Saved saved)
    {
        if (!ReferenceEquals(GameSettings.Current, saved.Owner))
            return;
        GameSettings.GraphicsSettings graphics = saved.Owner.Graphics;
        if (saved.WroteAmbient)
            graphics.CloudAmbientLight = saved.CloudAmbientLight;
        if (saved.WroteReflections)
            graphics.Reflections = saved.Reflections;
        if (saved.WroteShadows)
            graphics.CloudShadows = saved.CloudShadows;
        if (saved.WroteExposure && graphics.TonemapExposure != saved.Exposure)
        {
            graphics.TonemapExposure = saved.Exposure;
            ApplyTonemap();
        }
    }

    private static void ApplyTonemap()
    {
        if (GameProgram.Instance is { } program)
            GameReflection.Program_SetTonemapperSettings?.Invoke(program, null);
    }

    /// <summary>The exposure that the composite pass uses now, for the log.</summary>
    public static string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"exposure {GameSettings.Current.Graphics.TonemapExposure:G4}, cloud ambient light {GameSettings.Current.Graphics.CloudAmbientLight}, cloud shadows {GameSettings.Current.Graphics.CloudShadows}, reflections {GameSettings.Current.Graphics.Reflections}");

    public static void Reset()
    {
        SaveGuarded = false;
        _saved = null;
        _broken = false;
        _warnedShadows = false;
    }
}
