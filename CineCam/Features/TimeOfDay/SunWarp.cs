using System.Diagnostics;
using System.Globalization;
using Brutal.Logging;
using Brutal.Numerics;
using CineCam.Features.Replay;
using CineCam.Features.Takes;
using CineCam.Patches;
using KSA;

namespace CineCam.Features.TimeOfDay;

// Moves the game time forward to the next time at which the sun at a launch site has a given
// elevation. The game time cannot go back, so the search looks only ahead, and the move uses the
// automatic warp of the game so every orbit and vehicle moves the normal way.
internal static class SunWarp
{
    private const double CoarseStepSeconds = 600.0;
    private const double WarpTimeoutSeconds = 600.0;
    private const double RetryDelaySeconds = 2.0;
    private const int MaxWarpTries = 20;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static bool _running;
    private static UniverseTime _target;
    private static double _started;
    private static double _issued;
    private static double _lastLog;
    private static int _tries;

    public static bool IsRunning => _running;

    #region Start and stop

    /// <param name="elevationDeg">The sun elevation to reach, in degrees above the horizon.</param>
    /// <param name="setting">True to wait for the sun to go down through the elevation, false for the rise.</param>
    /// <param name="azimuthDeg">When set, the time at which the sun is closest to this compass direction at that elevation.</param>
    /// <param name="searchDays">How far ahead the search looks, in game days.</param>
    public static void Start(double elevationDeg, bool setting, double? azimuthDeg, double searchDays)
    {
        if (TakeRunner.Refused("move of the game time", CineCamPatches.FrameBlock))
            return;

        if (TakeRunner.IsRunning || ReplaySession.IsBusy)
        {
            DefaultCategory.Log.Warning("[CineCam] Finish the active take or replay operation before changing the time of day.");
            return;
        }

        if (_running)
        {
            DefaultCategory.Log.Warning("[CineCam] The game time is moving already.");
            return;
        }

        if (Program.Editor != null)
        {
            DefaultCategory.Log.Warning("[CineCam] The game time does not move in the vehicle editor.");
            return;
        }

        if (!TryFindSite(out Celestial? body, out LandmarkReference? site) || body == null || site == null)
        {
            DefaultCategory.Log.Error("[CineCam] No launch site for the sun search. Name one in the shot file (\"site\") or fly a vehicle near a Landmark.");
            return;
        }

        UniverseTime now = Universe.GetElapsedTime();
        if (!FindSunTime(body, site, now, elevationDeg, setting, azimuthDeg, searchDays, out UniverseTime target))
            return;
        if ((target - now).Seconds() < 1.0)
        {
            DefaultCategory.Log.Info("[CineCam] The sun has the wanted elevation now, so the game time stays.");
            return;
        }

        if (Program.ControlledVehicle != null)
            DefaultCategory.Log.Warning("[CineCam] A vehicle exists, and a long warp with a vehicle on the pad is not tested.");

        _target = target;
        _started = Clock.Elapsed.TotalSeconds;
        _lastLog = _started;
        _tries = 0;
        _running = true;
        IssueWarp(_started);
    }

    public static void Cancel()
    {
        if (!_running)
            return;
        _running = false;
        InputEvents.AutoWarpBuffer.Add(new InputEvents.AutoWarpData { StopWarp = true });
        DefaultCategory.Log.Info("[CineCam] The move of the game time was stopped.");
    }

    #endregion

    #region Warp

    public static void Tick()
    {
        if (!_running)
            return;

        double now = Clock.Elapsed.TotalSeconds;
        double elapsed = Universe.GetElapsedTime().Seconds();
        double target = _target.Seconds();
        if (elapsed >= target - 0.5)
        {
            _running = false;
            DefaultCategory.Log.Info(string.Create(CultureInfo.InvariantCulture,
                $"[CineCam] The game time reached the target after {now - _started:F1} s: {elapsed / 86400.0:F4} days."));
            return;
        }

        if (now - _lastLog > 10.0)
        {
            _lastLog = now;
            DefaultCategory.Log.Info(string.Create(CultureInfo.InvariantCulture,
                $"[CineCam] Warp: game time {elapsed / 86400.0:F4} days of {target / 86400.0:F4}, speed {Universe.GetSimulationSpeed():G4}."));
        }

        if (now - _started > WarpTimeoutSeconds)
        {
            _running = false;
            InputEvents.AutoWarpBuffer.Add(new InputEvents.AutoWarpData { StopWarp = true });
            DefaultCategory.Log.Error("[CineCam] The warp did not reach the target in 10 minutes. It was stopped.");
            return;
        }

        // The game stops an automatic warp by itself, for example for an alert or an open menu.
        if (!Universe.IsAutoWarpActive && now - _issued > RetryDelaySeconds)
        {
            if (_tries >= MaxWarpTries)
            {
                _running = false;
                DefaultCategory.Log.Error($"[CineCam] The game stopped the warp {_tries} times. The game time stays where it is.");
                return;
            }

            IssueWarp(now);
        }
    }

    private static void IssueWarp(double now)
    {
        _issued = now;
        _tries++;
        InputEvents.AutoWarpBuffer.Add(new InputEvents.AutoWarpData
        {
            StopWarp = false,
            WarpToTime = _target,
            SimTimeMargin = 0.0,
            IsUncapped = true,
        });
        DefaultCategory.Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"[CineCam] Warp to game time {_target.Seconds() / 86400.0:F4} days (try {_tries})."));
    }

    #endregion

    #region Sun search

    // The order is the site of the shot file, the Landmark nearest to the controlled vehicle, and
    // the first launch pad of the body.
    private static bool TryFindSite(out Celestial? body, out LandmarkReference? site)
    {
        body = null;
        site = null;
        Vehicle? vehicle = Program.ControlledVehicle;
        body = vehicle?.Parent as Celestial ?? Universe.CurrentSystem?.HomeBody as Celestial;
        if (body == null)
            return false;

        string? named = ShotFileStore.Load(out _)?.Site;
        if (!string.IsNullOrWhiteSpace(named))
        {
            site = body.GetLocation(named) as LandmarkReference;
            if (site != null)
                return true;
            DefaultCategory.Log.Warning($"[CineCam] The shot file names the site \"{named}\", but {body.Id} has no such Landmark.");
        }

        if (vehicle != null && vehicle.Parent is Celestial)
            site = SiteFrame.Nearest(body, vehicle, out _);
        site ??= SiteFrame.FirstLaunchPad(body);
        return site != null;
    }

    private static bool FindSunTime(Celestial body, LandmarkReference site, UniverseTime from,
        double elevationDeg, bool setting, double? azimuthDeg, double searchDays, out UniverseTime result)
    {
        result = from;
        site.GetAxesCcf(out double3 up, out double3 east, out double3 north);
        double t0 = from.Seconds() + 60.0;
        double t1 = from.Seconds() + searchDays * 86400.0;
        double3? start = SunDirCcfAt(body, from);
        if (start == null)
        {
            DefaultCategory.Log.Error("[CineCam] The sun is not on the orbit chain of the body, so its direction cannot be computed ahead.");
            return false;
        }

        // Checks the computed sun direction against the positions of the game now.
        double3 actual = CurrentSunDirCcf(body);
        double modelError = Math.Acos(Math.Clamp(double3.Dot(start.Value, actual), -1.0, 1.0)) * 180.0 / Math.PI;
        DefaultCategory.Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"[CineCam] Sun at \"{site.Key}\" now: elevation {Elevation(actual, up):F2}, azimuth {Azimuth(actual, east, north):F2} deg. The computed direction is {modelError:F4} deg off."));
        if (modelError > 0.5)
            DefaultCategory.Log.Warning("[CineCam] The computed sun direction is more than 0.5 deg off the one of the game.");

        if (azimuthDeg is double wantedAzimuth)
            result = SearchDirection(body, up, east, north, t0, t1, elevationDeg, wantedAzimuth, searchDays);
        else
            result = SearchCrossing(body, up, t0, t1, elevationDeg, setting, searchDays);

        double3 sun = SunDirCcfAt(body, result)!.Value;
        DefaultCategory.Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"[CineCam] Sun search: game time {result.Seconds() / 86400.0:F4} days ({(result - from).Seconds() / 86400.0:F1} days on), sun elevation {Elevation(sun, up):F2}, azimuth {Azimuth(sun, east, north):F2} deg."));
        return true;
    }

    private static UniverseTime SearchDirection(Celestial body, double3 up, double3 east, double3 north,
        double t0, double t1, double elevationDeg, double azimuthDeg, double searchDays)
    {
        double elR = elevationDeg * Math.PI / 180.0;
        double azR = azimuthDeg * Math.PI / 180.0;
        double3 wanted = up * Math.Sin(elR) + (north * Math.Cos(azR) + east * Math.Sin(azR)) * Math.Cos(elR);
        double Off(double t) => Math.Acos(Math.Clamp(double3.Dot(SunDirCcfAt(body, new UniverseTime(t))!.Value, wanted), -1.0, 1.0));

        double best = t0;
        double bestOff = double.MaxValue;
        for (double t = t0; t <= t1; t += CoarseStepSeconds)
        {
            double off = Off(t);
            if (off < bestOff)
            {
                bestOff = off;
                best = t;
            }
        }

        // A golden section search near the best coarse step refines the time to well below a second.
        double a = Math.Max(t0, best - CoarseStepSeconds);
        double b = best + CoarseStepSeconds;
        const double g = 0.6180339887498949;
        double c = b - g * (b - a);
        double d = a + g * (b - a);
        for (int i = 0; i < 60; i++)
        {
            if (Off(c) < Off(d))
                b = d;
            else
                a = c;
            c = b - g * (b - a);
            d = a + g * (b - a);
        }

        var result = new UniverseTime((a + b) * 0.5);
        double offDeg = Off(result.Seconds()) * 180.0 / Math.PI;
        if (offDeg > 1.0)
            DefaultCategory.Log.Warning(string.Create(CultureInfo.InvariantCulture,
                $"[CineCam] In {searchDays:F0} days the sun comes no closer than {offDeg:F2} deg to elevation {elevationDeg:F1}, azimuth {azimuthDeg:F1}."));
        return result;
    }

    private static UniverseTime SearchCrossing(Celestial body, double3 up, double t0, double t1,
        double elevationDeg, bool setting, double searchDays)
    {
        double El(double t) => Elevation(SunDirCcfAt(body, new UniverseTime(t))!.Value, up) - elevationDeg;

        double prevT = t0;
        double prev = El(t0);
        double highT = t0;
        double high = prev;
        for (double t = t0 + CoarseStepSeconds; t <= t1; t += CoarseStepSeconds)
        {
            double v = El(t);
            if (v > high)
            {
                high = v;
                highT = t;
            }

            bool cross = setting ? prev >= 0.0 && v < 0.0 : prev < 0.0 && v >= 0.0;
            if (cross)
            {
                double a = prevT;
                double b = t;
                for (int i = 0; i < 50; i++)
                {
                    double m = (a + b) * 0.5;
                    double vm = El(m);
                    bool before = setting ? vm >= 0.0 : vm < 0.0;
                    if (before)
                        a = m;
                    else
                        b = m;
                }

                return new UniverseTime((a + b) * 0.5);
            }

            prevT = t;
            prev = v;
        }

        DefaultCategory.Log.Warning(string.Create(CultureInfo.InvariantCulture,
            $"[CineCam] The sun does not {(setting ? "set" : "rise")} through {elevationDeg:F1} deg at this site in {searchDays:F0} days. The time of the highest sun ({high + elevationDeg:F2} deg) is used."));
        return new UniverseTime(highT);
    }

    // The sun direction in CCF at a future game time. It sums the orbit positions from the body up
    // to the sun, each Orbit.GetStateVectorsAt in the CCI frame of its parent and turned into the
    // ecliptic axes by GetCci2Cce, and applies the rotation of the body at that time.
    private static double3? SunDirCcfAt(Celestial body, UniverseTime time)
    {
        if (Universe.WorldSun is not { } sun)
            return null;
        double3 offset = double3.Zero;
        object node = body;
        for (int i = 0; i < 8 && !ReferenceEquals(node, sun) && node is IOrbiter orbiter && orbiter.Orbit != null; i++)
        {
            offset += orbiter.Orbit.GetStateVectorsAt(time).PositionCci.Transform(orbiter.Orbit.Parent.GetCci2Cce());
            node = orbiter.Orbit.Parent;
        }

        if (!ReferenceEquals(node, sun) || offset.Length() < 1.0)
            return null;
        return (-offset).Normalized().Transform(body.GetCce2Ccf(time));
    }

    private static double3 CurrentSunDirCcf(Celestial body)
    {
        Astronomical sun = Universe.WorldSun ?? throw new InvalidOperationException("Universe.WorldSun is null");
        return (sun.GetPositionEcl() - body.GetPositionEcl()).Normalized().Transform(body.GetCce2Ccf());
    }

    private static double Elevation(double3 dir, double3 up) =>
        Math.Asin(Math.Clamp(double3.Dot(dir, up), -1.0, 1.0)) * 180.0 / Math.PI;

    private static double Azimuth(double3 dir, double3 east, double3 north)
    {
        double az = Math.Atan2(double3.Dot(dir, east), double3.Dot(dir, north)) * 180.0 / Math.PI;
        return az < 0.0 ? az + 360.0 : az;
    }

    #endregion
}
