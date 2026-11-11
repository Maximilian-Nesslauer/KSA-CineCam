namespace CineCam.Director.Quick;

/// <summary>
/// Every vector is in the frame of <see cref="QuickWorld"/>, with the origin at the center of mass of the
/// vehicle and inertial axes, and the poses are <see cref="PoseFrame.Site"/> poses of that frame.
/// </summary>
public static class QuickMath
{
    /// <summary>Meters, the lowest altitude of an orbit flyby point, which matters on a body without an atmosphere.</summary>
    public const double AirlessGroundAltitude = 10_000.0;

    /// <summary>m/s, below which the flyby point goes in front of the nose.</summary>
    public const double StandingSpeed = 1.0;

    /// <summary>Meters.</summary>
    public const double MaxFlybyAhead = 20_000.0;

    // A share of the distance ahead. 0.35 sees the approach from about 19 degrees off the line of
    // flight, so the vehicle shows its side and not only its nose.
    private const double SideShare = 0.35;

    /// <summary>A flyby is done when the vehicle moves away from the point and is this many placement distances from it.</summary>
    public const double FlybyPastFactor = 1.5;

    /// <summary>Meters.</summary>
    public const double TrackerMaxRange = 150_000.0;

    /// <summary>Meters above the ground or the sea.</summary>
    public const double TrackerEyeHeight = 2.0;

    /// <summary>Meters above the ground or the sea.</summary>
    public const double GroundClearance = 3.0;

    /// <summary>Degrees, narrower than the other cameras because the ground tracker is a long lens.</summary>
    public const double LongLensMinFov = 1.0;

    /// <summary>Degrees.</summary>
    public const double NormalMinFov = PoseMath.FitMinFov;

    /// <summary>
    /// Inside the air the flight follows the surface, and outside it the vehicle coasts on its orbit, so
    /// the anchor makes the point move the same way as the vehicle.
    /// </summary>
    public static FlybyAnchor ChooseAnchor(double altitude, double atmosphereHeight, bool contact) =>
        !contact && altitude > Math.Max(atmosphereHeight, AirlessGroundAltitude) ? FlybyAnchor.Orbit : FlybyAnchor.Ground;

    public static V3 FlightVelocity(QuickWorld world, FlybyAnchor anchor) =>
        anchor == FlybyAnchor.Orbit ? world.InertialVelocity : world.SurfaceVelocity;

    /// <summary>
    /// An orbit point starts with the velocity of the vehicle minus the pass speed along the motion, so the
    /// vehicle passes it at that speed. A standing vehicle gets the point in front of its nose, or north when
    /// the nose points up. <paramref name="side"/> is +1 for the right of the motion and -1 for the left.
    /// </summary>
    public static FlybyPlan PlaceFlyby(QuickWorld world, FlybyAnchor anchor, QuickOptions options, int side)
    {
        V3 up = world.Up;
        Basis3 local = LocalHorizon.Frame(V3.Zero, world.BodyCentre, world.Pole);
        V3 velocity = FlightVelocity(world, anchor);
        double speed = velocity.Length;
        double size = world.Size;
        double minAhead = 2.0 * size + 10.0;
        double passSeconds = Math.Max(options.FlybyPassSeconds, 0.5);

        V3 direction;
        double relativeSpeed;
        V3 pointVelocity = V3.Zero;
        if (anchor == FlybyAnchor.Orbit && speed > StandingSpeed)
        {
            direction = velocity / speed;
            relativeSpeed = Math.Max(options.FlybyOrbitPassSpeed, 0.5);
            pointVelocity = velocity - direction * relativeSpeed;
        }
        else if (speed > StandingSpeed)
        {
            direction = velocity / speed;
            relativeSpeed = speed;
        }
        else
        {
            V3 nose = world.Vehicle.X - up * V3.Dot(world.Vehicle.X, up);
            direction = nose.Length > 0.2 ? nose.NormalizedOr(local.Z) : local.Z;
            relativeSpeed = 0.0;
        }

        double ahead = Math.Clamp(relativeSpeed * passSeconds, minAhead, MaxFlybyAhead);
        double sideDistance = options.FlybySide > 0.0
            ? options.FlybySide
            : Math.Clamp(SideShare * ahead, 1.5 * size + 5.0, 2000.0);

        V3 sideAxis = V3.Cross(direction, up);
        if (sideAxis.Length < 0.2)
            sideAxis = local.Y;
        sideAxis = sideAxis.NormalizedOr(local.Y) * (side < 0 ? -1.0 : 1.0);
        V3 lift = (up - direction * V3.Dot(up, direction)).NormalizedOr(V3.Zero);
        if (V3.Dot(lift, up) < 0.2)
            lift = V3.Zero;

        V3 position = world.Centre + direction * ahead + sideAxis * sideDistance + lift * (0.3 * sideDistance);
        return new FlybyPlan(anchor, position, pointVelocity, (position - world.Centre).Length);
    }

    /// <param name="vehicleFromCamera">The vector from the camera to the vehicle.</param>
    /// <param name="relativeVelocity">The velocity of the vehicle minus the velocity of the point.</param>
    public static bool FlybyDone(V3 vehicleFromCamera, V3 relativeVelocity, double placedDistance) =>
        V3.Dot(vehicleFromCamera, relativeVelocity) > 0.0
        && vehicleFromCamera.Length > FlybyPastFactor * Math.Max(placedDistance, 1.0);

    public static CameraPose ChasePose(QuickWorld world, V3? heading, QuickOptions options)
    {
        Basis3 flight = LocalHorizon.FlightFrame(world.Centre, world.BodyCentre, world.Pole, heading);
        double distance = Math.Max(options.ChaseDistance, 0.2) * world.Size;
        double height = options.ChaseHeight * world.Size;
        V3 position = world.Centre - flight.Z * distance + flight.X * height;
        return new CameraPose(PoseFrame.Site, position, world.Centre, flight.X, options.Fov);
    }

    /// <summary>The angle is in degrees in the local horizon frame, 0 is north and 90 is east.</summary>
    public static CameraPose OrbitPose(QuickWorld world, double angleDeg, QuickOptions options)
    {
        Basis3 local = LocalHorizon.Frame(world.Centre, world.BodyCentre, world.Pole);
        double distance = Math.Max(options.OrbitDistance, 0.2) * world.Size;
        double a = angleDeg * Math.PI / 180.0;
        double e = Math.Clamp(options.OrbitElevation, -89.0, 89.0) * Math.PI / 180.0;
        V3 around = (local.Z * Math.Cos(a) + local.Y * Math.Sin(a)) * Math.Cos(e);
        V3 position = world.Centre + (around + local.X * Math.Sin(e)) * distance;
        return new CameraPose(PoseFrame.Site, position, world.Centre, local.X, options.Fov);
    }

    /// <summary>
    /// The pose is in the vehicle frame. The camera sits just outside the bounding box of the part on the side
    /// away from the axis of the vehicle, or on the right for a part on the axis, and up in the picture points
    /// away from the hull.
    /// </summary>
    /// <param name="partMin">In the vehicle frame.</param>
    /// <param name="partMax">In the vehicle frame.</param>
    public static CameraPose OnboardPose(V3 partMin, V3 partMax, double vehicleLength, bool towardNose, double fov)
    {
        V3 centre = (partMin + partMax) * 0.5;
        V3 half = (partMax - partMin) * 0.5;
        var across = new V3(0.0, centre.Y, centre.Z);
        V3 radial = across.Length > 0.05 ? across.NormalizedOr(V3.UnitY) : V3.UnitY;
        double reach = Math.Abs(half.Y * radial.Y) + Math.Abs(half.Z * radial.Z);
        double partWidth = 2.0 * Math.Max(Math.Abs(half.Y), Math.Abs(half.Z));
        double margin = Math.Max(0.25, 0.15 * partWidth);
        double inset = Math.Min(0.1 * (partMax.X - partMin.X), 0.5);

        double x = towardNose ? partMin.X + inset : partMax.X - inset;
        V3 position = new V3(x, 0.0, 0.0) + across + radial * (reach + margin);
        V3 look = V3.UnitX * (towardNose ? 1.0 : -1.0) * Math.Max(vehicleLength, 10.0);
        return new CameraPose(PoseFrame.Vehicle, position, position + look, radial, fov);
    }

    /// <summary>
    /// The direction from the body center to the ground tracker. The tracker stands to the right of the flight
    /// and a quarter of the distance behind, so the vehicle crosses the picture and moves away from it.
    /// </summary>
    public static V3 TrackerDirection(QuickWorld world, V3? heading, double distance)
    {
        Basis3 flight = LocalHorizon.FlightFrame(V3.Zero, world.BodyCentre, world.Pole, heading);
        double radius = world.BodyRadius;
        distance = Math.Max(distance, 10.0);
        V3 ground = world.Up * radius + flight.Y * distance - flight.Z * (0.25 * distance);
        return ground.NormalizedOr(world.Up);
    }

    public static V3 PointOnBody(V3 bodyCentre, V3 direction, double radius) => bodyCentre + direction * radius;

    /// <summary>The reason why the ground tracker cannot start, or null when it can.</summary>
    public static string? TrackerBlock(double altitude) =>
        altitude > TrackerMaxRange
            ? $"The vehicle is higher than {TrackerMaxRange / 1000.0:F0} km, too far for a camera on the ground."
            : null;

    public static V3 KeepAbove(V3 point, V3 bodyCentre, double minRadius)
    {
        V3 fromCentre = point - bodyCentre;
        double radius = fromCentre.Length;
        if (radius >= minRadius || radius < 1e-9)
            return point;
        return bodyCentre + fromCentre * (minRadius / radius);
    }

    /// <summary>
    /// Degrees. It is <see cref="PoseMath.FitFov"/> with a lower limit that the caller picks, so a long lens
    /// can zoom further.
    /// </summary>
    public static double AutoZoomFov(double length, double width, double distance, double share, double minFov)
    {
        double fit = PoseMath.FitFov(length, width, distance, share);
        if (fit > PoseMath.FitMinFov || minFov >= PoseMath.FitMinFov)
            return Math.Max(fit, minFov);
        double size = Math.Sqrt(length * length + width * width);
        double half = Math.Atan(size / (2.0 * Math.Max(distance, 1.0) * Math.Max(share, 1e-3)));
        return Math.Clamp(2.0 * half * 180.0 / Math.PI, minFov, PoseMath.FitMaxFov);
    }

    public static CameraPose LookAtVehicle(QuickWorld world, V3 camera, double fov) =>
        new(PoseFrame.Site, camera, world.Centre, LocalHorizon.Up(camera, world.BodyCentre), fov);

    public static int Step(int index, int step, int count)
    {
        if (count <= 0)
            return 0;
        int next = (index + step) % count;
        return next < 0 ? next + count : next;
    }
}
