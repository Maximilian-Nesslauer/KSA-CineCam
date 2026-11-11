using Brutal.Numerics;
using CineCam.Director.Replay;
using KSA;

namespace CineCam.Features.Replay;

/// <summary>
/// The module states that change the look of a part tree. The recorder reads them from the live
/// tree and the replay writes them into a decoded tree, with the channels in the part order of the tree.
/// </summary>
internal sealed class ReplayPartState
{
    private readonly PartTree _tree;
    private readonly List<Binding> _bindings = [];
    private readonly List<string> _kinds = [];
    private float[] _applied = [];
    private float[] _values = [];

    private ReplayPartState(PartTree tree)
    {
        _tree = tree;
        var iterator = new PartTreeIterator(tree.Root);
        while (iterator.GetNextNode() is { } part)
            Visit(part);
        foreach (Binding binding in _bindings)
        {
            binding.Offset = _kinds.Count;
            _kinds.AddRange(binding.Kinds);
        }
        Key = ReplayPartKey.Of(_bindings.SelectMany(binding => binding.Kinds.Select(kind => (binding.PartId, kind))));
        _values = new float[_kinds.Count];
        _applied = Enumerable.Repeat(float.NaN, _kinds.Count).ToArray();
    }

    public IReadOnlyList<string> Kinds => _kinds;

    /// <summary>The <see cref="ReplayPartKey"/> of the parts that own the channels.</summary>
    public uint Key { get; }

    public static ReplayPartState Of(PartTree tree) => new(tree);

    /// <summary>The part of the first module whose state the last <see cref="Read"/> did not find, or null.</summary>
    public string? Missed { get; private set; }

    /// <summary>
    /// Reads the state of the live tree into the values, in the order of <see cref="Kinds"/>. A module
    /// whose state is not found keeps its last value, see <see cref="Missed"/>.
    /// </summary>
    public ReadOnlySpan<float> Read()
    {
        Missed = null;
        foreach (Binding binding in _bindings)
        {
            if (!binding.Read(_tree, _values.AsSpan(binding.Offset, binding.Kinds.Length)))
                Missed ??= binding.PartId;
        }
        return _values;
    }

    /// <summary>
    /// True when the recorded channels have the kinds of this tree in the same order, and the parts
    /// that own them are the same when the segment has their key.
    /// </summary>
    public bool Matches(ReplaySegment segment) =>
        segment.Channels is { } channels && channels.Count == _kinds.Count
        && channels.Select(channel => channel.Kind).SequenceEqual(_kinds)
        && (segment.PartKey is not uint key || key == Key);

    /// <summary>
    /// A binding whose values did not change is skipped, because a new transform makes the render
    /// data rebuild its matrices. The sun trackers come before the animations, as in
    /// VehicleUpdateState.UpdateModules.
    /// </summary>
    public void Apply(List<ReplayChannel> channels, double time)
    {
        for (int i = 0; i < _values.Length; i++)
            _values[i] = channels[i].ValueAt(time);
        for (int phase = 0; phase < 2; phase++)
        {
            foreach (Binding binding in _bindings)
            {
                if (binding.Phase != phase)
                    continue;
                Span<float> applied = _applied.AsSpan(binding.Offset, binding.Kinds.Length);
                ReadOnlySpan<float> values = _values.AsSpan(binding.Offset, binding.Kinds.Length);
                if (values.SequenceEqual(applied))
                    continue;
                binding.Apply(_tree, values);
                values.CopyTo(applied);
            }
        }
    }

    private void Visit(Part part)
    {
        foreach (SolarTracker module in part.Modules.Get<SolarTracker>())
            _bindings.Add(new TrackerBinding(module));
        foreach (KeyframeAnimationModule module in part.Modules.Get<KeyframeAnimationModule>())
            _bindings.Add(new AnimationBinding(module));
        foreach (Gimbal module in part.Modules.Get<Gimbal>())
            _bindings.Add(new GimbalBinding(module));
        foreach (FxTemperature module in part.Modules.Get<FxTemperature>())
            _bindings.Add(new HeatBinding(module));
        foreach (FxWetness module in part.Modules.Get<FxWetness>())
            _bindings.Add(new WetnessBinding(module));
        foreach (PowerConsumer module in part.Modules.Get<PowerConsumer>())
        {
            if (module.LightSwitch)
                _bindings.Add(new LightBinding(module));
        }
        foreach (Part subpart in part.SubParts)
            Visit(subpart);
    }

    // A state of a module, or false when the module is not in the state list of the tree, for
    // example after it left with a split.
    private static bool TryState<TModule, TState>(PartTree tree, TModule module, out TState state)
        where TModule : ModuleStateful<TModule, TState, EmptyStruct, EmptyStruct>, IDisposable
        where TState : unmanaged
    {
        state = default;
        if (!tree.States.TryGetTypeList(out ModuleStateful<TModule, TState, EmptyStruct, EmptyStruct>.StateList list))
            return false;
        int index = module.StatesIdx;
        if ((uint)index >= (uint)list.NumModules || !ReferenceEquals(list.GetModuleByIdx(index), module))
            return false;
        state = list.GetStateByIdx(index);
        return true;
    }

    private delegate void Change<TState>(ref TState state);

    private static void SetState<TModule, TState>(PartTree tree, TModule module, Change<TState> change)
        where TModule : ModuleStateful<TModule, TState, EmptyStruct, EmptyStruct>, IDisposable
        where TState : unmanaged
    {
        if (!tree.States.TryGetTypeList(out ModuleStateful<TModule, TState, EmptyStruct, EmptyStruct>.StateList list))
            return;
        int index = module.StatesIdx;
        if ((uint)index >= (uint)list.NumModules || !ReferenceEquals(list.GetModuleByIdx(index), module))
            return;
        change(ref list.GetModuleAndAllMutableStatesForInitializationByIdx(index).State);
    }

    private abstract class Binding(Part part, int phase, params string[] kinds)
    {
        public string PartId { get; } = part.Id ?? "";
        public string[] Kinds { get; } = kinds;
        public int Phase { get; } = phase;
        public int Offset { get; set; }
        public abstract bool Read(PartTree tree, Span<float> values);
        public abstract void Apply(PartTree tree, ReadOnlySpan<float> values);
    }

    // KeyframeAnimationModule.UpdateModules applies the sun tracking rotation of the extension only
    // while the animation moves or stays deployed, so the recorded angle is 0 when no rotation applies.
    private sealed class AnimationBinding(KeyframeAnimationModule module) : Binding(module.Parent, 1, module.Extension is SolarTrackingExtension
        ? [ReplayChannelKind.Animation, ReplayChannelKind.AnimationTracking]
        : [ReplayChannelKind.Animation])
    {
        public override bool Read(PartTree tree, Span<float> values)
        {
            if (!TryState(tree, module, out KeyframeAnimationModule.State state))
                return false;
            values[0] = state.TimeCurrent;
            if (values.Length > 1 && module.Extension is SolarTrackingExtension tracking)
            {
                bool applies = module.TimeGoal != state.TimeCurrent
                    || KeyframeAnimationModule.DeriveDeploymentState(module.TimeGoal, state.TimeCurrent) == KeyframeAnimationModule.DeploymentState.Deployed;
                values[1] = applies && tracking.Rotation.HasValue ? (float)tracking.CurrentAngle : 0f;
            }
            return true;
        }

        public override void Apply(PartTree tree, ReadOnlySpan<float> values)
        {
            float angle = values.Length > 1 ? values[1] : 0f;
            module.ApplyAnimationTransforms(values[0], angle != 0f ? doubleQuat.CreateFromAxisAngle(double3.UnitX, angle) : null);
        }
    }

    // The same rotation of the sub parts as SolarTracker.UpdateModules.
    private sealed class TrackerBinding(SolarTracker module) : Binding(module.Parent, 0, ReplayChannelKind.Tracker)
    {
        public override bool Read(PartTree tree, Span<float> values)
        {
            if (!TryState(tree, module, out SolarTrackerState state))
                return false;
            values[0] = (float)state.CurrentAngle;
            return true;
        }

        public override void Apply(PartTree tree, ReadOnlySpan<float> values)
        {
            doubleQuat rotation = doubleQuat.CreateFromAxisAngle(double3.UnitX, values[0]);
            HashSet<string> excluded = module.ExcludeSubPartIds;
            ReadOnlySpan<Part> subparts = module.Parent.FullPart.SubParts;
            for (int i = 0; i < subparts.Length; i++)
            {
                Part part = subparts[i];
                if (excluded.Count > 0 && excluded.Contains(part.Id))
                    continue;
                part.PositionParentAsmb = part.PositionParentAsmbSafe.Transform(rotation);
                part.Asmb2ParentAsmb = doubleQuat.Concatenate(part.Asmb2ParentAsmbSafe, rotation);
            }
        }
    }

    // PartTreeRenderData reads GimbalState.Gimbal2Asmb in every frame for the dynamic part models.
    private sealed class GimbalBinding(Gimbal module) : Binding(module.Parent, 0, ReplayChannelKind.GimbalY, ReplayChannelKind.GimbalZ)
    {
        public override bool Read(PartTree tree, Span<float> values)
        {
            if (!TryState(tree, module, out GimbalState state))
                return false;
            values[0] = state.AngleY;
            values[1] = state.AngleZ;
            return true;
        }

        public override void Apply(PartTree tree, ReadOnlySpan<float> values)
        {
            float y = values[0], z = values[1];
            SetState(tree, module, (ref GimbalState state) =>
            {
                state.AngleY = y;
                state.AngleZ = z;
                state.Gimbal2Asmb = Gimbal.ComputeGimbal2Asmb(y, z);
            });
        }
    }

    // PartTreeRenderData reads the heat state in every frame for the dynamic part models.
    private sealed class HeatBinding(FxTemperature module) : Binding(module.Parent, 0, ReplayChannelKind.Heat, ReplayChannelKind.ThinFilm)
    {
        public override bool Read(PartTree tree, Span<float> values)
        {
            if (!TryState(tree, module, out FxTemperatureState state))
                return false;
            values[0] = state.EmissivityFraction;
            values[1] = state.ThinFilmFraction;
            return true;
        }

        public override void Apply(PartTree tree, ReadOnlySpan<float> values)
        {
            float heat = values[0], film = values[1];
            SetState(tree, module, (ref FxTemperatureState state) =>
            {
                state.EmissivityFraction = heat;
                state.ThinFilmFraction = film;
            });
        }
    }

    // PartTreeRenderData compares the wetness of the parts in every frame and rewrites the changed ones.
    private sealed class WetnessBinding(FxWetness module) : Binding(module.Parent, 0, ReplayChannelKind.Wetness)
    {
        public override bool Read(PartTree tree, Span<float> values)
        {
            if (!TryState(tree, module, out FxWetnessState state))
                return false;
            values[0] = state.WetnessFraction;
            return true;
        }

        public override void Apply(PartTree tree, ReadOnlySpan<float> values)
        {
            float wetness = values[0];
            SetState(tree, module, (ref FxWetnessState state) => state.WetnessFraction = wetness);
        }
    }

    // Part.IsLightSwitchedOff reads LightIsActive and the Active state of the switch. The lights of
    // the part and the emissive flag of its models follow it in every frame.
    private sealed class LightBinding(PowerConsumer module) : Binding(module.Parent, 0, ReplayChannelKind.Light)
    {
        public override bool Read(PartTree tree, Span<float> values)
        {
            if (!TryState(tree, module, out PowerConsumerState state))
                return false;
            values[0] = module.LightIsActive && state.Active ? 1f : 0f;
            return true;
        }

        public override void Apply(PartTree tree, ReadOnlySpan<float> values)
        {
            bool on = values[0] > 0.5f;
            module.LightIsActive = on;
            SetState(tree, module, (ref PowerConsumerState state) => state.Active = on);
        }
    }
}
