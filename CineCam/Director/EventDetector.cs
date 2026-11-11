using System.Globalization;

namespace CineCam.Director;

/// <summary>
/// What the game side reads from the controlled vehicle in one frame. A sequence whose parts all
/// leave the vehicle is removed at once, so only <see cref="Fired"/> shows that it fired.
/// <see cref="FreshEngines"/> counts engines not seen running before in this take, so hot staging
/// shows. <see cref="RadialSpeed"/> is in meters per second away from the center of <see cref="Body"/>.
/// <see cref="StableOrbit"/> on a body without an atmosphere also needs the periapsis above the
/// highest terrain. <see cref="VehicleChanged"/> is true in the first sample after the take moved to
/// another vehicle object, which the game makes when vehicles dock or undock.
/// </summary>
public readonly record struct VehicleSample(
    bool EngineOn,
    bool Contact,
    int ActiveSequence,
    double Altitude,
    double Speed,
    IReadOnlyList<int>? Fired = null,
    int FreshEngines = 0,
    string? Body = null,
    double RadialSpeed = 0.0,
    bool StableOrbit = false,
    bool OutsideAtmosphere = false,
    int DockedPorts = 0,
    int Marks = 0,
    bool VehicleChanged = false);

public sealed record DetectedEvent(string Name, double TakeTime, string Detail);

/// <summary>
/// Turns the per-frame samples of the vehicle into the events of the shot file, each reported once,
/// and predicts the altitude and speed events so that a shot can start before them.
/// </summary>
public sealed class EventDetector
{
    // The rate of altitude and speed is smoothed over about this many seconds.
    private const double RateTimeConstant = 0.5;

    // An engine that runs for less time than this does not count toward a burnout.
    private const double MinBurnSeconds = 1.0;

    // Engines that light within this many seconds of the last counted ignition belong to the same
    // ignition, because the engines of one stage do not all start in the same frame.
    private const double IgnitionGroupSeconds = 5.0;

    // The lift-off needs this many meters of rise above the last altitude with ground contact.
    private const double LiftoffRise = 0.5;

    // A landing needs this many seconds without ground contact before it, so a bounce or a
    // contact that flickers for a frame does not count as a new landing.
    private const double LandedAirSeconds = 1.0;

    // The radial speed must pass this many meters per second on the other side of zero before
    // an apsis counts, so the noise of a nearly circular orbit gives no passages.
    private const double ApsisDeadband = 0.1;

    // A docked port that goes away within this many seconds of a fired sequence left with a
    // stage, so it does not count as an undocking.
    private const double UndockAfterStageSeconds = 1.0;

    private readonly List<double> _altitudeTargets = new();
    private readonly List<double> _speedTargets = new();
    private readonly Dictionary<string, double> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _predictions = new(StringComparer.Ordinal);

    private bool _started;
    private double _lastTime;
    private bool _wasEngineOn;
    private double _groundAltitude;
    private int _lastSequence;
    private double _lastAltitude;
    private double _lastSpeed;
    private double _altitudeRate;
    private double _speedRate;
    private double _engineOnSince;
    private double _lastIgnition;
    private int _ignitions;
    private int _burnouts;
    private bool _wasContact;
    private double _airSince;
    private int _landings;
    private string? _body;
    private int _soiChanges;
    private int _radialSign;
    private int _apoapses;
    private int _periapses;
    private int _docked;
    private int _dockings;
    private int _undockings;
    private double _lastSequenceTime = double.NegativeInfinity;
    private int _marks;

    public EventDetector(IEnumerable<EventName> watched)
    {
        foreach (EventName name in watched)
        {
            if (name.Kind == EventKind.Altitude && !_altitudeTargets.Contains(name.Value))
                _altitudeTargets.Add(name.Value);
            else if (name.Kind == EventKind.Speed && !_speedTargets.Contains(name.Value))
                _speedTargets.Add(name.Value);
        }
    }

    /// <summary>Canonical event names and the take time at which each was first seen.</summary>
    public IReadOnlyDictionary<string, double> Seen => _seen;

    /// <summary>Canonical names of altitude and speed events and the seconds until each, from the last update.</summary>
    public IReadOnlyDictionary<string, double> Predictions => _predictions;

    public static List<EventName> Watched(Take take, IReadOnlyDictionary<string, string> aliases)
    {
        var result = new List<EventName>();
        foreach (Trigger trigger in take.Shots.Select(s => s.At).Concat(take.Speed.Select(k => k.At)).Concat(take.Actions.Select(a => a.At)))
        {
            if (trigger.Kind != TriggerKind.Event)
                continue;
            if (EventName.TryResolve(trigger.Event, aliases, out EventName name, out _))
                result.Add(name);
        }

        return result;
    }

    /// <param name="takeTime">Seconds since the take started. It must not go back.</param>
    public IReadOnlyList<DetectedEvent> Update(double takeTime, in VehicleSample sample)
    {
        var found = new List<DetectedEvent>();
        double dt = _started ? Math.Max(takeTime - _lastTime, 0.0) : 0.0;

        if (!_started)
        {
            _started = true;
            _lastSequence = sample.ActiveSequence;
            _lastAltitude = sample.Altitude;
            _lastSpeed = sample.Speed;
            if (sample.EngineOn)
            {
                _engineOnSince = takeTime;
                CountIgnition(found, takeTime, "already on at the start");
            }

            _groundAltitude = sample.Altitude;
            if (!sample.Contact)
                Add(found, "liftoff", takeTime, "already flying at the start");
            _airSince = takeTime;
            _body = sample.Body;
            _radialSign = RadialSign(sample.RadialSpeed);
            _docked = sample.DockedPorts;
        }
        else
        {
            // Another vehicle object has its own sequence list, so the sequence count starts
            // again from its active sequence and the jump gives no events.
            if (sample.VehicleChanged)
                _lastSequence = sample.ActiveSequence;

            if (sample.EngineOn && !_wasEngineOn)
            {
                _engineOnSince = takeTime;
                CountIgnition(found, takeTime, "");
            }
            else if (sample.EngineOn && sample.FreshEngines > 0 && takeTime - _lastIgnition >= IgnitionGroupSeconds)
            {
                CountIgnition(found, takeTime, "another engine lit while the engines ran");
            }

            if (!sample.EngineOn && _wasEngineOn && takeTime - _engineOnSince >= MinBurnSeconds)
            {
                _burnouts++;
                Add(found, EventName.Counted("burnout", _burnouts), takeTime, "");
            }

            // A vehicle that stands on a pad can lose its contact for a frame or two, for example
            // when the sim speed changes, so the lift-off also needs a rise above the last
            // altitude with contact.
            if (sample.Contact)
                _groundAltitude = sample.Altitude;
            else if (sample.Altitude - _groundAltitude >= LiftoffRise)
                Add(found, "liftoff", takeTime, "");

            if (sample.Contact && !_wasContact && _seen.ContainsKey("liftoff") && takeTime - _airSince >= LandedAirSeconds)
            {
                _landings++;
                Add(found, EventName.Counted("landed", _landings), takeTime, "");
            }

            if (!sample.Contact && _wasContact)
                _airSince = takeTime;

            if ((sample.Fired?.Count ?? 0) > 0 || sample.ActiveSequence > _lastSequence)
                _lastSequenceTime = takeTime;

            if (sample.Fired != null && !sample.VehicleChanged)
            {
                foreach (int fired in sample.Fired)
                {
                    int from = Math.Min(_lastSequence + 1, fired);
                    for (int n = from; n <= Math.Min(fired, from + 999); n++)
                        Add(found, "sequence:" + n.ToString(CultureInfo.InvariantCulture), takeTime,
                            n == fired && sample.ActiveSequence < fired ? "fired; its parts left the vehicle at once" : "");
                    _lastSequence = Math.Max(_lastSequence, fired);
                }
            }

            if (sample.ActiveSequence > _lastSequence)
            {
                int first = _lastSequence + 1;
                int last = Math.Min(sample.ActiveSequence, first + 999);
                for (int n = first; n <= last; n++)
                    Add(found, "sequence:" + n.ToString(CultureInfo.InvariantCulture), takeTime, "");
            }

            UpdateSoi(found, takeTime, sample);
            UpdateApsis(found, takeTime, sample);
            UpdateDocking(found, takeTime, sample);

            if (dt > 1e-6)
            {
                double blend = 1.0 - Math.Exp(-dt / RateTimeConstant);
                _altitudeRate += ((sample.Altitude - _lastAltitude) / dt - _altitudeRate) * blend;
                _speedRate += ((sample.Speed - _lastSpeed) / dt - _speedRate) * blend;
            }
        }

        for (int i = 0; i < sample.Marks; i++)
        {
            _marks++;
            Add(found, EventName.Counted("mark", _marks), takeTime, "");
        }

        foreach (double target in _altitudeTargets)
            Reach(found, "altitude", target, sample.Altitude, _altitudeRate, takeTime);
        foreach (double target in _speedTargets)
            Reach(found, "speed", target, sample.Speed, _speedRate, takeTime);

        _wasEngineOn = sample.EngineOn;
        _wasContact = sample.Contact;
        _lastSequence = Math.Max(_lastSequence, sample.ActiveSequence);
        _lastAltitude = sample.Altitude;
        _lastSpeed = sample.Speed;
        _lastTime = takeTime;
        return found;
    }

    // The apsis watch starts again on a change of body, because a radial speed about another body
    // says nothing about the orbit before.
    private void UpdateSoi(List<DetectedEvent> found, double takeTime, in VehicleSample sample)
    {
        if (sample.Body == null)
            return;
        if (_body != null && !string.Equals(_body, sample.Body, StringComparison.Ordinal))
        {
            _soiChanges++;
            Add(found, EventName.Counted("soi", _soiChanges), takeTime, $"from {_body} to {sample.Body}");
            _radialSign = 0;
        }

        _body = sample.Body;
    }

    // A passage counts only in free flight on a stable orbit outside the atmosphere, so the top of a
    // hop and a sign change during a burn (a circularization) give no event. The sign is still
    // followed in a burn, so the first passage after it is found against the sign at its end.
    private void UpdateApsis(List<DetectedEvent> found, double takeTime, in VehicleSample sample)
    {
        int sign = RadialSign(sample.RadialSpeed);
        if (sign == 0)
            return;
        bool free = !sample.EngineOn && sample.StableOrbit && sample.OutsideAtmosphere;
        if (_radialSign != 0 && sign != _radialSign && free)
        {
            if (sign < 0)
            {
                _apoapses++;
                Add(found, EventName.Counted("apoapsis", _apoapses), takeTime, "");
            }
            else
            {
                _periapses++;
                Add(found, EventName.Counted("periapsis", _periapses), takeTime, "");
            }
        }

        _radialSign = sign;
    }

    private void UpdateDocking(List<DetectedEvent> found, double takeTime, in VehicleSample sample)
    {
        int before = _docked;
        _docked = sample.DockedPorts;
        string detail = string.Create(CultureInfo.InvariantCulture, $"docked ports {before} to {sample.DockedPorts}");
        if (sample.DockedPorts > before)
        {
            _dockings++;
            Add(found, EventName.Counted("docked", _dockings), takeTime, detail);
        }
        else if (sample.DockedPorts < before && takeTime - _lastSequenceTime > UndockAfterStageSeconds)
        {
            _undockings++;
            Add(found, EventName.Counted("undocked", _undockings), takeTime, detail);
        }
    }

    private static int RadialSign(double radialSpeed) =>
        radialSpeed > ApsisDeadband ? 1 : radialSpeed < -ApsisDeadband ? -1 : 0;

    private void Reach(List<DetectedEvent> found, string kind, double target, double value, double rate, double takeTime)
    {
        string name = new EventName(kind == "altitude" ? EventKind.Altitude : EventKind.Speed, target).Canonical;
        if (_seen.ContainsKey(name))
            return;
        if (value >= target)
        {
            Add(found, name, takeTime, "");
            _predictions.Remove(name);
            return;
        }

        double? seconds = EventPrediction.SecondsUntil(value, target, rate);
        if (seconds.HasValue)
            _predictions[name] = seconds.Value;
        else
            _predictions.Remove(name);
    }

    private void CountIgnition(List<DetectedEvent> found, double takeTime, string detail)
    {
        _ignitions++;
        _lastIgnition = takeTime;
        Add(found, EventName.Counted("ignition", _ignitions), takeTime, detail);
    }

    private void Add(List<DetectedEvent> found, string name, double takeTime, string detail)
    {
        if (_seen.ContainsKey(name))
            return;
        _seen[name] = takeTime;
        found.Add(new DetectedEvent(name, takeTime, detail));
    }
}
