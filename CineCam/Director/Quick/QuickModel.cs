namespace CineCam.Director.Quick;

public enum QuickMode { Flyby, Chase, Orbit, Onboard, GroundTracker }

/// <summary>A ground point turns with the body, and an orbit point falls freely from its state at placement.</summary>
public enum FlybyAnchor { Ground, Orbit }

/// <summary>The window changes these and the camera reads them in each frame.</summary>
public sealed class QuickOptions
{
    public bool AutoZoom { get; set; } = true;

    /// <summary>The share of the picture height that the vehicle fills with the auto-zoom.</summary>
    public double ZoomShare { get; set; } = 0.35;

    /// <summary>Vertical, in degrees, used without the auto-zoom.</summary>
    public double Fov { get; set; } = 60.0;

    /// <summary>Seconds from the placement of a ground flyby point to the pass of the vehicle.</summary>
    public double FlybyPassSeconds { get; set; } = 6.0;

    /// <summary>Meters from the line of flight. 0 or less picks it from the speed and the size.</summary>
    public double FlybySide { get; set; }

    /// <summary>m/s relative to an orbit flyby point.</summary>
    public double FlybyOrbitPassSpeed { get; set; } = 15.0;

    public bool FlybyAutoNext { get; set; } = true;

    /// <summary>In vehicle sizes.</summary>
    public double ChaseDistance { get; set; } = 3.0;

    /// <summary>In vehicle sizes.</summary>
    public double ChaseHeight { get; set; } = 0.6;

    /// <summary>In vehicle sizes.</summary>
    public double OrbitDistance { get; set; } = 3.0;

    /// <summary>Degrees above the local horizon.</summary>
    public double OrbitElevation { get; set; } = 15.0;

    /// <summary>Degrees per real second, and a negative rate turns the other way.</summary>
    public double OrbitDegPerSec { get; set; } = 6.0;

    /// <summary>When false, the onboard camera looks down the stack toward the base.</summary>
    public bool OnboardTowardNose { get; set; }

    /// <summary>Horizontal meters from the point under the vehicle.</summary>
    public double TrackerDistance { get; set; } = 1500.0;
}

/// <summary>
/// Every vector has its origin at the center of mass of the vehicle and the axes of the body-centered
/// inertial frame of the game, so the numbers stay small and a pose maps to the game by one addition.
/// </summary>
public sealed class QuickWorld
{
    /// <summary>+X to the nose, +Y to the right, +Z down.</summary>
    public Basis3 Vehicle { get; init; } = Basis3.Identity;

    /// <summary>The center of the bounding box, which the cameras look at.</summary>
    public V3 Centre { get; init; }

    /// <summary>Meters along the axis of the vehicle.</summary>
    public double Length { get; init; } = 1.0;

    /// <summary>The larger of the two sizes across the axis, in meters.</summary>
    public double Width { get; init; } = 1.0;

    public V3 BodyCentre { get; init; } = new(-1.0, 0.0, 0.0);

    /// <summary>The rotation axis of the body, toward north.</summary>
    public V3 Pole { get; init; } = V3.UnitZ;

    /// <summary>m/s over the turning surface of the body.</summary>
    public V3 SurfaceVelocity { get; init; }

    /// <summary>m/s relative to the body center.</summary>
    public V3 InertialVelocity { get; init; }

    /// <summary>Meters above the mean radius.</summary>
    public double Altitude { get; init; }

    /// <summary>Meters above the mean radius, 0 without an atmosphere.</summary>
    public double AtmosphereHeight { get; init; }

    public bool Contact { get; init; }

    /// <summary>At least 1 m, the unit of distances in vehicle sizes.</summary>
    public double Size => Math.Max(Math.Max(Length, Width), 1.0);

    public double BodyRadius => Math.Max(BodyCentre.Length - Altitude, 1.0);

    public V3 Up => (-BodyCentre).NormalizedOr(V3.UnitX);
}

/// <summary>
/// <see cref="Position"/> is in the frame of <see cref="QuickWorld"/>. <see cref="PointVelocity"/> is the
/// inertial velocity that an orbit point starts with.
/// </summary>
public readonly record struct FlybyPlan(FlybyAnchor Anchor, V3 Position, V3 PointVelocity, double Distance);
