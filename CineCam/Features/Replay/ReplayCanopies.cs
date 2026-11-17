using System.Reflection;
using Brutal.Logging;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using RenderCore.Systems;

namespace CineCam.Features.Replay;

/// <summary>
/// The canopies of the parachutes of a decoded part tree. The live game runs a cloth simulation of each canopy
/// (ChuteClothSystem), which needs the live vehicle and moves only with the sim time. The replay gives each canopy
/// the target shape of that simulation instead: the deploy and reef clip of the game at the recorded state, fill and
/// direction, as ChuteClothSystem.SnapshotAndKick does in its mode without physics at a high time warp. So the
/// canopy shows the same at every time of the replay and after a seek, without the flutter of the live cloth.
/// </summary>
internal sealed class ReplayCanopies
{
    private sealed class Canopy(Parachute chute)
    {
        public Parachute Chute { get; } = chute;
        public ChuteClothState Cloth { get; } = new();
        public float3[] Clip { get; } = new float3[ChuteClothSystem.Topology.CanopyNodeCount];
        public float[] ClipScales { get; } = Ones();
        public float[] Scales { get; } = Ones();
        public object? Cache { get; set; }
        public float Diameter { get; set; } = float.NaN;
        public float Riser { get; set; } = float.NaN;
    }

    // ChuteClothSystem.UpdateAttractorTargets(ChuteClothState, Parachute, float3[], float[], float, ref ClipFillCache)
    // fills the target shape from the state of a parachute. Its last parameter is a private struct, so the
    // method runs through reflection.
    private static readonly MethodInfo? Attractors = Find();

    // The game snaps the canopy orientation to its target with this step when it adopts a canopy.
    private const float SnapStep = 999f;

    // The bone arrays of ChuteRenderable and ChuteClothSystem.ApplyResults.
    private const int BoneScaleCount = 34;

    private readonly Canopy[] _canopies;
    private bool _failed;

    public ReplayCanopies(IEnumerable<Parachute> chutes) => _canopies = chutes.Select(chute => new Canopy(chute)).ToArray();

    public static bool Available => Attractors != null;

    public int Count => _canopies.Length;

    /// <summary>
    /// Takes the decoded parachutes out of ChuteClothSystem. Parachute.ApplySaveData asks the system for a cloth of
    /// a deployed parachute, and the system would then write its shape into the decoded parachute in every frame.
    /// </summary>
    public static void Release(PartTree tree)
    {
        foreach (Parachute chute in tree.Modules.Get<Parachute>())
            ChuteClothSystem.ReleaseImmediate(chute);
    }

    /// <summary>
    /// Writes the shape of each canopy into its parachute for the recorded state that the parachutes have now. The
    /// line render of PartTree.UpdateRenderData and <see cref="Draw"/> read it. A parachute without a canopy gets
    /// none, so its lines and canopy do not show.
    /// </summary>
    public void Shape()
    {
        if (_failed)
            return;
        try
        {
            foreach (Canopy canopy in _canopies)
                Shape(canopy);
        }
        catch (Exception ex)
        {
            _failed = true;
            Clear();
            DefaultCategory.Log.Warning($"[CineCam] The replay shows no parachute canopies after an error: {ex.GetBaseException().Message}");
        }
    }

    private static void Shape(Canopy canopy)
    {
        Parachute chute = canopy.Chute;
        if (chute.State is not (ChuteState.LineStretch or ChuteState.Reefed or ChuteState.Disreefing or ChuteState.Full))
        {
            Clear(chute);
            return;
        }

        ChuteClothTopology topology = ChuteClothSystem.Topology;
        ChuteClothState cloth = canopy.Cloth;
        float diameter = chute.Tuning.DiameterM;
        float riser = chute.RiserLength;
        if (diameter != canopy.Diameter || riser != canopy.Riser)
        {
            cloth.Reset(topology, diameter, riser);
            canopy.Diameter = diameter;
            canopy.Riser = riser;
        }

        cloth.Tuning = ChuteClothTuning.Current;
        object?[] arguments = [cloth, chute, canopy.Clip, canopy.ClipScales, SnapStep, canopy.Cache];
        bool clip = Attractors!.Invoke(null, arguments) is true;
        canopy.Cache = arguments[5];
        ChuteClothForces.ReseedFromAttractors(topology, cloth);
        ChuteCanopyBones.ComputeDeltas(topology, cloth.Positions, cloth.BoneDeltas);

        // ChuteClothSystem.ApplyResults uses the bone scales of the clip only while the canopy opens.
        bool opening = chute.State is ChuteState.LineStretch or ChuteState.Reefed or ChuteState.Disreefing;
        chute.ClothPositionsFront = cloth.Positions;
        chute.ClothBoneDeltasFront = cloth.BoneDeltas;
        chute.ClothBoneScalesFront = clip && opening ? canopy.ClipScales : canopy.Scales;
        chute.ClothLineSamplesValid = false;
    }

    /// <summary>Draws the canopies that <see cref="Shape"/> gave a shape, as Vehicle.UpdateParachuteRenderData does.</summary>
    public void Draw(ViewHandle view, double deltaTime, in double4x4 asmb2Ego)
    {
        if (_failed)
            return;
        try
        {
            foreach (Canopy canopy in _canopies)
            {
                if (canopy.Chute.ClothPositionsFront != null)
                    canopy.Chute.DrawCanopy(view, deltaTime, in asmb2Ego);
            }
        }
        catch (Exception ex)
        {
            _failed = true;
            Clear();
            DefaultCategory.Log.Warning($"[CineCam] The replay shows no parachute canopies after an error: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>Removes the shapes, for a segment that no longer shows.</summary>
    public void Clear()
    {
        foreach (Canopy canopy in _canopies)
            Clear(canopy.Chute);
    }

    private static void Clear(Parachute chute)
    {
        chute.ClothPositionsFront = null;
        chute.ClothBoneDeltasFront = null;
        chute.ClothBoneScalesFront = null;
        chute.ClothLineSamplesValid = false;
    }

    private static float[] Ones()
    {
        float[] ones = new float[BoneScaleCount];
        Array.Fill(ones, 1f);
        return ones;
    }

    private static MethodInfo? Find()
    {
        MethodInfo? method = AccessTools.Method(typeof(ChuteClothSystem), "UpdateAttractorTargets");
        ParameterInfo[]? parameters = method?.GetParameters();
        if (method != null && method.ReturnType == typeof(bool) && parameters is { Length: 6 }
            && parameters[0].ParameterType == typeof(ChuteClothState) && parameters[1].ParameterType == typeof(Parachute)
            && parameters[2].ParameterType == typeof(float3[]) && parameters[3].ParameterType == typeof(float[])
            && parameters[4].ParameterType == typeof(float) && parameters[5].ParameterType.IsByRef)
            return method;
        DefaultCategory.Log.Warning("[CineCam] The replay shows no parachute canopies, because ChuteClothSystem.UpdateAttractorTargets is missing or changed.");
        return null;
    }
}
