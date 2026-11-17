using Brutal.Logging;
using Brutal.Numerics;
using CineCam.Core;
using CineCam.Director.Replay;
using KSA;

namespace CineCam.Features.Replay;

/// <summary>
/// The kittens of a replay: the crew in the seats of the recorded vehicles and the kittens on EVA. The game
/// draws them into the mesh buckets that Program.RenderViewport clears before Vehicle.UpdateSeatedCrewRenderData,
/// so the replay draws them from that call. A seated kitten is the KittenRenderable of the IVASeat of the
/// decoded part tree, and a kitten on EVA is a KittenRenderable of the replay.
/// </summary>
internal sealed class ReplayCrew : IDisposable
{
    private sealed class Pose
    {
        public KittenRenderable? Renderable;
        public AnimatedRenderable? Model;
        public BoneAnimRuntime? Runtime;
        public KittenClock Clock = new();

        // KittenRenderable picks the clip of the pose in its first draw, so the clock starts after it.
        public bool Drawn;
    }

    private readonly Dictionary<IVASeat, Pose> _seats = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, Pose> _kittens = new();
    private readonly HashSet<int> _failedKittens = [];
    private static bool _animationLogged;

    /// <summary>
    /// Disposes the kittens of the seats and actors that the replay does not show at this time, for example
    /// the crew of a segment that ended. A seat makes its kitten again when it shows again.
    /// </summary>
    public void Retain(HashSet<IVASeat> seats, HashSet<int> kittens)
    {
        List<IVASeat>? gone = null;
        foreach (IVASeat seat in _seats.Keys)
        {
            if (!seats.Contains(seat))
                (gone ??= []).Add(seat);
        }
        foreach (IVASeat seat in gone ?? [])
        {
            seat.Dispose();
            _seats.Remove(seat);
        }

        List<int>? left = null;
        foreach ((int id, Pose pose) in _kittens)
        {
            if (kittens.Contains(id))
                continue;
            pose.Renderable?.Dispose();
            (left ??= []).Add(id);
        }
        foreach (int id in left ?? [])
            _kittens.Remove(id);
    }

    /// <summary>
    /// The seated kittens of one recorded vehicle, by the rule of Vehicle.UpdateSeatedCrewRenderData with the
    /// model matrix of the replay. IVASeat.UpdateSeatedRenderable makes the KittenRenderable on its first call.
    /// </summary>
    public void DrawSeats(IViewport viewport, int frameIndex, IVASeat[] seats, in double4x4 matrixAsmb2Ego, double time)
    {
        foreach (IVASeat seat in seats)
        {
            if (!_seats.TryGetValue(seat, out Pose? pose))
                _seats[seat] = pose = new Pose();
            if (!ReferenceEquals(pose.Renderable, seat.Renderable))
                Track(pose, seat.Renderable);
            double step = Advance(pose, time);
            seat.UpdateSeatedRenderable(viewport, frameIndex, Math.Min(step, KittenClock.MaxDampingStep), in matrixAsmb2Ego);
            Settle(pose, step);
        }
    }

    /// <summary>
    /// A kitten on EVA, by the rule of KittenEva.UpdateRenderData: the transform has the rotation of the
    /// vehicle at its assembly origin, and the animation reads the recorded locomotion state. The jump state
    /// stays empty, so a jump shows the pose of the airborne mode.
    /// </summary>
    public void DrawKitten(IViewport viewport, int frameIndex, ReplayActor actor, ReplayKitten? state, in double4x4 matrixAsmb2Ego, double time)
    {
        if (actor.Kitten is not string character || _failedKittens.Contains(actor.Id))
            return;
        if (!_kittens.TryGetValue(actor.Id, out Pose? pose))
        {
            KittenRenderable renderable;
            try
            {
                renderable = new KittenRenderable(character);
            }
            catch (Exception ex)
            {
                _failedKittens.Add(actor.Id);
                DefaultCategory.Log.Warning($"[CineCam] The replay does not show the kitten {actor.Name} on EVA: {ex.Message}");
                return;
            }

            _kittens[actor.Id] = pose = new Pose();
            Track(pose, renderable);
        }

        double step = Advance(pose, time);
        ReplayKitten kitten = state ?? new ReplayKitten((int)LocomotionMode.Mmu, 0, 0, 0, 0, 1);
        var locomotion = new LocomotionState
        {
            Mode = (LocomotionMode)kitten.Mode,
            GroundSpeed = kitten.Speed,
            GravityMagnitude = kitten.Gravity,
            LadderRailT = kitten.Ladder,
            UprightAlignment = kitten.Upright,
        };
        var inputs = new KittenAnimInputs
        {
            SprintHeld = kitten.Has(ReplayKittenFlags.Sprint),
            MoveHeld = kitten.Has(ReplayKittenFlags.Move),
            RcsEnabled = kitten.Has(ReplayKittenFlags.Rcs),
        };
        double3 acceleration = kitten.Motion is { } motion ? new double3(motion[0], motion[1], motion[2]) : double3.Zero;
        double3 angular = kitten.Motion is { } turn ? new double3(turn[3], turn[4], turn[5]) : double3.Zero;
        KittenRenderable drawn = pose.Renderable!;
        drawn.ConditionRung = KittenRenderable.ReadConditionRung(Universe.KittenRoster.Find(actor.Name));
        drawn.UpdateRenderData(viewport, frameIndex, Math.Min(step, KittenClock.MaxDampingStep), float4x4.Pack(in matrixAsmb2Ego),
            acceleration, angular, in locomotion, in inputs);
        Settle(pose, step);
    }

    /// <summary>Disposes every kitten, so the next frame makes them again with a new animation clock.</summary>
    public void Reset()
    {
        foreach (IVASeat seat in _seats.Keys)
            seat.Dispose();
        _seats.Clear();
        foreach (Pose pose in _kittens.Values)
            pose.Renderable?.Dispose();
        _kittens.Clear();
    }

    public void Dispose() => Reset();

    private static void Track(Pose pose, KittenRenderable? renderable)
    {
        pose.Renderable = renderable;
        (pose.Model, pose.Runtime) = renderable != null ? RuntimeOf(renderable) : (null, null);
        pose.Clock = new KittenClock();
        pose.Drawn = false;
    }

    // The game advances the animation by the frame time times the simulation speed, which is 0 in a
    // replay. So the replay advances the animation of the model by the recording time before the draw,
    // at the playback rate that KittenRenderable chose for the clip, and not while KittenRenderable holds
    // the clip (a kitten that stands still on a ladder). The draw then samples it without a step of its own.
    private static double Advance(Pose pose, double time)
    {
        if (pose.Renderable == null || !pose.Drawn)
            return 0.0;
        double step = pose.Clock.Advance(time);
        if (step > 0 && pose.Runtime != null && pose.Model?.FreezeAnimation != true)
            pose.Runtime.Update((float)(step * pose.Renderable.GroundAnimPlaybackRate));
        return step;
    }

    // A draw that changes the clip starts a blend from the pose before, which only a step of time ends. A
    // paused replay gives no step, so the blend ends here and the next frame shows the new clip.
    private static void Settle(Pose pose, double step)
    {
        pose.Drawn = pose.Renderable != null;
        if (step > 0 || pose.Runtime == null || pose.Model?.IsAnimationBlending != true)
            return;
        pose.Runtime.Update(KittenLocomotionTuning.Current.AnimBlendTime + 0.01f);
    }

    // KittenRenderable keeps its model in the private field _characterAvatar, and AnimatedRenderable keeps
    // its animation in the protected field RuntimeAnim. Without them the kittens keep the first pose of
    // their animation.
    private static (AnimatedRenderable? Model, BoneAnimRuntime? Runtime) RuntimeOf(KittenRenderable renderable)
    {
        try
        {
            if (GameReflection.KittenRenderable_CharacterAvatar?.GetValue(renderable) is CharacterAvatar avatar
                && avatar.Core.CharacterModel is { } model
                && GameReflection.AnimatedRenderable_RuntimeAnim?.GetValue(model) is BoneAnimRuntime runtime)
                return (model, runtime);
        }
        catch (Exception ex) when (ex is ArgumentException or FieldAccessException or InvalidCastException)
        {
            LogAnimation(ex.Message);
            return (null, null);
        }

        LogAnimation(UiText.MessageText("text.0d5212e25cf8", "a field of the kitten model was not found"));
        return (null, null);
    }

    private static void LogAnimation(string reason)
    {
        if (_animationLogged)
            return;
        _animationLogged = true;
        DefaultCategory.Log.Warning($"[CineCam] The kittens in the replay do not move, because {reason}.");
    }
}
