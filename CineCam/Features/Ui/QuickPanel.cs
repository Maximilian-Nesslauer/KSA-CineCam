using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using CineCam.Director.Quick;
using CineCam.Features.Quick;
using KSA;

namespace CineCam.Features.Ui;

internal static class QuickPanel
{
    private static readonly QuickMode[] Modes =
        [QuickMode.Flyby, QuickMode.Chase, QuickMode.Orbit, QuickMode.Onboard, QuickMode.GroundTracker];

    private static readonly double[] Speeds = [1.0, 0.5, 0.25, 0.1];

    // The running mode, or the last clicked mode when none runs.
    private static QuickMode _shown = QuickMode.Flyby;

    public static void Draw()
    {
        QuickMode? running = QuickCamera.Mode;
        if (running is QuickMode mode)
            _shown = mode;

        DrawModes(running);
        DrawState(running);
        DrawModeSettings(_shown, running);
        DrawPicture(_shown);
        DrawSlowMotion();
        ImGui.Dummy(new float2(0f, 6f * Widgets.Scale));
        Widgets.Muted("For a planned sequence of cameras, use the Editor.");
    }

    // Must not touch ImGui. It runs when the mod unloads.
    public static void Shutdown()
    {
        _shown = QuickMode.Flyby;
    }

    private static void DrawModes(QuickMode? running)
    {
        Widgets.Section("Camera");
        for (int i = 0; i < Modes.Length; i++)
        {
            QuickMode mode = Modes[i];
            if (i > 0)
                Widgets.SameLine();
            string? block = QuickCamera.StartBlock(mode);
            string label = QuickCamera.Describe(mode).ToUpperInvariant() + "##quick-" + mode;
            if (Widgets.Button(label, block == null, block ?? Explain(mode), running == mode ? Widgets.Tone.Primary : Widgets.Tone.Plain))
            {
                _shown = mode;
                QuickCamera.RequestStart(mode);
            }
        }

        Widgets.SameLine();
        if (Widgets.Button("STOP", running != null, running != null
                ? "Gives back the camera, the field of view and the sim speed from before."
                : "No quick camera runs.", Widgets.Tone.Danger))
            QuickCamera.RequestStop();

        string? common = QuickCamera.StartBlock(QuickMode.Chase);
        if (running == null && common != null)
            Widgets.Reason(common);
    }

    private static void DrawState(QuickMode? running)
    {
        if (running is QuickMode mode)
            Widgets.Label($"On: {QuickCamera.Describe(mode)}", Widgets.GoodColor);
        else
            Widgets.Muted("Off. Click a camera to start it. A take, a shot preview or a replay stops it.");
        if (QuickCamera.Status.Length > 0 && !(running is QuickMode on && QuickCamera.Status == $"{QuickCamera.Describe(on)} on."))
            Widgets.Muted(QuickCamera.Status);
    }

    private static void DrawModeSettings(QuickMode mode, QuickMode? running)
    {
        QuickOptions o = QuickCamera.Options;
        bool active = running == mode;
        Widgets.Section($"{QuickCamera.Describe(mode)} settings");
        switch (mode)
        {
            case QuickMode.Flyby:
            {
                if (Widgets.Button("NEXT FLYBY", active, active ? "Places a new point ahead of the vehicle." : "Start the flyby first."))
                    QuickCamera.RequestNextFlyby();
                Widgets.Reason(active ? "" : "Start the flyby first.");
                bool autoNext = o.FlybyAutoNext;
                if (Widgets.Check("Place the next point when the vehicle is past", ref autoNext))
                    o.FlybyAutoNext = autoNext;
                double pass = o.FlybyPassSeconds;
                if (Widgets.Num("Seconds until the vehicle passes", ref pass, 0.1f, 1.0, 60.0, "%.1f",
                        "The vehicle reaches the point after about this time."))
                    o.FlybyPassSeconds = pass;
                double side = o.FlybySide;
                if (Widgets.Num("Side distance (m)", ref side, 1.0f, 0.0, 5000.0, "%.0f",
                        "The distance from the line of flight. 0 picks it from the speed and the size of the vehicle."))
                    o.FlybySide = side;
                double orbitPass = o.FlybyOrbitPassSpeed;
                if (Widgets.Num("Pass speed in orbit (m/s)", ref orbitPass, 0.5f, 1.0, 500.0, "%.1f",
                        "In orbit the point falls with the vehicle, and the vehicle passes it at this speed."))
                    o.FlybyOrbitPassSpeed = orbitPass;
                const string AnchorHelp = "In the air or near the ground the point is fixed to the ground. Above the atmosphere (above 10 km on a body without air) it falls freely in orbit, so the vehicle passes it slowly.";
                if (active && QuickCamera.FlybyAnchorNow is FlybyAnchor anchor)
                    Widgets.StatusLine(anchor == FlybyAnchor.Orbit ? "The point falls with the vehicle in orbit." : "The point is fixed to the ground.",
                        Widgets.MutedColor, AnchorHelp);
                else
                    Widgets.StatusLine("The point is on the ground, or in orbit above the air.", Widgets.MutedColor, AnchorHelp);
                break;
            }
            case QuickMode.Chase:
            {
                double distance = o.ChaseDistance;
                if (Widgets.Num("Distance (vehicle sizes)", ref distance, 0.05f, 0.5, 50.0, "%.2f"))
                    o.ChaseDistance = distance;
                double height = o.ChaseHeight;
                if (Widgets.Num("Height (vehicle sizes)", ref height, 0.05f, -5.0, 20.0, "%.2f"))
                    o.ChaseHeight = height;
                break;
            }
            case QuickMode.Orbit:
            {
                double distance = o.OrbitDistance;
                if (Widgets.Num("Distance (vehicle sizes)", ref distance, 0.05f, 0.5, 50.0, "%.2f"))
                    o.OrbitDistance = distance;
                double elevation = o.OrbitElevation;
                if (Widgets.Num("Elevation (deg)", ref elevation, 0.5f, -80.0, 85.0, "%.0f"))
                    o.OrbitElevation = elevation;
                double rate = o.OrbitDegPerSec;
                if (Widgets.Num("Speed (deg/s)", ref rate, 0.2f, -90.0, 90.0, "%.1f", "A negative speed turns the other way."))
                    o.OrbitDegPerSec = rate;
                break;
            }
            case QuickMode.Onboard:
            {
                if (Widgets.Button("PREVIOUS PART", active, active ? null : "Start the onboard camera first."))
                    QuickCamera.RequestPartStep(-1);
                Widgets.SameLine();
                if (Widgets.Button("NEXT PART", active, active ? null : "Start the onboard camera first."))
                    QuickCamera.RequestPartStep(1);
                if (active && QuickCamera.OnboardPartCount > 0)
                    Widgets.Muted(string.Create(CultureInfo.InvariantCulture,
                        $"Part {QuickCamera.OnboardPartIndex + 1} of {QuickCamera.OnboardPartCount}: {QuickCamera.OnboardPartName}"));
                bool nose = o.OnboardTowardNose;
                if (Widgets.Check("Look toward the nose", ref nose, "Else the camera looks down the stack."))
                    o.OnboardTowardNose = nose;
                break;
            }
            case QuickMode.GroundTracker:
            {
                double distance = o.TrackerDistance;
                if (Widgets.Num("Distance from the flight path (m)", ref distance, 10.0f, 50.0, 20000.0, "%.0f",
                        "Used when the tracker is placed."))
                    o.TrackerDistance = distance;
                Vehicle? vehicle = Program.ControlledVehicle;
                string? block = vehicle == null || vehicle.IsDisposed ? "Select a vehicle in flight."
                    : QuickMath.TrackerBlock(vehicle.GetBarometricAltitude());
                if (Widgets.Button("PLACE UNDER THE VEHICLE AGAIN", active && block == null,
                        !active ? "Start the ground tracker first." : block))
                    QuickCamera.RequestStart(QuickMode.GroundTracker);
                Widgets.Muted(string.Create(CultureInfo.InvariantCulture,
                    $"A long lens on the ground near the point under the vehicle. It works up to {QuickMath.TrackerMaxRange / 1000.0:F0} km."));
                break;
            }
        }
    }

    private static void DrawPicture(QuickMode mode)
    {
        QuickOptions o = QuickCamera.Options;
        Widgets.Section("Picture");
        bool onboard = mode == QuickMode.Onboard;
        bool auto = o.AutoZoom;
        if (Widgets.Check("Auto-zoom", ref auto, "Keeps the vehicle at the same size in the picture."))
            o.AutoZoom = auto;
        if (o.AutoZoom && !onboard)
        {
            double share = o.ZoomShare * 100.0;
            if (Widgets.Num("Vehicle size (% of the picture height)", ref share, 0.5f, 2.0, 95.0, "%.0f"))
                o.ZoomShare = share / 100.0;
        }
        else
        {
            double fov = o.Fov;
            if (Widgets.Num("Field of view (deg)", ref fov, 0.2f, 5.0, 120.0, "%.1f"))
                o.Fov = fov;
        }

        if (onboard && o.AutoZoom)
            Widgets.Muted("The onboard camera is fixed to the hull and uses the field of view, not the auto-zoom.");
    }

    private static void DrawSlowMotion()
    {
        Widgets.Section("Slow motion");
        string? block = QuickCamera.SlowMotionBlock();
        double? current = QuickCamera.SlowMotion;
        for (int i = 0; i < Speeds.Length; i++)
        {
            double speed = Speeds[i];
            if (i > 0)
                Widgets.SameLine();
            bool chosen = current is double c && Math.Abs(c - speed) < 1e-9;
            string label = string.Create(CultureInfo.InvariantCulture, $"{speed:0.##}x##quick-speed-{i}");
            if (Widgets.Button(label, block == null, block ?? "The speed from before comes back when the quick camera stops.",
                    chosen ? Widgets.Tone.Primary : Widgets.Tone.Plain))
                QuickCamera.RequestSlowMotion(speed);
        }

        Widgets.Reason(block ?? "");
    }

    private static string Explain(QuickMode mode) => mode switch
    {
        QuickMode.Flyby => "A fixed camera ahead of the vehicle. The vehicle flies past and the camera turns to follow it.",
        QuickMode.Chase => "Behind and above the vehicle along its direction of flight, with a level horizon.",
        QuickMode.Orbit => "Circles the vehicle with a level horizon.",
        QuickMode.Onboard => "A camera on a part of the vehicle that looks along the stack.",
        QuickMode.GroundTracker => "A long lens on the ground that follows the vehicle, like a launch tracking camera.",
        _ => "",
    };
}
