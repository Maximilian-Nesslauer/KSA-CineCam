using Brutal.Numerics;
using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Features.Takes;
using KSA;

namespace CineCam.Features.Replay;

internal readonly record struct ReplayCameraTarget(ReplaySegment Segment, ReplaySample Sample, PartTree Parts,
    double3 Center, double Radius);

// The camera follows a recorded vehicle by moving with its recorded center each frame. The take
// camera keeps following the vehicle of the take, so it stays with the vehicle while no shot runs.
internal sealed class ReplayCamera(Celestial body, Func<int, ReplayCameraTarget?> sample)
{
    private static Camera Camera => Program.MainViewport.BaseCamera;
    private readonly UpStabilizer _up = new();
    private V3 _lastForwardSite = V3.UnitX;
    private double3? _lastFollowPosition;

    public int? FollowActorId { get; private set; }
    // In degrees, because Camera.SetFieldOfView takes degrees and Camera.GetFieldOfView gives radians.
    public double FieldOfView => Camera.GetFieldOfView() * (180.0 / Math.PI);

    // Follows a vehicle from a place beside it, or follows nothing for a null id.
    public void Follow(int? actorId)
    {
        FollowActorId = null;
        _lastFollowPosition = null;
        if (actorId is not int id || sample(id) is not { } pose)
            return;

        FollowActorId = id;
        double3 target = TargetCenter(pose);
        double3 up = (target - body.GetPositionEcl()).Normalized();
        double3 direction = double3.Cross(up, double3.UnitZ);
        if (direction.LengthSquared() < 0.01)
            direction = double3.Cross(up, double3.UnitX);
        Camera.PositionEcl = target + (direction.Normalized() + up * 0.25) * (3.5 * pose.Radius);
        Camera.LocalRotation = Camera.LookAtRotation((target - Camera.PositionEcl).Normalized(), up);
        Program.MainViewport.FlyController.CacheOffset();
        _lastFollowPosition = target;
    }

    // Follows a vehicle from where the camera is now.
    public void Keep(int actorId)
    {
        if (sample(actorId) is not { } pose)
            return;
        FollowActorId = actorId;
        _lastFollowPosition = TargetCenter(pose);
    }

    public void UpdateFollow()
    {
        if (FollowActorId is not int id)
            return;
        if (sample(id) is not { } followed)
        {
            FollowActorId = null;
            _lastFollowPosition = null;
            return;
        }
        double3 position = TargetCenter(followed);
        if (_lastFollowPosition is double3 last)
            Camera.PositionEcl += position - last;
        _lastFollowPosition = position;
    }

    // Writes a pose of the site frame into the camera, as CameraRig.Apply does in a live take. The
    // free controller keeps the rotation because FlyController.CacheOffset stores it again.
    public void Apply(SiteFrame site, CameraPose pose)
    {
        V3 forwardSite = pose.LookAt - pose.Position;
        if (forwardSite.Length < 1e-6)
            forwardSite = _lastForwardSite;
        forwardSite = forwardSite.NormalizedOr(_lastForwardSite);
        _lastForwardSite = forwardSite;
        V3 upSite = _up.Stabilize(forwardSite, pose.Up);
        Camera.PositionEcl = site.ToEcl(pose.Position);
        Camera.LocalRotation = Camera.LookAtRotation(site.DirectionToEcl(forwardSite).Normalized(), site.DirectionToEcl(upSite).Normalized());
        SetFieldOfView(Math.Clamp(pose.Fov, 1.0, 150.0));
        Program.MainViewport.FlyController.CacheOffset();
    }

    public void SetFieldOfView(double fov)
    {
        if (Math.Abs(FieldOfView - fov) > 1e-3)
            Camera.SetFieldOfView((float)fov);
    }

    // The pose of a saved view in the body-fixed frame. The forward vector is Camera.ForwardView
    // turned by the rotation, as in Camera.GetForwardEcl. Null when the vehicle or the mount part is
    // not in the recording at that time.
    public static (V3 Position, V3 Forward)? ViewPoseCcf(ReplayView view, ReplayCameraTarget? target)
    {
        double3 position = Vector(view.Position);
        doubleQuat rotation = Quat(view.Rotation);
        if (view.ActorId.HasValue)
        {
            if (target is not { } pose)
                return null;
            if (view.Mount is { } mount)
            {
                if (mount.SegmentStart != pose.Segment.Start || mount.PartIndex < 0 || mount.PartIndex >= pose.Parts.Parts.Length)
                    return null;
                Part part = pose.Parts.Parts[mount.PartIndex];
                position = part.PositionVehicleAsmb + position.Transform(part.Asmb2VehicleAsmb);
                rotation = doubleQuat.Concatenate(rotation, part.Asmb2VehicleAsmb);
            }
            doubleQuat actorRotation = Quat(pose.Sample.Rotation);
            position = Vector(pose.Sample.Position) + position.Transform(actorRotation);
            rotation = doubleQuat.Concatenate(rotation, actorRotation);
        }

        double3 forward = Camera.ForwardView.Transform(rotation);
        return (FlightRecorder.ToV3(position), FlightRecorder.ToV3(forward));
    }

    private double3 TargetCenter(ReplayCameraTarget target)
    {
        (double3 origin, doubleQuat orientation) = WorldPose(body, target.Sample);
        return origin + target.Center.Transform(orientation);
    }

    private static double3 ToEcl(Celestial body, double3 position) =>
        body.GetPositionEcl() + position.Transform(body.GetCcf2Cce());

    public static (double3 Origin, doubleQuat Rotation) WorldPose(Celestial body, ReplaySample sample) =>
        (ToEcl(body, Vector(sample.Position)), doubleQuat.Concatenate(Quat(sample.Rotation), body.GetCcf2Cce()));

    public static double3 Vector(V3 value) => new(value.X, value.Y, value.Z);
    private static doubleQuat Quat(ReplayRotation value) => new(value.X, value.Y, value.Z, value.W);
}
