using System.Reflection;
using CineCam.Core;
using HarmonyLib;
using KSA;
using KSA.Atmosphere.Rendering;

namespace CineCam.Features.Replay;

internal sealed class ReplayCloudState
{
    private static readonly FieldInfo?[] Fields = new[]
    {
        "_noiseScroll", "_noiseScrollAtFrameStart", "_noiseFrameMovementInCloudSpace",
        "_twoDimensionalFlowMapPhase", "_volumetricsFlowMapPhase",
    }.Select(name => AccessTools.Field(typeof(CloudLayerRenderData), name)).ToArray();
    private static readonly FieldInfo? CommonFrame = AccessTools.Field(typeof(CloudLayerRenderData), "_lastCommonFrameUpdate");
    private static readonly FieldInfo? VolumetricsFrame = AccessTools.Field(typeof(CloudLayerRenderData), "_lastVolumetricsFrameUpdate");
    private readonly Dictionary<CloudLayerRenderData, object?[]> _saved = new(ReferenceEqualityComparer.Instance);
    private bool _failed;

    public bool Available => !_failed && Fields.All(member => member != null) && CommonFrame != null && VolumetricsFrame != null;

    public void Capture(CloudLayerRenderData layer)
    {
        if (!Available || _saved.ContainsKey(layer)) return;
        try
        {
            _saved.Add(layer, Fields.Select(field => field!.GetValue(layer)).ToArray());
        }
        catch (Exception ex)
        {
            _failed = true;
            LogHelper.WarnOnce("cloud-state", $"[CineCam] Replay cloud movement is disabled: {ex.Message}");
        }
    }

    public void Restore()
    {
        foreach ((CloudLayerRenderData layer, object?[] values) in _saved)
        {
            try
            {
                for (int i = 0; i < Fields.Length; i++) Fields[i]!.SetValue(layer, values[i]);
                CommonFrame!.SetValue(layer, ulong.MaxValue);
                VolumetricsFrame!.SetValue(layer, ulong.MaxValue);
            }
            catch (Exception ex)
            {
                LogHelper.WarnOnce("cloud-state-restore", $"[CineCam] The live cloud state could not be restored: {ex.Message}");
            }
        }
        _saved.Clear();
    }
}

internal sealed class ReplayStormCache
{
    // StormState.Get has one cached time per body. Compute bypasses that cache, so a replay does
    // not replace the live sample used by SpaceWeatherWindow.DrawContent.
    private static readonly MethodInfo? Compute = AccessTools.DeclaredMethod(typeof(StormState), "Compute",
        [typeof(Celestial), typeof(double), typeof(double), typeof(StormEffects).MakeByRefType()]);
    private readonly Dictionary<Celestial, (double Seconds, Storm Value)> _cache = new(ReferenceEqualityComparer.Instance);

    public Storm Get(Celestial body, double seconds)
    {
        if (_cache.TryGetValue(body, out var sample) && sample.Seconds == seconds) return sample.Value;
        if (Compute == null)
        {
            LogHelper.WarnOnce("replay-storm", "[CineCam] The aurora keeps the live storm state because StormState.Compute is unavailable.");
            return StormState.Get(body, Universe.GetElapsedSeconds());
        }
        Storm value = (Storm)Compute.Invoke(null, [body, seconds, seconds, null])!;
        _cache[body] = (seconds, value);
        return value;
    }
}
