namespace CineCam.Director;

public enum PoseFrame { Site, Vehicle }

/// <summary>
/// A camera pose in one frame. <see cref="Up"/> is the direction that points up in the picture.
/// <see cref="Fov"/> is the vertical field of view in degrees.
/// </summary>
public readonly record struct CameraPose(PoseFrame Frame, V3 Position, V3 LookAt, V3 Up, double Fov);

/// <summary>
/// What the pose math needs to know about the world at one moment. Everything is in the launch
/// site frame (+X up, +Y east, +Z north) except <see cref="PartPosition"/>.
/// </summary>
public sealed class PoseInputs
{
    /// <summary>The vehicle frame in the site frame. The origin is the base of the vehicle and +X points to the nose.</summary>
    public Basis3 Vehicle { get; set; } = Basis3.Identity;

    /// <summary>Length of the vehicle along its axis in meters. Zero when unknown.</summary>
    public double VehicleHeight { get; set; }

    /// <summary>The position of the named part in the vehicle frame, when the vehicle has it.</summary>
    public V3? PartPosition { get; set; }

    /// <summary>
    /// Distance from the camera to the vehicle at the start of a track shot. A value of 0 or
    /// less means the current distance, so the field of view stays at the shot value.
    /// </summary>
    public double TrackReferenceDistance { get; set; }

    /// <summary>The center of the body that the vehicle is over, in the site frame. Null when unknown, then the site axes stand for the local horizon.</summary>
    public V3? BodyCentre { get; set; }

    /// <summary>The direction of the rotation axis of the body (north) in the site frame. Null when unknown.</summary>
    public V3? Pole { get; set; }

    /// <summary>Width of the vehicle across its axis in meters. Zero when unknown.</summary>
    public double VehicleWidth { get; set; }

    /// <summary>The horizontal direction of flight in the site frame, or null when unknown, then the flight frame points north.</summary>
    public V3? FlightHeading { get; set; }
}

/// <summary>
/// The local horizon frame at a point: +X away from the body centre, +Y east and +Z north. It has
/// the axes of the launch site frame, but it moves with the point, so it stays right over the
/// whole flight.
/// </summary>
public static class LocalHorizon
{
    /// <summary>
    /// Without a body centre the site axes are returned, which is right near the launch site. East is
    /// north cross up, which makes (up, east, north) right handed like the site frame.
    /// </summary>
    public static Basis3 Frame(V3 origin, V3? bodyCentre, V3? pole)
    {
        if (bodyCentre == null)
            return new Basis3(origin, V3.UnitX, V3.UnitY, V3.UnitZ);

        V3 up = (origin - bodyCentre.Value).NormalizedOr(V3.UnitX);
        V3 north = NorthOf(up, pole ?? V3.UnitZ);
        return new Basis3(origin, up, V3.Cross(north, up), north);
    }

    /// <summary>The unit direction away from the body centre, or the site up when the centre is unknown.</summary>
    public static V3 Up(V3 position, V3? bodyCentre) =>
        bodyCentre == null ? V3.UnitX : (position - bodyCentre.Value).NormalizedOr(V3.UnitX);

    /// <summary>The local frame turned about +X so that +Z points along the horizontal part of <paramref name="heading"/>.</summary>
    public static Basis3 FlightFrame(V3 origin, V3? bodyCentre, V3? pole, V3? heading)
    {
        Basis3 local = Frame(origin, bodyCentre, pole);
        V3 up = local.X;
        V3 forward = local.Z;
        if (heading.HasValue)
            forward = (heading.Value - up * V3.Dot(heading.Value, up)).NormalizedOr(local.Z);
        return new Basis3(origin, up, V3.Cross(forward, up), forward);
    }

    // Over a pole of the body the projected pole vanishes and any horizontal direction works.
    private static V3 NorthOf(V3 up, V3 pole)
    {
        V3 north = pole - up * V3.Dot(pole, up);
        if (north.Length > 1e-6)
            return north / north.Length;
        V3 axis = Math.Abs(up.Z) < 0.9 ? V3.UnitZ : V3.UnitY;
        return (axis - up * V3.Dot(axis, up)).NormalizedOr(V3.UnitY);
    }
}

public static class PoseMath
{
    /// <summary>
    /// Orbit, crane and tripod poses are in the frame of the shot target, and track poses in the site
    /// frame. Chase and part poses are in the vehicle frame, so the caller can smooth the vehicle turn
    /// before it maps the pose.
    /// </summary>
    public static CameraPose Evaluate(Shot shot, double progress, PoseInputs inputs)
    {
        CameraPose pose = EvaluateLevel(shot, progress, inputs);
        double roll = RollOf(shot, progress);
        return roll == 0.0 ? pose : pose with { Up = RollUp(pose.Position, pose.LookAt, pose.Up, roll) };
    }

    /// <summary>The roll in degrees. A path adds the roll of its keyframes to the roll of the shot.</summary>
    public static double RollOf(Shot shot, double progress) =>
        shot.Type == ShotType.Path && shot.Points.Count > 0
            ? shot.Roll + PathMath.Evaluate(shot.Points, progress, shot.Fov).Roll
            : shot.Roll;

    /// <summary>The frame that the position of a shot moves in, ignoring how the pose is mapped to the site frame.</summary>
    public enum MotionFrame { Site, Vehicle, Local }

    /// <summary>A track shot is a site frame shot, because only its aim follows the vehicle.</summary>
    public static MotionFrame MotionFrameOf(Shot shot)
    {
        if (shot.UsesHorizonFrame)
            return MotionFrame.Local;
        return shot.Type switch
        {
            ShotType.Track => MotionFrame.Site,
            ShotType.Chase or ShotType.Part => MotionFrame.Vehicle,
            _ => shot.Target == ShotTarget.Site ? MotionFrame.Site : MotionFrame.Vehicle,
        };
    }

    /// <summary>A positive roll turns the camera clockwise as seen from behind it, so the picture turns counterclockwise.</summary>
    public static V3 RollUp(V3 position, V3 lookAt, V3 up, double rollDeg)
    {
        V3 forward = (lookAt - position).NormalizedOr(V3.UnitZ);
        V3 level = (up - forward * V3.Dot(forward, up)).NormalizedOr(up);
        double angle = DegToRad(rollDeg);
        // Rodrigues' rotation, simplified because the level up is perpendicular to the view direction.
        return level * Math.Cos(angle) + V3.Cross(forward, level) * Math.Sin(angle);
    }

    /// <summary>
    /// <paramref name="smoothed"/> is the vehicle frame after the turn smoothing. A shot with a horizon
    /// frame has its offset and look point in the local horizon frame at the vehicle or the part.
    /// </summary>
    public static CameraPose EvaluateSite(Shot shot, double progress, PoseInputs inputs, Basis3 smoothed)
    {
        CameraPose pose;
        Basis3? horizon = null;
        if (shot.UsesHorizonFrame)
        {
            progress = Math.Clamp(progress, 0.0, 1.0);
            // The up of a local shot is the local up at the vehicle unless "horizon" asks for the
            // up at the camera.
            V3 origin = smoothed.Origin;
            if (shot.Type == ShotType.Part)
                origin = smoothed.ToParent(inputs.PartPosition ?? V3.Zero);
            Basis3 local = shot.UsesFlightFrame
                ? LocalHorizon.FlightFrame(origin, inputs.BodyCentre, inputs.Pole, inputs.FlightHeading)
                : LocalHorizon.Frame(origin, inputs.BodyCentre, inputs.Pole);
            V3 offset = shot.Offset, aim = shot.LookAt;
            double fov = shot.Fov;
            if (shot.Type == ShotType.Path)
            {
                PathSample sample = PathMath.Evaluate(shot.Points, progress, shot.Fov);
                offset = sample.Position;
                aim = sample.LookAt;
                fov = sample.Fov;
            }

            pose = new CameraPose(PoseFrame.Site, local.ToParent(offset), local.ToParent(aim), local.X, fov);
            horizon = local;
        }
        else
        {
            pose = ToSiteFrame(EvaluateLevel(shot, progress, inputs), smoothed);
        }

        if (shot.Aim == ShotAim.Vehicle && Shot.SupportsAim(shot.Type))
            pose = pose with { LookAt = AimPoint(shot, inputs, smoothed, horizon) };

        if (shot.Fit is double fit && SupportsFit(shot.Type) && inputs.VehicleHeight > 0.0)
        {
            V3 centre = smoothed.Origin + smoothed.X * (inputs.VehicleHeight * 0.5);
            pose = pose with { Fov = FitFov(inputs.VehicleHeight, inputs.VehicleWidth, (centre - pose.Position).Length, fit) };
        }

        if (shot.EffectiveUp == ShotUp.Horizon)
            pose = pose with { Up = LocalHorizon.Up(pose.Position, inputs.BodyCentre) };
        double roll = RollOf(shot, progress);
        return roll == 0.0 ? pose : pose with { Up = RollUp(pose.Position, pose.LookAt, pose.Up, roll) };
    }

    /// <summary>
    /// The center of the vehicle plus the aim offset. The offset is in the motion frame of the shot,
    /// with <paramref name="horizon"/> as the local or flight frame.
    /// </summary>
    public static V3 AimPoint(Shot shot, PoseInputs inputs, Basis3 smoothed, Basis3? horizon)
    {
        V3 centre = smoothed.Origin + smoothed.X * (inputs.VehicleHeight * 0.5);
        V3 offset = MotionFrameOf(shot) switch
        {
            MotionFrame.Site => shot.AimOffset,
            MotionFrame.Local when horizon.HasValue => horizon.Value.DirectionToParent(shot.AimOffset),
            _ => smoothed.DirectionToParent(shot.AimOffset),
        };
        return centre + offset;
    }

    public static bool SupportsFit(ShotType type) => type is ShotType.Chase or ShotType.Part or ShotType.Path or ShotType.Track;

    /// <summary>The smallest and largest field of view that a fit sets, in degrees.</summary>
    public const double FitMinFov = 5.0, FitMaxFov = 90.0;

    /// <summary>
    /// The vertical field of view in degrees at which a vehicle fills the share <paramref name="fit"/>
    /// of the picture height. The diagonal of length and width is fitted, so the field of view does not
    /// change when the vehicle turns.
    /// </summary>
    public static double FitFov(double length, double width, double distance, double fit)
    {
        double size = Math.Sqrt(length * length + width * width);
        double half = Math.Atan(size / (2.0 * Math.Max(distance, 1.0) * Math.Max(fit, 1e-3)));
        return Math.Clamp(RadToDeg(2.0 * half), FitMinFov, FitMaxFov);
    }

    private static CameraPose EvaluateLevel(Shot shot, double progress, PoseInputs inputs)
    {
        progress = Math.Clamp(progress, 0.0, 1.0);
        PoseFrame targetFrame = shot.Target == ShotTarget.Site ? PoseFrame.Site : PoseFrame.Vehicle;
        V3 up = V3.UnitX;

        switch (shot.Type)
        {
            case ShotType.Orbit:
            {
                double angle = shot.StartDeg + shot.DegPerSec * shot.Duration * progress;
                V3 position = shot.Center + new V3(shot.Height, 0.0, 0.0) + Around(angle, shot.Radius);
                return new CameraPose(targetFrame, position, shot.LookAt, up, shot.Fov);
            }
            case ShotType.Crane:
            {
                double height = shot.From + (shot.To - shot.From) * progress;
                V3 position = new V3(height, 0.0, 0.0) + Around(shot.Azimuth, shot.Radius);
                return new CameraPose(targetFrame, position, new V3(height, 0.0, 0.0), up, shot.Fov);
            }
            case ShotType.Tripod:
                return new CameraPose(targetFrame, shot.Position, shot.LookAt, up, shot.Fov);
            case ShotType.Track:
            {
                V3 aim = inputs.Vehicle.Origin + inputs.Vehicle.X * (inputs.VehicleHeight * 0.5);
                double fov = shot.Fov;
                if (shot.KeepSize && inputs.TrackReferenceDistance > 0.0)
                {
                    double distance = (aim - shot.Position).Length;
                    double half = Math.Tan(DegToRad(shot.Fov) * 0.5) * inputs.TrackReferenceDistance / Math.Max(distance, 1e-6);
                    fov = Math.Clamp(RadToDeg(2.0 * Math.Atan(half)), 0.5, 120.0);
                }

                return new CameraPose(PoseFrame.Site, shot.Position, aim, up, fov);
            }
            case ShotType.Path:
            {
                PathSample sample = PathMath.Evaluate(shot.Points, progress, shot.Fov);
                return new CameraPose(targetFrame, sample.Position, sample.LookAt, up, sample.Fov);
            }
            case ShotType.Chase:
                return new CameraPose(PoseFrame.Vehicle, shot.Offset, shot.LookAt, up, shot.Fov);
            case ShotType.Part:
            {
                V3 part = inputs.PartPosition ?? V3.Zero;
                return new CameraPose(PoseFrame.Vehicle, part + shot.Offset, part + shot.LookAt, up, shot.Fov);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(shot), shot.Type, "unknown shot type");
        }
    }

    public static CameraPose ToSiteFrame(CameraPose pose, Basis3 vehicle)
    {
        if (pose.Frame == PoseFrame.Site)
            return pose;
        return new CameraPose(PoseFrame.Site, vehicle.ToParent(pose.Position), vehicle.ToParent(pose.LookAt),
            vehicle.DirectionToParent(pose.Up), pose.Fov);
    }

    /// <summary>
    /// The look point is interpolated, not the view direction, so a subject that both shots look at
    /// stays in the picture. A pose whose look point is at its position falls back to a slerp of the
    /// look direction.
    /// </summary>
    public static CameraPose Blend(CameraPose from, CameraPose to, double weight, bool curved = false)
    {
        weight = Math.Clamp(weight, 0.0, 1.0);
        if (from.Frame != to.Frame)
            throw new ArgumentException("both poses must be in the same frame");

        V3 position = curved ? CurvedPosition(from, to, weight) : V3.Lerp(from.Position, to.Position, weight);
        V3 up = V3.Lerp(from.Up, to.Up, weight).NormalizedOr(from.Up);
        double fov = from.Fov + (to.Fov - from.Fov) * weight;

        if (HasLookPoint(from) && HasLookPoint(to))
        {
            V3 point = V3.Lerp(from.LookAt, to.LookAt, weight);
            if ((point - position).Length > 1e-6)
                return new CameraPose(from.Frame, position, point, up, fov);
        }

        V3 fromLook = from.LookAt - from.Position;
        V3 toLook = to.LookAt - to.Position;
        V3 direction = V3.SlerpDirection(fromLook, toLook, weight);
        double distance = fromLook.Length + (toLook.Length - fromLook.Length) * weight;
        return new CameraPose(from.Frame, position, position + direction * Math.Max(distance, 1e-6), up, fov);
    }

    private static bool HasLookPoint(CameraPose pose)
    {
        V3 look = pose.LookAt - pose.Position;
        double length = look.Length;
        return length > 1e-6 && !double.IsNaN(length) && !double.IsInfinity(length);
    }

    // A quadratic Bezier whose control point lies away from the subject and a little upward, so the
    // camera swings around the subject instead of cutting through it. The swing fades out when the
    // chord passes at least one chord length from the subject.
    private static V3 CurvedPosition(CameraPose from, CameraPose to, double weight)
    {
        V3 chord = to.Position - from.Position;
        double length = chord.Length;
        if (length < 1e-6)
            return from.Position;

        V3 axis = chord / length;
        V3 mid = (from.Position + to.Position) * 0.5;
        V3 subject = (from.LookAt + to.LookAt) * 0.5;
        V3 away = mid - subject;
        away -= axis * V3.Dot(away, axis);

        V3 up = (from.Up + to.Up).NormalizedOr(V3.UnitX);
        V3 upSide = up - axis * V3.Dot(up, axis);
        if (upSide.Length < 1e-6)
            upSide = V3.Cross(axis, V3.UnitY);
        V3 side = (away + upSide.NormalizedOr(V3.UnitX) * (0.25 * length)).NormalizedOr(upSide.NormalizedOr(V3.UnitX));

        double nearness = Math.Clamp(1.0 - away.Length / length, 0.0, 1.0);
        V3 control = mid + side * (BlendSwing * length * nearness);
        double w = weight, v = 1.0 - weight;
        return from.Position * (v * v) + control * (2.0 * v * w) + to.Position * (w * w);
    }

    // The distance of the Bezier control point from the chord, as a share of the chord length.
    private const double BlendSwing = 0.5;

    // The offset of a point at the given distance around the +X axis. The angle is in degrees,
    // 0 is the +Z axis (north on the site frame) and 90 is the +Y axis (east).
    private static V3 Around(double angleDeg, double radius)
    {
        double a = DegToRad(angleDeg);
        return new V3(0.0, Math.Sin(a) * radius, Math.Cos(a) * radius);
    }

    private static double DegToRad(double degrees) => degrees * Math.PI / 180.0;

    private static double RadToDeg(double radians) => radians * 180.0 / Math.PI;
}
