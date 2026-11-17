using System.Text;

namespace CineCam.Director;

/// <summary>The deflection of an engine gimbal against its limit.</summary>
public static class GimbalMath
{
    /// <summary>A gimbal at this share of its limit or more is at the limit.</summary>
    public const double LimitShare = 0.98;

    /// <summary>The event comes again only after every gimbal was below this share.</summary>
    public const double RearmShare = 0.9;

    /// <summary>
    /// The deflection as a share of the limit, 1 at the limit. A gimbal that the game constrains to a circle
    /// (Gimbal.ConstrainStateToCircle) is at its limit on the ellipse of the two maximum angles, and another one on
    /// the limit of either axis. An axis with a maximum of 0 does not move and does not count.
    /// </summary>
    public static double Share(double angleY, double angleZ, double maxY, double maxZ, bool circle)
    {
        double y = maxY > 0.0 && double.IsFinite(angleY) ? angleY / maxY : 0.0;
        double z = maxZ > 0.0 && double.IsFinite(angleZ) ? angleZ / maxZ : 0.0;
        return circle && maxY > 0.0 && maxZ > 0.0 ? Math.Sqrt(y * y + z * z) : Math.Max(Math.Abs(y), Math.Abs(z));
    }
}

/// <summary>
/// Finds the peaks of the dynamic pressure, the moments of max Q. A peak counts when the pressure has fallen a clear
/// margin below it, and its time is the time of the highest sample. A peak below <see cref="MinPeak"/>, a sample with
/// ground contact and a pressure that only falls since the tracking started (a take that starts after max Q) give no
/// event. After a max Q the next one needs the pressure below half of that peak first, so a reentry after an ascent
/// has its own max Q while the small changes of a flight at a steady speed give no more.
/// </summary>
public sealed class MaxQTracker
{
    /// <summary>Pascals. About the pressure of 40 m/s at sea level on Earth.</summary>
    public const double MinPeak = 1000.0;

    /// <summary>The share of the peak that the pressure must fall below it.</summary>
    public const double DropShare = 0.03;

    /// <summary>Pascals that the pressure must fall below the peak at least.</summary>
    public const double MinDrop = 100.0;

    /// <summary>The share of the last peak below which the pressure must fall before the next max Q.</summary>
    public const double RearmShare = 0.5;

    private bool _armed = true;
    private bool _tracking;
    private bool _rose;
    private double _peak;
    private double _peakTime;
    private double _lastPeak;

    public bool Armed => _armed;

    /// <summary>The highest pressure since the tracking started, or 0.</summary>
    public double Peak => _tracking ? _peak : 0.0;

    public double? PeakTime => _tracking ? _peakTime : null;

    /// <summary>Returns the time and the pressure of a max Q when this sample shows it.</summary>
    public (double Time, double Pascals)? Update(double time, double pascals, bool contact)
    {
        if (!double.IsFinite(pascals))
            return null;
        if (!_armed)
        {
            if (pascals >= _lastPeak * RearmShare)
                return null;
            _armed = true;
            _tracking = false;
        }

        if (contact)
        {
            _tracking = false;
            return null;
        }

        if (!_tracking || pascals > _peak)
        {
            _rose = _tracking;
            _tracking = true;
            _peak = pascals;
            _peakTime = time;
            return null;
        }

        if (_peak < MinPeak || _peak - pascals < Math.Max(_peak * DropShare, MinDrop))
            return null;

        _armed = false;
        _tracking = false;
        _lastPeak = _peak;
        return _rose ? (_peakTime, _peak) : null;
    }

    /// <summary>
    /// Seconds until the peak of a pressure that rises at <paramref name="rate"/> and whose rate changes by
    /// <paramref name="rateChange"/> per second, or null when the pressure does not rise toward a peak.
    /// </summary>
    public static double? SecondsToPeak(double rate, double rateChange)
    {
        if (!(rate > 1e-9) || !(rateChange < -1e-9))
            return null;
        return rate / -rateChange;
    }
}

public static class EventText
{
    /// <summary>"ProbeCoreUnpowered" gives "probe core unpowered".</summary>
    public static string Words(string name)
    {
        var text = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c) && i > 0)
                text.Append(' ');
            text.Append(char.ToLowerInvariant(c));
        }

        return text.ToString();
    }

    /// <summary>A detail for the replay file, which keeps at most <paramref name="limit"/> characters.</summary>
    public static string? Clip(string? detail, int limit = 200)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return null;
        string trimmed = detail.Trim();
        return trimmed.Length <= limit ? trimmed : trimmed[..limit];
    }
}
