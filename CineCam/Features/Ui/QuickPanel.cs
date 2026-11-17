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
        Widgets.Muted(UiText.MessageText("text.652646332f55", "For a planned sequence of cameras, use the Editor."));
    }

    // Must not touch ImGui. It runs when the mod unloads.
    public static void Shutdown()
    {
        _shown = QuickMode.Flyby;
    }

    private static void DrawModes(QuickMode? running)
    {
        Widgets.Section(UiText.MessageText("text.03494b0d1f80", "Camera"));
        for (int i = 0; i < Modes.Length; i++)
        {
            QuickMode mode = Modes[i];
            if (i > 0)
                Widgets.SameLine();
            string? block = QuickCamera.StartBlock(mode);
            string label = UiText.Concat(UiText.Concat(UiText.Upper(QuickCamera.Describe(mode)), "##quick-"), mode);
            if (Widgets.Button(label, block == null, block ?? Explain(mode), running == mode ? Widgets.Tone.Primary : Widgets.Tone.Plain))
            {
                _shown = mode;
                QuickCamera.RequestStart(mode);
            }
        }

        Widgets.SameLine();
        if (Widgets.Button(UiText.MessageText("text.04dedf8afeb8", "STOP"), running != null, running != null
                ? UiText.MessageText("text.6d8cfa168e55", "Gives back the camera, the field of view and the sim speed from before.")
                : UiText.MessageText("text.627a7b1b7e0e", "No quick camera runs."), Widgets.Tone.Danger))
            QuickCamera.RequestStop();

        string? common = QuickCamera.StartBlock(QuickMode.Chase);
        if (running == null && common != null)
            Widgets.Reason(common);
    }

    private static void DrawState(QuickMode? running)
    {
        if (running is QuickMode mode)
            Widgets.Label(UiText.Format("text.3e90e2a3febf", $"On: {QuickCamera.Describe(mode)}"), Widgets.GoodColor);
        else
            Widgets.Muted(UiText.MessageText("text.6959e0167014", "Off. Click a camera to start it. A take, a shot preview or a replay stops it."));
        if (QuickCamera.Status.Length > 0 && !(running is QuickMode on && QuickCamera.Status == UiText.Format("text.dded112742f6", $"{QuickCamera.Describe(on)} on.")))
            Widgets.Muted(QuickCamera.Status);
    }

    private static void DrawModeSettings(QuickMode mode, QuickMode? running)
    {
        QuickOptions o = QuickCamera.Options;
        bool active = running == mode;
        Widgets.Section(UiText.Format("text.d6ed4f5cff1d", $"{QuickCamera.Describe(mode)} settings"));
        switch (mode)
        {
            case QuickMode.Flyby:
            {
                if (Widgets.Button(UiText.MessageText("text.6c34f91eda80", "NEXT FLYBY"), active, active ? UiText.MessageText("text.d46016c0ade3", "Places a new point ahead of the vehicle.") : UiText.MessageText("text.631d507ff0b8", "Start the flyby first.")))
                    QuickCamera.RequestNextFlyby();
                Widgets.Reason(active ? "" : UiText.MessageText("text.631d507ff0b8", "Start the flyby first."));
                bool autoNext = o.FlybyAutoNext;
                if (Widgets.Check(UiText.MessageText("text.f690345e1a2e", "Place the next point when the vehicle is past"), ref autoNext))
                    o.FlybyAutoNext = autoNext;
                double pass = o.FlybyPassSeconds;
                if (Widgets.Num(UiText.MessageText("text.a9ca9caa0b92", "Seconds until the vehicle passes"), ref pass, 0.1f, 1.0, 60.0, "%.1f",
                        UiText.MessageText("text.5e698331c80f", "The vehicle reaches the point after about this time.")))
                    o.FlybyPassSeconds = pass;
                double side = o.FlybySide;
                if (Widgets.Num(UiText.MessageText("text.c69d068754cf", "Side distance (m)"), ref side, 1.0f, 0.0, 5000.0, "%.0f",
                        UiText.MessageText("text.5ee0ba7d6ffc", "The distance from the line of flight. 0 picks it from the speed and the size of the vehicle.")))
                    o.FlybySide = side;
                double orbitPass = o.FlybyOrbitPassSpeed;
                if (Widgets.Num(UiText.MessageText("text.29aa0456f3e4", "Pass speed in orbit (m/s)"), ref orbitPass, 0.5f, 1.0, 500.0, "%.1f",
                        UiText.MessageText("text.d40ccce6e4c8", "In orbit the point falls with the vehicle, and the vehicle passes it at this speed.")))
                    o.FlybyOrbitPassSpeed = orbitPass;
                string AnchorHelp = UiText.MessageText("text.6f021966834c", "In the air or near the ground the point is fixed to the ground. Above the atmosphere (above 10 km on a body without air) it falls freely in orbit, so the vehicle passes it slowly.");
                if (active && QuickCamera.FlybyAnchorNow is FlybyAnchor anchor)
                    Widgets.StatusLine(anchor == FlybyAnchor.Orbit ? UiText.MessageText("text.5bbb1cb680e5", "The point falls with the vehicle in orbit.") : UiText.MessageText("text.39ca012b64d7", "The point is fixed to the ground."),
                        Widgets.MutedColor, AnchorHelp);
                else
                    Widgets.StatusLine(UiText.MessageText("text.14422b7d280b", "The point is on the ground, or in orbit above the air."), Widgets.MutedColor, AnchorHelp);
                break;
            }
            case QuickMode.Chase:
            {
                double distance = o.ChaseDistance;
                if (Widgets.Num(UiText.MessageText("text.dfcdcc6ca866", "Distance (vehicle sizes)"), ref distance, 0.05f, 0.5, 50.0, "%.2f"))
                    o.ChaseDistance = distance;
                double height = o.ChaseHeight;
                if (Widgets.Num(UiText.MessageText("text.56326dabdd5d", "Height (vehicle sizes)"), ref height, 0.05f, -5.0, 20.0, "%.2f"))
                    o.ChaseHeight = height;
                break;
            }
            case QuickMode.Orbit:
            {
                double distance = o.OrbitDistance;
                if (Widgets.Num(UiText.MessageText("text.dfcdcc6ca866", "Distance (vehicle sizes)"), ref distance, 0.05f, 0.5, 50.0, "%.2f"))
                    o.OrbitDistance = distance;
                double elevation = o.OrbitElevation;
                if (Widgets.Num(UiText.MessageText("text.fb95af8c33d2", "Elevation (deg)"), ref elevation, 0.5f, -80.0, 85.0, "%.0f"))
                    o.OrbitElevation = elevation;
                double rate = o.OrbitDegPerSec;
                if (Widgets.Num(UiText.MessageText("text.c7c0dcb3b8fc", "Speed (deg/s)"), ref rate, 0.2f, -90.0, 90.0, "%.1f", UiText.MessageText("text.5ce53e3cbf8b", "A negative speed turns the other way.")))
                    o.OrbitDegPerSec = rate;
                break;
            }
            case QuickMode.Onboard:
            {
                if (Widgets.Button(UiText.MessageText("text.997592529e14", "PREVIOUS PART"), active, active ? null : UiText.MessageText("text.077816171533", "Start the onboard camera first.")))
                    QuickCamera.RequestPartStep(-1);
                Widgets.SameLine();
                if (Widgets.Button(UiText.MessageText("text.635b2244dabe", "NEXT PART"), active, active ? null : UiText.MessageText("text.077816171533", "Start the onboard camera first.")))
                    QuickCamera.RequestPartStep(1);
                if (active && QuickCamera.OnboardPartCount > 0)
                    Widgets.Muted(UiText.Format("text.c1a96ee28a1a", $"Part {QuickCamera.OnboardPartIndex + 1} of {QuickCamera.OnboardPartCount}: {QuickCamera.OnboardPartName}"));
                bool nose = o.OnboardTowardNose;
                if (Widgets.Check(UiText.MessageText("text.7d8b599ff266", "Look toward the nose"), ref nose, UiText.MessageText("text.4047529f3a81", "Else the camera looks down the stack.")))
                    o.OnboardTowardNose = nose;
                break;
            }
            case QuickMode.GroundTracker:
            {
                double distance = o.TrackerDistance;
                if (Widgets.Num(UiText.MessageText("text.5d32fa547f0a", "Distance from the flight path (m)"), ref distance, 10.0f, 50.0, 20000.0, "%.0f",
                        UiText.MessageText("text.523c52df3212", "Used when the tracker is placed.")))
                    o.TrackerDistance = distance;
                Vehicle? vehicle = Program.ControlledVehicle;
                string? block = vehicle == null || vehicle.IsDisposed ? UiText.MessageText("text.d2d6a7832312", "Select a vehicle in flight.")
                    : QuickMath.TrackerBlock(vehicle.GetBarometricAltitude());
                if (Widgets.Button(UiText.MessageText("text.a0881680fb4c", "PLACE UNDER THE VEHICLE AGAIN"), active && block == null,
                        !active ? UiText.MessageText("text.5c05fd3b59db", "Start the ground tracker first.") : block))
                    QuickCamera.RequestStart(QuickMode.GroundTracker);
                Widgets.Muted(UiText.Format("text.570054a2ca15", $"A long lens on the ground near the point under the vehicle. It works up to {QuickMath.TrackerMaxRange / 1000.0:F0} km."));
                break;
            }
        }
    }

    private static void DrawPicture(QuickMode mode)
    {
        QuickOptions o = QuickCamera.Options;
        Widgets.Section(UiText.MessageText("text.9a1c54f55e7c", "Picture"));
        bool onboard = mode == QuickMode.Onboard;
        bool auto = o.AutoZoom;
        if (Widgets.Check(UiText.MessageText("text.bd6a2bfb0784", "Auto-zoom"), ref auto, UiText.MessageText("text.04eb351373d9", "Keeps the vehicle at the same size in the picture.")))
            o.AutoZoom = auto;
        if (o.AutoZoom && !onboard)
        {
            double share = o.ZoomShare * 100.0;
            if (Widgets.Num(UiText.MessageText("text.d2e85009ab84", "Vehicle size (% of the picture height)"), ref share, 0.5f, 2.0, 95.0, "%.0f"))
                o.ZoomShare = share / 100.0;
        }
        else
        {
            double fov = o.Fov;
            if (Widgets.Num(UiText.MessageText("text.29a85e89b90b", "Field of view (deg)"), ref fov, 0.2f, 5.0, 120.0, "%.1f"))
                o.Fov = fov;
        }

        if (onboard && o.AutoZoom)
            Widgets.Muted(UiText.MessageText("text.e83272723e76", "The onboard camera is fixed to the hull and uses the field of view, not the auto-zoom."));
    }

    private static void DrawSlowMotion()
    {
        Widgets.Section(UiText.MessageText("text.3c18ba95de19", "Slow motion"));
        string? block = QuickCamera.SlowMotionBlock();
        double? current = QuickCamera.SlowMotion;
        for (int i = 0; i < Speeds.Length; i++)
        {
            double speed = Speeds[i];
            if (i > 0)
                Widgets.SameLine();
            bool chosen = current is double c && Math.Abs(c - speed) < 1e-9;
            string label = UiText.Format("", $"{speed:0.##}x##quick-speed-{i}");
            if (Widgets.Button(label, block == null, block ?? UiText.MessageText("text.437e40554c99", "The speed from before comes back when the quick camera stops."),
                    chosen ? Widgets.Tone.Primary : Widgets.Tone.Plain))
                QuickCamera.RequestSlowMotion(speed);
        }

        Widgets.Reason(block ?? "");
    }

    private static string Explain(QuickMode mode) => mode switch
    {
        QuickMode.Flyby => UiText.MessageText("text.965aa845bf59", "A fixed camera ahead of the vehicle. The vehicle flies past and the camera turns to follow it."),
        QuickMode.Chase => UiText.MessageText("text.227f4a08d62d", "Behind and above the vehicle along its direction of flight, with a level horizon."),
        QuickMode.Orbit => UiText.MessageText("text.1b21a02218ae", "Circles the vehicle with a level horizon."),
        QuickMode.Onboard => UiText.MessageText("text.c81b2ea4c826", "A camera on a part of the vehicle that looks along the stack."),
        QuickMode.GroundTracker => UiText.MessageText("text.c69ed40a012f", "A long lens on the ground that follows the vehicle, like a launch tracking camera."),
        _ => "",
    };
}
