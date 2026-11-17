using Brutal.Logging;
using Brutal.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using Brutal.GlfwApi;
using CineCam.Director.Replay;
using CineCam.Features.Replay;
using HarmonyLib;
using KSA;
using KSA.Atmosphere.Rendering;
using KSA.Atmosphere.Rendering.ShaderStructs;
using KSA.Rendering.Particles;
using KSA.Rendering.Water.Rendering;

namespace CineCam.Patches;

internal static class ReplayPatches
{
    private static Harmony? _harmony;
    private static int _worldChangeDepth;

    public static void Apply()
    {
        _harmony = new Harmony("com.maxi.cinecam.replay");
        try
        {
            Type[] none = Type.EmptyTypes;
            Type[] draw = [typeof(IViewport), typeof(int)];
            Type[] input = [typeof(InputAction), typeof(GlfwKeyAction), typeof(GlfwModifier)];
            // A replay draws the recorded flight in the main viewport in place of the live vehicles
            // and holds the game at speed 0. A missing Patch target turns the replay off, and a
            // missing PatchOptional target loses only that detail.
            Patch(typeof(Program), nameof(Program.RefreshVehiclesInFrame), nameof(AfterVehicleRefresh), none, postfix: true);
            Patch(typeof(Program), "OnFrameViewports", nameof(BeforeViewports), [typeof(double)]);
            Patch(typeof(Vehicle), nameof(Vehicle.Split), nameof(AfterSplit),
                [typeof(Part.Connection), typeof(Part.Connection.IConnector), typeof(double),
                    typeof(PoseChange).MakeByRefType(), typeof(string)], postfix: true);
            Patch(typeof(PartModelRenderer), nameof(PartModelRenderer.UpdateRenderData), nameof(BeforePartModels), draw);
            Patch(typeof(Vehicle), nameof(Vehicle.UpdateRenderData), nameof(BeforeVehicleDraw), draw);
            Patch(typeof(Vehicle), nameof(Vehicle.UpdateExhaustLights), nameof(BeforeVehicleLights),
                [typeof(Camera), typeof(IViewport)]);
            Patch(typeof(Vehicle), nameof(Vehicle.AddVolumetricExhaustInstances), nameof(BeforeLiveExhaust),
                [typeof(Camera), typeof(VolumetricExhaustRenderer), typeof(double)]);
            Patch(typeof(VolumetricExhaustRenderer), nameof(VolumetricExhaustRenderer.EndPlumeGrouping), nameof(BeforePlumes), none);
            Patch(typeof(Program), "UpdateShaderData", nameof(ReplaySun), [typeof(double), typeof(IViewport)], transpiler: true);
            Patch(typeof(VolumetricExhaustRenderer), nameof(VolumetricExhaustRenderer.UpdateFrameData), nameof(ReplayPlumeTime),
                none, transpiler: true);
            Patch(typeof(VolumetricExhaustRenderer), nameof(VolumetricExhaustRenderer.EndPlumeGrouping), nameof(ReplayPlumeTime),
                none, transpiler: true);
            PatchWorldChange(nameof(Universe.DeserializeSave), [typeof(UniverseData)]);
            PatchWorldChange(nameof(Universe.LoadSystem), [typeof(string)]);
            Patch(typeof(Universe), nameof(Universe.SetSimulationSpeed), nameof(BeforeSpeed), [typeof(double), typeof(bool)]);
            Patch(typeof(Universe), nameof(Universe.SetSimulationSpeed), nameof(BeforeSpeedSelection), [typeof(SimSpeed)]);
            Patch(typeof(Universe), nameof(Universe.GetJobSimStep), nameof(BeforeSimStep), [typeof(double)]);
            Patch(typeof(Vehicle), nameof(Vehicle.ProcessInput), nameof(BeforeVehicleInput), input);
            PatchOptional(typeof(KittenEva), nameof(KittenEva.ProcessInput), nameof(BeforeVehicleInput), input);

            // The stock vessel and body keys and menu items follow another object with
            // changeControl, which would change the controlled vehicle under the replay.
            PatchOptional(typeof(Universe), nameof(Universe.SeekNextVehicle), nameof(BeforeSeek), [typeof(SeekDirection)]);
            PatchOptional(typeof(Universe), nameof(Universe.SeekNextCelestial), nameof(BeforeSeek), [typeof(SeekDirection)]);

            // Live objects that Program draws outside Vehicle.UpdateRenderData. Without these
            // patches the replay works and the live objects stay visible.
            PatchOptional(typeof(KittenEva), nameof(KittenEva.UpdateRenderData), nameof(BeforeVehicleDraw), draw);
            PatchOptional(typeof(Vehicle), nameof(Vehicle.UpdateParachuteRenderData), nameof(BeforeVehicleDraw), [typeof(IViewport)]);
            PatchOptional(typeof(Vehicle), nameof(Vehicle.UpdateSeatedCrewRenderData), nameof(BeforeVehicleDraw), draw);
            PatchOptional(typeof(VolumetricTrailRenderer), nameof(VolumetricTrailRenderer.PrepareAndRenderShadowCascades),
                nameof(BeforeTrails), Shape(typeof(VolumetricTrailRenderer), nameof(VolumetricTrailRenderer.PrepareAndRenderShadowCascades),
                    "CommandBuffer", nameof(IViewport), nameof(Celestial), "Int32"));
            // The light of the recorded plumes needs the plume data that EndPlumeGrouping computes.
            // Without this patch the recorded plumes give no light.
            PatchOptional(typeof(VolumetricExhaustRenderer), nameof(VolumetricExhaustRenderer.EndPlumeGrouping), nameof(AfterPlumes),
                none, postfix: true);
            Type particles = typeof(ParticleSystem<ParticleUpdateData, ParticleRenderData>);
            foreach (string name in new[] { "WriteCommandsColorOpaque", "WriteCommandsColorTranslucent" })
                PatchOptional(particles, name, nameof(BeforeParticles), Shape(particles, name, "CommandBuffer", nameof(IViewport), "Int32"));
            // The cloud layers turn with the game time, and their shadows darken the ground and the
            // vehicles under them. These patches turn them to the recorded moment. Without them the
            // replay shows the clouds and their shadows of the moment at which the replay opened.
            PatchOptional(typeof(CloudLayerRenderData), nameof(CloudLayerRenderData.UpdateCommonFrameData), nameof(ReplayCloudTime),
                [typeof(int), typeof(int), typeof(double), typeof(double4x4)], transpiler: true);
            PatchOptional(typeof(CloudShadowVolume), "GetPlanetToCloudRotation", nameof(ReplayCloudTime),
                [typeof(CloudLayerRenderData)], transpiler: true);
            // The waves and the aurora move with the game time. These patches give them the recorded
            // moment. Without them they stay as they were when the replay opened.
            PatchOptional(typeof(OceanFFT), "EvolveSpectrums", nameof(ReplayWaveTime),
                Shape(typeof(OceanFFT), "EvolveSpectrums", "CommandBuffer", nameof(IViewport), "int2", "BarrierBatch&"), transpiler: true);
            PatchOptional(typeof(OceanRenderData), nameof(OceanRenderData.UpdateTangentFrameData), nameof(ReplayWaveTime),
                [typeof(double3), typeof(int), typeof(doubleQuat), typeof(doubleQuat), typeof(float4x4)], transpiler: true);
            PatchOptional(typeof(AuroraRenderer), nameof(AuroraRenderer.FillAuroraData), nameof(ReplayAuroraTime),
                [typeof(AtmosphereData).MakeByRefType(), typeof(AtmosphericBody), typeof(float3)], transpiler: true);
            // The small cloud detail moves by the last sim step, which is 0 while the replay holds the
            // game. This patch moves it by the step of the replay, so it moves while the replay plays.
            PatchOptional(typeof(CloudRenderer), nameof(CloudRenderer.RenderWithCompute), nameof(ReplayCloudStep),
                Shape(typeof(CloudRenderer), nameof(CloudRenderer.RenderWithCompute), nameof(AtmosphericBody), "CommandBuffer", nameof(IViewport),
                    "Int32", "Int32", "VkDescriptorSet", "VkDescriptorSet", "ByteSize", "Boolean&"), transpiler: true);
            // The status lights of the probe cores show the recorded status. Without this patch they
            // show the status that the game computes from the decoded vehicle.
            PatchOptional(typeof(PowerConsumer), nameof(PowerConsumer.ComputeStatus), nameof(BeforePowerStatus), [typeof(PartTree)]);
            // The ground clutter picks its shadow casters by the sun direction. Without this patch the
            // replay culls them by the sun of the live moment.
            PatchOptional(typeof(GroundClutterRenderer), "UpdateShadowCullingData", nameof(ReplaySun),
                [typeof(Camera), typeof(Celestial), typeof(int), typeof(int)], transpiler: true);
            // The game is at speed 0 in a replay, so the stock alert "Simulation paused" would stay
            // over the view. A render hides the whole interface, so it needs no patch for that.
            PatchOptional(typeof(FixedAlert), nameof(FixedAlert.Draw), nameof(BeforeFixedAlert), [typeof(float), typeof(double)]);
            // The sim stays at speed 0 in a replay, so the game gives every sound the pitch 0. The
            // engine sounds of the replay get the pitch of the replay rate here. Without the patch
            // the replay plays no engine sound.
            ReplaySound.PitchHook = PatchOptional(typeof(ChannelWrapper), nameof(ChannelWrapper.HandleSimSpeedPitch),
                nameof(BeforeSoundPitch), none);
            ReplaySession.Available = true;
        }
        catch (Exception ex)
        {
            Shutdown();
            DefaultCategory.Log.Warning($"[CineCam] Replay is unavailable: {ex}");
        }
    }

    private static void Patch(Type owner, string target, string method, Type[]? types = null, bool postfix = false, bool transpiler = false)
    {
        var original = AccessTools.DeclaredMethod(owner, target, types)
            ?? throw new MissingMethodException(owner.Name, target);
        var hook = new HarmonyMethod(typeof(ReplayPatches), method);
        _harmony!.Patch(original, prefix: postfix || transpiler ? null : hook, postfix: postfix ? hook : null,
            transpiler: transpiler ? hook : null);
    }

    private static bool PatchOptional(Type owner, string target, string method, Type[]? types, bool postfix = false, bool transpiler = false)
    {
        try
        {
            if (types == null)
                throw new MissingMethodException(owner.Name, target);
            Patch(owner, target, method, types, postfix, transpiler);
            return true;
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Warning($"[CineCam] Replay runs without the patch of {owner.Name}.{target}: {ex.Message}");
            return false;
        }
    }

    // The parameter types of a method whose parameters use a game type that the mod does not
    // reference, found by the type names. Null when no single method has these names.
    private static Type[]? Shape(Type owner, string target, params string[] parameterTypes)
    {
        MethodInfo[] matches = AccessTools.GetDeclaredMethods(owner).Where(method => method.Name == target
            && method.GetParameters().Select(parameter => parameter.ParameterType.Name).SequenceEqual(parameterTypes)).ToArray();
        return matches.Length == 1 ? matches[0].GetParameters().Select(parameter => parameter.ParameterType).ToArray() : null;
    }

    private static void PatchWorldChange(string target, Type[] types)
    {
        var original = AccessTools.DeclaredMethod(typeof(Universe), target, types)
            ?? throw new MissingMethodException(nameof(Universe), target);
        _harmony!.Patch(original, prefix: new HarmonyMethod(typeof(ReplayPatches), nameof(BeforeWorldChange)),
            finalizer: new HarmonyMethod(typeof(ReplayPatches), nameof(AfterWorldChange)));
    }

    public static void Shutdown()
    {
        ReplaySession.Shutdown();
        _harmony?.UnpatchAll(_harmony.Id);
        ReplayPartState.ReleaseAll();
        _harmony = null;
        ReplaySound.PitchHook = false;
        _worldChangeDepth = 0;
        _pausedAlert = null;
        _pausedAlertLooked = false;
    }

    // Program.PrepareFrame refreshes the vehicles after the input and the last solver results and
    // before the next solver step. Universe.DeserializeSave also calls it, which the depth check skips.
    private static void AfterVehicleRefresh()
    {
        if (_worldChangeDepth == 0)
            ReplaySession.UpdateFlight();
    }

    private static void BeforeViewports(double __0) => ReplaySession.UpdatePlayback(__0);

    private static void AfterSplit(Vehicle __instance, Vehicle? __result)
    {
        if (__result != null)
            ReplaySession.ObserveSplit(__instance, __result);
    }

    private static void BeforePartModels(IViewport __0, int __1)
    {
        if (ReplaySession.Scene is not { } scene || !ReferenceEquals(__0, Program.MainViewport))
            return;
        try
        {
            scene.DrawModels(__0, __1);
        }
        catch (Exception ex)
        {
            ReplaySession.Abort(ex);
        }
    }

    private static bool BeforeVehicleDraw(IViewport __0) =>
        !ReplaySession.IsPlayingBack || !ReferenceEquals(__0, Program.MainViewport);

    private static bool BeforeVehicleLights(IViewport __1) =>
        !ReplaySession.IsPlayingBack || !ReferenceEquals(__1, Program.MainViewport);

    private static bool BeforeLiveExhaust(Camera __0) =>
        !ReplaySession.IsPlayingBack || !ReferenceEquals(__0, Program.MainViewport.BaseCamera);

    private static void BeforePlumes(VolumetricExhaustRenderer __instance)
    {
        try
        {
            ReplaySession.Scene?.DrawPlumes(__instance);
        }
        catch (Exception ex)
        {
            ReplaySession.Abort(ex);
        }
    }

    private static void AfterPlumes()
    {
        try
        {
            ReplaySession.Scene?.DrawExhaustLights();
        }
        catch (Exception ex)
        {
            ReplaySession.Abort(ex);
        }
    }

    // Program.UpdateShaderData and GroundClutterRenderer.UpdateShadowCullingData get the sun direction
    // from Camera.GetPositionEgo(Universe.WorldSun), so the replay gives the sun of the recorded moment.
    private static IEnumerable<CodeInstruction> ReplaySun(IEnumerable<CodeInstruction> instructions) =>
        ReplaceCall(instructions, AccessTools.DeclaredMethod(typeof(Camera), nameof(Camera.GetPositionEgo), [typeof(IPosition)]),
            AccessTools.DeclaredMethod(typeof(ReplayPatches), nameof(SunPositionEgo)));

    private static IEnumerable<CodeInstruction> ReplayPlumeTime(IEnumerable<CodeInstruction> instructions) =>
        ReplaceCall(instructions, AccessTools.DeclaredMethod(typeof(Universe), nameof(Universe.GetElapsedSeconds)),
            AccessTools.DeclaredMethod(typeof(ReplayPatches), nameof(VisualTime)));

    private static IEnumerable<CodeInstruction> ReplayCloudTime(IEnumerable<CodeInstruction> instructions) =>
        ReplaceCall(instructions, AccessTools.DeclaredMethod(typeof(Universe), nameof(Universe.GetElapsedSeconds)),
            AccessTools.DeclaredMethod(typeof(ReplayPatches), nameof(CloudTime)));

    // OceanFFT.EvolveSpectrums and OceanRenderData.UpdateTangentFrameData give the game time to the
    // wave shaders.
    private static IEnumerable<CodeInstruction> ReplayWaveTime(IEnumerable<CodeInstruction> instructions) =>
        ReplaceCall(instructions, AccessTools.DeclaredMethod(typeof(Universe), nameof(Universe.GetElapsedSeconds)),
            AccessTools.DeclaredMethod(typeof(ReplayPatches), nameof(CloudTime)));

    // AuroraRenderer.FillAuroraData reads the game time once for StormState.Get and once for the phase.
    private static IEnumerable<CodeInstruction> ReplayAuroraTime(IEnumerable<CodeInstruction> instructions) =>
        ReplaceCall(instructions, AccessTools.DeclaredMethod(typeof(Universe), nameof(Universe.GetElapsedSeconds)),
            AccessTools.DeclaredMethod(typeof(ReplayPatches), nameof(CloudTime)), expected: 2);

    // CloudRenderer.RenderWithCompute reads Universe.GetLastSimStep().DeltaTime once for the flow map
    // phases and the noise scroll of all cloud layers.
    private static IEnumerable<CodeInstruction> ReplayCloudStep(IEnumerable<CodeInstruction> instructions) =>
        ReplaceCall(instructions, AccessTools.DeclaredMethod(typeof(Universe), nameof(Universe.GetLastSimStep)),
            AccessTools.DeclaredMethod(typeof(ReplayPatches), nameof(CloudSimStep)));

    // Each patched method calls the original exactly once. Another count means that the game method
    // changed, so the throw leaves an optional target unchanged and turns the replay off for a
    // required one.
    private static IEnumerable<CodeInstruction> ReplaceCall(IEnumerable<CodeInstruction> instructions, MethodInfo original, MethodInfo replacement,
        int expected = 1)
    {
        List<CodeInstruction> list = instructions.ToList();
        List<CodeInstruction> calls = list.Where(instruction => instruction.Calls(original)).ToList();
        if (calls.Count != expected)
            throw new MissingMethodException($"Expected {expected} render call(s) to {original.Name}, found {calls.Count}.");
        foreach (CodeInstruction call in calls)
        {
            call.opcode = OpCodes.Call;
            call.operand = replacement;
        }
        return list;
    }

    private static double3 SunPositionEgo(Camera camera, IPosition target) =>
        ReplaySession.Scene?.TrySunPosition(camera, target, out double3 position) == true
            ? position : camera.GetPositionEgo(target);

    private static double VisualTime() => ReplaySession.Scene?.VisualTime ?? Universe.GetElapsedSeconds();

    private static double CloudTime() => ReplaySession.Scene?.GameTime ?? Universe.GetElapsedSeconds();

    private static SimStep CloudSimStep()
    {
        SimStep last = Universe.GetLastSimStep();
        return ReplaySession.Scene is { } scene
            ? new SimStep { PreviousTime = last.PreviousTime, NextTime = last.NextTime, DeltaTime = scene.CloudStep }
            : last;
    }

    private static bool BeforePowerStatus(PowerConsumer __instance, ref PowerStatus __result)
    {
        if (!ReplayPartState.TryRecordedStatus(__instance, out PowerStatus status))
            return true;
        __result = status;
        return false;
    }

    private static void BeforeWorldChange(out bool __state)
    {
        __state = true;
        if (_worldChangeDepth++ == 0)
            ReplaySession.OnWorldChange();
    }

    private static void AfterWorldChange(bool __state)
    {
        if (__state)
            _worldChangeDepth--;
    }

    private static bool BeforeSpeed(double __0) => !ReplaySession.IsPlayingBack || __0 == 0;

    private static bool BeforeSpeedSelection() => !ReplaySession.IsPlayingBack;

    private static void BeforeSimStep()
    {
        if (!ReplaySession.IsPlayingBack)
            return;
        if (Universe.IsAutoWarpActive)
            Universe.AutoWarpStop(resetSimulationSpeed: false);
        if (Universe.GetSimulationSpeed() != 0)
            Universe.SetSimulationSpeed(0, alert: false);
    }

    private static bool BeforeVehicleInput() => !ReplaySession.IsPlayingBack;

    private static bool BeforeSeek() => !ReplaySession.IsPlayingBack;

    // FixedAlert keeps its alerts in the private array FixedAlert.All, and the paused alert is the one
    // whose private name is LStrings.AlertSimulationPaused. The instance is looked up once; when the
    // fields are missing, the lookup finds nothing and the alert shows as in stock.
    private static FixedAlert? _pausedAlert;
    private static bool _pausedAlertLooked;

    private static bool BeforeFixedAlert(FixedAlert __instance, ref bool __result)
    {
        if (!ReplaySession.IsPlayingBack)
            return true;
        if (!_pausedAlertLooked)
        {
            _pausedAlertLooked = true;
            _pausedAlert = FindPausedAlert();
        }

        if (!ReferenceEquals(__instance, _pausedAlert))
            return true;
        __result = false;
        return false;
    }

    private static FixedAlert? FindPausedAlert()
    {
        try
        {
            FieldInfo? all = AccessTools.Field(typeof(FixedAlert), "All");
            FieldInfo? name = AccessTools.Field(typeof(FixedAlert), "_displayName");
            // The mod does not reference the localization assembly, so the name is read as an object.
            object? paused = AccessTools.Field(typeof(LStrings), "AlertSimulationPaused")?.GetValue(null);
            if (all?.GetValue(null) is Array alerts && name != null && paused != null)
            {
                foreach (object? alert in alerts)
                {
                    if (alert is FixedAlert fixedAlert && Equals(name.GetValue(fixedAlert), paused))
                        return fixedAlert;
                }
            }

            DefaultCategory.Log.Warning("[CineCam] The replay shows the stock alert \"Simulation paused\", because the alert could not be found.");
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Warning($"[CineCam] The replay shows the stock alert \"Simulation paused\": {ex.Message}");
        }

        return null;
    }

    // The same value that ChannelWrapper.HandleSimSpeedPitch computes, with the replay rate in place of the sim speed.
    private static bool BeforeSoundPitch(ChannelWrapper __instance)
    {
        if (ReplaySound.Current is not { } sound || !sound.Owns(__instance))
            return true;
        __instance.SimPitchMultiplier = ReplaySoundMath.SpeedPitch((SoundSpeedRule)(int)__instance.SoundReference.SimSpeedRule, sound.Rate);
        return false;
    }

    // A null parent makes VolumetricTrailRenderer.PrepareAndRenderShadowCascades prepare no
    // trail frame, so RenderWithUpscaling draws no plume trail and no explosion volume.
    private static void BeforeTrails(IViewport __1, ref Celestial? __2)
    {
        if (ReplaySession.IsPlayingBack && ReferenceEquals(__1, Program.MainViewport))
            __2 = null;
    }

    // The same early return that the particle methods take when the particle setting is off.
    private static bool BeforeParticles(IViewport __1) =>
        !ReplaySession.IsPlayingBack || !ReferenceEquals(__1, Program.MainViewport);
}
