using CineCam.Director;

namespace CineCam.Editing;

public enum ShotPreset { TowerCam, GroundTracker, ChaseLow, OnboardStack, OrbitReveal, Flyby }

/// <summary>
/// <see cref="Vehicle"/> is the vehicle frame in the world point frame (+X toward the nose, origin at the
/// base), as the director uses it. <see cref="Velocity"/> is in the same frame, in m/s.
/// </summary>
public readonly record struct PresetInput(double Length, double Width, Basis3 Vehicle, V3 Velocity);

/// <summary>
/// The presets that stand at a fixed place get their position in the world point frame from where the
/// vehicle is now, so they suit a shot that starts soon. Nothing here touches the game.
/// </summary>
public static class ShotPresets
{
    // Meters, so a vehicle with no size read yet still gives a usable shot.
    private const double MinLength = 5.0;
    private const double MinWidth = 1.0;

    // m/s, below which the flyby uses the vehicle axis as its direction.
    private const double StandingSpeed = 2.0;

    public static readonly ShotPreset[] All = Enum.GetValues<ShotPreset>();

    public static string Label(ShotPreset preset) => preset switch
    {
        ShotPreset.TowerCam => UiText.MessageText("text.9eaf2b7c948e", "Tower cam"),
        ShotPreset.GroundTracker => UiText.MessageText("text.39a241e43045", "Ground tracker, long lens"),
        ShotPreset.ChaseLow => UiText.MessageText("text.c49ba5df103a", "Chase, low"),
        ShotPreset.OnboardStack => UiText.MessageText("text.666fa6153093", "Onboard, down the stack"),
        ShotPreset.OrbitReveal => UiText.MessageText("text.9a9455a162fe", "Orbit reveal"),
        _ => UiText.MessageText("text.6c435a230c44", "Flyby"),
    };

    public static string Help(ShotPreset preset) => preset switch
    {
        ShotPreset.TowerCam => UiText.MessageText("text.3c198e4cb306", "A fixed camera low and close beside the vehicle that looks up along it. Good on the pad before and at lift-off."),
        ShotPreset.GroundTracker => UiText.MessageText("text.d2606fed3b27", "A fixed camera far away at the height of the base that turns to follow the vehicle and keeps it framed, like a long lens on a tracking mount."),
        ShotPreset.ChaseLow => UiText.MessageText("text.b46b909bd5ab", "Follows the vehicle from behind and below, along the direction of flight, with the horizon level."),
        ShotPreset.OnboardStack => UiText.MessageText("text.bba28e6a878a", "A camera mounted at the top of the vehicle that looks down along its side to the base."),
        ShotPreset.OrbitReveal => UiText.MessageText("text.2562214102fd", "Turns slowly around the vehicle and looks at its middle."),
        _ => UiText.MessageText("text.276f902296d4", "A fixed point ahead on the path of the vehicle. The vehicle flies past the camera at the middle of the shot."),
    };

    public static Shot Make(ShotPreset preset, PresetInput input)
    {
        double length = Math.Max(input.Length, MinLength);
        double width = Math.Max(input.Width, MinWidth);
        Basis3 v = input.Vehicle;
        Shot shot;
        switch (preset)
        {
            case ShotPreset.TowerCam:
                shot = ShotEdit.NewShot(ShotType.Tripod);
                shot.Target = ShotTarget.Site;
                shot.Position = Round(v.ToParent(new V3(2.0, width * 1.5 + 6.0, width * 0.5)));
                shot.LookAt = Round(v.ToParent(new V3(length * 0.9, 0.0, 0.0)));
                shot.Fov = 65.0;
                shot.Duration = 8.0;
                break;
            case ShotPreset.GroundTracker:
                shot = ShotEdit.NewShot(ShotType.Track);
                shot.Position = Round(v.ToParent(new V3(1.5, length * 20.0, length * 8.0)));
                shot.Aim = ShotAim.Vehicle;
                shot.Fit = 0.45;
                shot.Fov = 10.0;
                shot.Duration = 20.0;
                break;
            case ShotPreset.ChaseLow:
                shot = ShotEdit.NewShot(ShotType.Chase);
                shot.Frame = ShotFrame.Flight;
                shot.Offset = new V3(Math.Round(length * 0.1, 2), Math.Round(width * 1.5, 2), Math.Round(-length * 1.6, 2));
                shot.LookAt = new V3(Math.Round(length * 0.5, 2), 0.0, 0.0);
                shot.Aim = ShotAim.Vehicle;
                shot.Fit = 0.55;
                shot.Smoothing = 0.8;
                shot.Duration = 15.0;
                break;
            case ShotPreset.OnboardStack:
                shot = ShotEdit.NewShot(ShotType.Part);
                shot.Part = "";
                shot.Frame = ShotFrame.Vehicle;
                shot.Offset = new V3(Math.Round(length + 0.5, 2), Math.Round(width * 0.5 + 0.6, 2), 0.0);
                shot.LookAt = new V3(0.0, Math.Round(width * 0.5, 2), 0.0);
                shot.Smoothing = 0.0;
                shot.Fov = 70.0;
                shot.Duration = 10.0;
                break;
            case ShotPreset.OrbitReveal:
                shot = ShotEdit.NewShot(ShotType.Orbit);
                shot.Target = ShotTarget.Vehicle;
                shot.Center = new V3(Math.Round(length * 0.5, 2), 0.0, 0.0);
                shot.Radius = Math.Round(Math.Max(length, width) * 1.8, 2);
                shot.Height = Math.Round(length * 0.15, 2);
                shot.StartDeg = 0.0;
                shot.DegPerSec = 8.0;
                shot.LookAt = shot.Center;
                shot.Ease = Ease.InOut;
                shot.Fov = 40.0;
                shot.Duration = 14.0;
                break;
            default:
                shot = Flyby(input, length, width);
                break;
        }

        return shot;
    }

    // The camera stands beside the point that the vehicle reaches at the middle of the shot.
    private static Shot Flyby(PresetInput input, double length, double width)
    {
        Basis3 v = input.Vehicle;
        double speed = input.Velocity.Length;
        Shot shot = ShotEdit.NewShot(ShotType.Track);
        shot.Duration = 8.0;
        V3 direction = speed > StandingSpeed ? input.Velocity * (1.0 / speed) : v.X;
        V3 side = V3.Cross(direction, v.Y).NormalizedOr(v.Z);
        V3 up = V3.Cross(side, direction).NormalizedOr(v.X);
        double ahead = Math.Max(speed * shot.Duration * 0.5, length * 3.0);
        double off = Math.Max(length * 1.5, width * 6.0);
        V3 centre = v.Origin + v.X * (length * 0.5);
        shot.Position = Round(centre + direction * ahead + side * off + up * (length * 0.3));
        shot.Aim = ShotAim.Vehicle;
        shot.Fit = 0.5;
        shot.Fov = 30.0;
        return shot;
    }

    /// <summary>A shot that suits any vehicle anywhere.</summary>
    public static Shot Generic()
    {
        Shot shot = ShotEdit.NewShot(ShotType.Chase);
        shot.Frame = ShotFrame.Flight;
        shot.Offset = new V3(10.0, 8.0, -40.0);
        shot.LookAt = new V3(10.0, 0.0, 0.0);
        shot.Aim = ShotAim.Vehicle;
        shot.Fit = 0.5;
        shot.Smoothing = 0.8;
        shot.Duration = 10.0;
        return shot;
    }

    private static V3 Round(V3 v) => new(Math.Round(v.X, 2), Math.Round(v.Y, 2), Math.Round(v.Z, 2));
}

public static class ShotLabels
{
    public static string Type(ShotType type) => type switch
    {
        ShotType.Orbit => UiText.MessageText("text.29ac7e56b3b4", "Orbit"),
        ShotType.Crane => UiText.MessageText("text.cf5445e943f7", "Crane"),
        ShotType.Tripod => UiText.MessageText("text.1246fc93bca0", "Fixed"),
        ShotType.Track => UiText.MessageText("text.051f01f095a2", "Track"),
        ShotType.Chase => UiText.MessageText("text.1ae6e6416a7a", "Chase"),
        ShotType.Part => UiText.MessageText("text.24568053844f", "Part mount"),
        _ => UiText.MessageText("text.62fa5a5b0d3c", "Path"),
    };

    public static string TypeHelp(ShotType type) => type switch
    {
        ShotType.Orbit => UiText.MessageText("text.b9a1ff542520", "Turns around an axis at a fixed radius and height, around a world point or around the vehicle."),
        ShotType.Crane => UiText.MessageText("text.0cf611243b84", "Moves up or down beside an axis and always looks at the axis."),
        ShotType.Tripod => UiText.MessageText("text.8f805830b973", "Stands still at one position and looks at one point."),
        ShotType.Track => UiText.MessageText("text.6f3537b18452", "Stands still at a world point and turns to follow the vehicle."),
        ShotType.Chase => UiText.MessageText("text.9edbdabf0d69", "Moves with the vehicle at an offset, with smooth turns."),
        ShotType.Part => UiText.MessageText("text.5c16a318d574", "Is fixed to a part of the vehicle, like an onboard camera."),
        _ => UiText.MessageText("text.edd451c95436", "Moves through a list of camera points on a smooth curve."),
    };

    /// <summary>Empty for a type with one fixed frame.</summary>
    public static IReadOnlyList<FrameChoice> Choices(ShotType type) => type switch
    {
        ShotType.Orbit or ShotType.Crane or ShotType.Tripod => new[] { FrameChoice.World, FrameChoice.Vehicle },
        ShotType.Chase or ShotType.Part => new[] { FrameChoice.Vehicle, FrameChoice.Horizon, FrameChoice.Flight },
        ShotType.Path => new[] { FrameChoice.World, FrameChoice.Vehicle, FrameChoice.Horizon, FrameChoice.Flight },
        _ => Array.Empty<FrameChoice>(),
    };

    /// <summary>Combines the target and the frame of the shot into one choice.</summary>
    public static FrameChoice FrameOf(Shot shot)
    {
        if (shot.Type == ShotType.Track)
            return FrameChoice.World;
        bool takesTarget = shot.Type is ShotType.Orbit or ShotType.Crane or ShotType.Tripod or ShotType.Path;
        if (takesTarget && shot.Target == ShotTarget.Site)
            return FrameChoice.World;
        if (shot.Type is ShotType.Orbit or ShotType.Crane or ShotType.Tripod)
            return FrameChoice.Vehicle;
        return shot.Frame switch
        {
            ShotFrame.Local => FrameChoice.Horizon,
            ShotFrame.Flight => FrameChoice.Flight,
            _ => FrameChoice.Vehicle,
        };
    }

    /// <summary>Returns false when the type cannot use the choice.</summary>
    public static bool SetFrame(Shot shot, FrameChoice choice)
    {
        if (!Choices(shot.Type).Contains(choice))
            return false;
        if (choice == FrameChoice.World)
        {
            shot.Target = ShotTarget.Site;
            return true;
        }

        if (shot.Type is not (ShotType.Chase or ShotType.Part))
            shot.Target = ShotTarget.Vehicle;
        if (shot.Type is ShotType.Chase or ShotType.Part or ShotType.Path)
        {
            shot.Frame = choice switch
            {
                FrameChoice.Horizon => ShotFrame.Local,
                FrameChoice.Flight => ShotFrame.Flight,
                _ => ShotFrame.Vehicle,
            };
        }

        return true;
    }

    public static string Frame(FrameChoice choice, ShotType type)
    {
        string name = type == ShotType.Part ? UiText.MessageText("text.8570a2e1669f", "Part") : UiText.MessageText("text.a62394ba4acc", "Vehicle");
        return choice switch
        {
            FrameChoice.World => UiText.MessageText("text.83d5fdd0af5e", "World point"),
            FrameChoice.Horizon => UiText.Concat(name, UiText.MessageText("text.f15834d1facd", ", horizon level")),
            FrameChoice.Flight => UiText.Concat(name, UiText.MessageText("text.36138ad2e537", ", along flight")),
            _ => name,
        };
    }

    public static string FrameHelp(FrameChoice choice) => choice switch
    {
        FrameChoice.World => UiText.MessageText("text.45180ff5e872", "The camera is placed relative to a point on the ground that does not move. +X is up, +Y east, +Z north (m)."),
        FrameChoice.Horizon => UiText.MessageText("text.eab3339437bb", "The camera moves with the vehicle, but keeps the horizon level when the vehicle turns. +X is away from the ground, +Y east, +Z north (m)."),
        FrameChoice.Flight => UiText.MessageText("text.9c6eefc86fb6", "The camera moves with the vehicle and turns with its direction of flight, with the horizon level. +Z points along the flight (m)."),
        _ => UiText.MessageText("text.5795d9fcd42b", "The camera moves and turns with the vehicle. +X points to the nose (m)."),
    };
}

public enum FrameChoice { World, Vehicle, Horizon, Flight }
