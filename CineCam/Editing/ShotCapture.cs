using CineCam.Director;

namespace CineCam.Editing;

/// <summary>In the launch site frame. <see cref="Forward"/> is a unit vector.</summary>
public readonly record struct CapturedCamera(V3 Position, V3 Forward, double Fov);

public enum KeyKind { Fixed, Track, Chase, Orbit, PartMount, PathPoint }

/// <summary>
/// The inverse of <see cref="PoseMath.Evaluate"/>, which writes a camera pose placed by hand into a shot
/// in the frame that the shot type uses.
/// </summary>
public static class ShotCapture
{
    // Meters, used when the view does not point at the vehicle.
    private const double FallbackLookDistance = 50.0;

    // Meters, because an orbit needs a radius above 0 and an angle cannot be read on the axis.
    private const double MinRadius = 0.5;

    public static bool CanCapture(ShotType type) =>
        type is ShotType.Tripod or ShotType.Track or ShotType.Chase or ShotType.Part;

    /// <summary>
    /// Orbit and crane shots move, so the captured pose becomes the pose at <paramref name="progress"/> and
    /// the speed of the shot stays as it is.
    /// </summary>
    /// <param name="progress">After the ease, 0 to 1, as <see cref="PoseMath.Evaluate"/> takes it.</param>
    /// <param name="bodyCentre">In the site frame.</param>
    /// <param name="pole">In the site frame.</param>
    public static bool ApplyAtProgress(Shot shot, CapturedCamera camera, Basis3 vehicle, double vehicleHeight,
        V3? partPosition, double progress, out string message, V3? bodyCentre = null, V3? pole = null, V3? heading = null)
    {
        if (shot.Type == ShotType.Path)
            return AddPathPoint(shot, camera, vehicle, vehicleHeight, progress, out message, bodyCentre, pole, heading);
        if (shot.Type is not (ShotType.Orbit or ShotType.Crane))
            return Apply(shot, camera, vehicle, vehicleHeight, partPosition, out message, bodyCentre, pole, heading);

        progress = Math.Clamp(progress, 0.0, 1.0);
        bool inSite = shot.Target == ShotTarget.Site;
        V3 position = inSite ? camera.Position : ToLocal(vehicle, camera.Position);
        if (shot.Type == ShotType.Orbit)
        {
            V3 rel = position - shot.Center;
            double radius = Math.Sqrt(rel.Y * rel.Y + rel.Z * rel.Z);
            if (radius < MinRadius)
            {
                message = UiText.MessageText("text.1912af93e7d2", "The camera is on the axis of the orbit, so no radius and angle can be read from it. Move it away from the axis.");
                return false;
            }

            V3 lookWorld = LookPoint(camera, vehicle, vehicleHeight);
            shot.LookAt = Round(inSite ? lookWorld : ToLocal(vehicle, lookWorld));
            shot.Height = Math.Round(rel.X, 2);
            shot.Radius = Math.Round(radius, 2);
            double angle = RadToDeg(Math.Atan2(rel.Y, rel.Z));
            double travelled = shot.DegPerSec * shot.Duration * progress;
            shot.StartDeg = Math.Round(Wrap360(angle - travelled), 2);
            message = UiText.MessageText("text.e38630ad6e6f", "Height, radius, start angle, look point and field of view were set from the camera. The turn speed stays, so the camera is at this angle at this moment of the shot.");
        }
        else
        {
            double radius = Math.Sqrt(position.Y * position.Y + position.Z * position.Z);
            if (radius < MinRadius)
            {
                message = UiText.MessageText("text.497ee065b1dc", "The camera is on the axis of the crane, so no radius and azimuth can be read from it. Move it away from the axis.");
                return false;
            }

            double shift = position.X - (shot.From + (shot.To - shot.From) * progress);
            shot.From = Math.Round(shot.From + shift, 2);
            shot.To = Math.Round(shot.To + shift, 2);
            shot.Radius = Math.Round(radius, 2);
            shot.Azimuth = Math.Round(Wrap360(RadToDeg(Math.Atan2(position.Y, position.Z))), 2);
            message = UiText.MessageText("text.f615973439bd", "Height, radius, azimuth and field of view were set from the camera. A crane always looks at its axis, so the view direction is not stored.");
        }

        shot.Fov = Math.Clamp(Math.Round(camera.Fov, 1), 1.0, 150.0);
        return true;
    }

    public static bool CanConvertToPath(ShotType type) => type is ShotType.Tripod or ShotType.Chase;

    public static Basis3 PathFrame(Shot shot, Basis3 vehicle, V3? bodyCentre, V3? pole, V3? heading = null)
    {
        if (shot.Type == ShotType.Path && shot.Target == ShotTarget.Site)
            return Basis3.Identity;
        return shot.UsesHorizonFrame ? HorizonFrame(shot, vehicle.Origin, bodyCentre, pole, heading) : vehicle;
    }

    private static Basis3 HorizonFrame(Shot shot, V3 origin, V3? bodyCentre, V3? pole, V3? heading) =>
        shot.UsesFlightFrame
            ? LocalHorizon.FlightFrame(origin, bodyCentre, pole, heading)
            : LocalHorizon.Frame(origin, bodyCentre, pole);

    /// <summary>
    /// Replaces a point that is at that progress already. A tripod or chase shot becomes a path shot with its
    /// pose as the point at 0, and a progress below 0.05 then puts the camera at the end, because two points
    /// at the start would give no move.
    /// </summary>
    /// <param name="progress">After the ease, 0 to 1, which is the T of a point.</param>
    public static bool AddPathPoint(Shot shot, CapturedCamera camera, Basis3 vehicle, double vehicleHeight,
        double progress, out string message, V3? bodyCentre = null, V3? pole = null, V3? heading = null)
    {
        bool converted = false;
        if (shot.Type != ShotType.Path)
        {
            if (!CanConvertToPath(shot.Type))
            {
                message = UiText.Format("text.6c1198ed4d4d", $"A {shot.Type.ToString().ToLowerInvariant()} shot cannot become a path shot. Use a tripod or chase shot, or add a path shot.");
                return false;
            }

            ConvertToPath(shot);
            converted = true;
        }

        progress = Math.Clamp(progress, 0.0, 1.0);
        double t = converted && progress < 0.05 ? 1.0 : Math.Round(progress, 3);
        Basis3 frame = PathFrame(shot, vehicle, bodyCentre, pole, heading);
        V3 look = LookPoint(camera, vehicle, vehicleHeight);
        var point = new PathPoint
        {
            T = t,
            Position = Round(ToLocal(frame, camera.Position)),
            LookAt = Round(ToLocal(frame, look)),
            Fov = Math.Clamp(Math.Round(camera.Fov, 1), 1.0, 150.0),
        };

        int existing = shot.Points.FindIndex(p => Math.Abs(p.T - t) < 0.005);
        if (existing >= 0)
        {
            point.T = shot.Points[existing].T;
            point.Roll = shot.Points[existing].Roll;
            shot.Points[existing] = point;
        }
        else
        {
            int at = shot.Points.FindIndex(p => p.T > t);
            shot.Points.Insert(at < 0 ? shot.Points.Count : at, point);
        }

        int number = shot.Points.IndexOf(point) + 1;
        string start = converted ? UiText.MessageText("text.5b994dee4c0b", "The shot is a path shot now, with its old pose as point 1. ") : "";
        message = UiText.Format("text.a47b5cf86d51", $"{start}The camera is point {number} of {shot.Points.Count} at {t:0.###} of the path.");
        return true;
    }

    // The points of a vehicle path stay in the frame that the tripod or chase shot used.
    private static void ConvertToPath(Shot shot)
    {
        bool chase = shot.Type == ShotType.Chase;
        var first = new PathPoint
        {
            T = 0.0,
            Position = chase ? shot.Offset : shot.Position,
            LookAt = shot.LookAt,
            Fov = shot.Fov,
        };
        shot.Type = ShotType.Path;
        if (chase)
            shot.Target = ShotTarget.Vehicle;
        if (shot.Ease == Ease.None)
            shot.Ease = Ease.InOut;
        shot.Points.Clear();
        shot.Points.Add(first);
    }

    /// <param name="vehicle">In the site frame, as the director uses it.</param>
    /// <param name="vehicleHeight">Meters along the axis of the vehicle.</param>
    /// <param name="partPosition">In the vehicle frame, or null when the vehicle has no such part.</param>
    /// <param name="bodyCentre">In the site frame, needed by a chase or part shot with the local frame.</param>
    /// <param name="pole">In the site frame.</param>
    /// <param name="heading">The horizontal direction of flight in the site frame, for a shot with the flight frame.</param>
    public static bool Apply(Shot shot, CapturedCamera camera, Basis3 vehicle, double vehicleHeight,
        V3? partPosition, out string message, V3? bodyCentre = null, V3? pole = null, V3? heading = null)
    {
        message = "";
        if (!CanCapture(shot.Type))
        {
            message = UiText.Format("text.dd45cba6d9c2", $"A {shot.Type.ToString().ToLowerInvariant()} shot has no fixed camera position, so the camera pose cannot be written into it.");
            return false;
        }

        V3 look = LookPoint(camera, vehicle, vehicleHeight);

        switch (shot.Type)
        {
            case ShotType.Tripod:
                if (shot.Target == ShotTarget.Site)
                {
                    shot.Position = Round(camera.Position);
                    shot.LookAt = Round(look);
                }
                else
                {
                    shot.Position = Round(ToLocal(vehicle, camera.Position));
                    shot.LookAt = Round(ToLocal(vehicle, look));
                }

                break;
            case ShotType.Track:
                shot.Position = Round(camera.Position);
                break;
            case ShotType.Chase:
                Basis3 chaseFrame = shot.UsesHorizonFrame ? HorizonFrame(shot, vehicle.Origin, bodyCentre, pole, heading) : vehicle;
                shot.Offset = Round(ToLocal(chaseFrame, camera.Position));
                shot.LookAt = Round(ToLocal(chaseFrame, look));
                break;
            case ShotType.Part:
                if (shot.Part.Length > 0 && partPosition == null)
                {
                    message = UiText.Format("text.5f48e4f679d1", $"The vehicle has no part \"{shot.Part}\", so the offset cannot be written.");
                    return false;
                }

                V3 part = partPosition ?? V3.Zero;
                if (shot.UsesHorizonFrame)
                {
                    Basis3 partFrame = HorizonFrame(shot, vehicle.ToParent(part), bodyCentre, pole, heading);
                    shot.Offset = Round(ToLocal(partFrame, camera.Position));
                    shot.LookAt = Round(ToLocal(partFrame, look));
                    break;
                }

                shot.Offset = Round(ToLocal(vehicle, camera.Position) - part);
                shot.LookAt = Round(ToLocal(vehicle, look) - part);
                break;
        }

        shot.Fov = Math.Clamp(Math.Round(camera.Fov, 1), 1.0, 150.0);
        message = shot.Type switch
        {
            ShotType.Track => UiText.MessageText("text.a919b5fa1346", "Position and field of view were set from the camera. A track shot looks at the vehicle by itself."),
            _ => UiText.MessageText("text.8dd4cb26ab31", "Position, look point and field of view were set from the camera."),
        };
        return true;
    }

    #region Add key here

    public static string KeyHelp(KeyKind kind) => kind switch
    {
        KeyKind.Fixed => UiText.MessageText("text.121cf6d98edc", "The camera stays at this place and looks in this direction."),
        KeyKind.Track => UiText.MessageText("text.c44bd627222b", "The camera stays at this place and turns to follow the vehicle."),
        KeyKind.Chase => UiText.MessageText("text.72819dfde05b", "The camera keeps this offset from the vehicle and follows it, with the horizon level."),
        KeyKind.Orbit => UiText.MessageText("text.6caee0fe8814", "The camera turns around the vehicle, starting from this place."),
        KeyKind.PartMount => UiText.MessageText("text.5e2688ea0a36", "The camera is fixed to the vehicle at this place, like an onboard camera."),
        _ => UiText.MessageText("text.2a5ce3db7eda", "Adds this place as a point of the selected path shot, or starts a new path shot here."),
    };

    public static string KeyLabel(KeyKind kind) => kind == KeyKind.PartMount ? UiText.MessageText("text.24568053844f", "Part mount") : kind == KeyKind.PathPoint ? UiText.MessageText("text.57e8a4879bcc", "Path point") : kind.ToString();

    /// <summary>Returns null for <see cref="KeyKind.PathPoint"/>, which <see cref="NewPath"/> and <see cref="AppendPathPoint"/> handle.</summary>
    /// <param name="vehicle">In the site frame, which the window calls the world point frame.</param>
    public static Shot? NewKey(KeyKind kind, CapturedCamera camera, Basis3 vehicle, double vehicleHeight, out string message,
        V3? bodyCentre = null, V3? pole = null, V3? heading = null)
    {
        Shot shot;
        bool ok;
        switch (kind)
        {
            case KeyKind.Fixed:
                shot = ShotEdit.NewShot(ShotType.Tripod);
                shot.Target = ShotTarget.Site;
                ok = Apply(shot, camera, vehicle, vehicleHeight, null, out message);
                break;
            case KeyKind.Track:
                shot = ShotEdit.NewShot(ShotType.Track);
                ok = Apply(shot, camera, vehicle, vehicleHeight, null, out message);
                break;
            case KeyKind.Chase:
                shot = ShotEdit.NewShot(ShotType.Chase);
                shot.Frame = ShotFrame.Local;
                ok = Apply(shot, camera, vehicle, vehicleHeight, null, out message, bodyCentre, pole, heading);
                break;
            case KeyKind.Orbit:
                shot = ShotEdit.NewShot(ShotType.Orbit);
                shot.Target = ShotTarget.Vehicle;
                shot.Center = V3.Zero;
                ok = ApplyAtProgress(shot, camera, vehicle, vehicleHeight, null, 0.0, out message);
                break;
            case KeyKind.PartMount:
                shot = ShotEdit.NewShot(ShotType.Part);
                shot.Part = "";
                shot.Frame = ShotFrame.Vehicle;
                shot.Smoothing = 0.0;
                ok = Apply(shot, camera, vehicle, vehicleHeight, null, out message);
                break;
            default:
                message = UiText.MessageText("text.1ca1ec725118", "A path point is added to a path shot.");
                return null;
        }

        return ok ? shot : null;
    }

    public static Shot NewPath(CapturedCamera camera, Basis3 vehicle, double vehicleHeight, V3? bodyCentre = null, V3? pole = null)
    {
        Shot shot = ShotEdit.NewShot(ShotType.Path);
        shot.Target = ShotTarget.Vehicle;
        shot.Frame = ShotFrame.Local;
        shot.Points.Clear();
        Basis3 frame = PathFrame(shot, vehicle, bodyCentre, pole);
        shot.Points.Add(new PathPoint
        {
            T = 0.0,
            Position = Round(ToLocal(frame, camera.Position)),
            LookAt = Round(ToLocal(frame, LookPoint(camera, vehicle, vehicleHeight))),
            Fov = Math.Clamp(Math.Round(camera.Fov, 1), 1.0, 150.0),
        });
        shot.Fov = shot.Points[0].Fov!.Value;
        return shot;
    }

    /// <summary>The existing points move toward the start by the same share, so the new point at T = 1 gets the end of the path.</summary>
    public static bool AppendPathPoint(Shot shot, CapturedCamera camera, Basis3 vehicle, double vehicleHeight, out string message,
        V3? bodyCentre = null, V3? pole = null, V3? heading = null)
    {
        if (shot.Type != ShotType.Path)
        {
            message = UiText.MessageText("text.c6ec92c0a99b", "Select a path shot first.");
            return false;
        }

        int count = shot.Points.Count;
        double scale = count / (count + 1.0);
        foreach (PathPoint existing in shot.Points)
            existing.T = Math.Round(existing.T * scale, 3);
        Basis3 frame = PathFrame(shot, vehicle, bodyCentre, pole, heading);
        shot.Points.Add(new PathPoint
        {
            T = 1.0,
            Position = Round(ToLocal(frame, camera.Position)),
            LookAt = Round(ToLocal(frame, LookPoint(camera, vehicle, vehicleHeight))),
            Fov = Math.Clamp(Math.Round(camera.Fov, 1), 1.0, 150.0),
        });
        message = UiText.Format("text.22ffb64c4926", $"The camera is point {shot.Points.Count} of {shot.Points.Count}, at the end of the path.");
        return true;
    }

    #endregion

    /// <summary>A point on the view line at the depth of the middle of the vehicle, or farther out when the vehicle is not ahead.</summary>
    public static V3 LookPoint(CapturedCamera camera, Basis3 vehicle, double vehicleHeight)
    {
        V3 aim = vehicle.Origin + vehicle.X * (vehicleHeight * 0.5);
        double distance = V3.Dot(aim - camera.Position, camera.Forward);
        if (distance < 2.0)
            distance = FallbackLookDistance;
        return camera.Position + camera.Forward * distance;
    }

    private static double RadToDeg(double radians) => radians * 180.0 / Math.PI;

    private static double Wrap360(double degrees)
    {
        double wrapped = degrees % 360.0;
        return wrapped < 0.0 ? wrapped + 360.0 : wrapped;
    }

    public static V3 ToLocal(Basis3 basis, V3 point)
    {
        V3 d = point - basis.Origin;
        return new V3(V3.Dot(d, basis.X), V3.Dot(d, basis.Y), V3.Dot(d, basis.Z));
    }

    private static V3 Round(V3 v) => new(Math.Round(v.X, 2), Math.Round(v.Y, 2), Math.Round(v.Z, 2));
}
