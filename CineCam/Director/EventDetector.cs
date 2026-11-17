using System.Globalization;

namespace CineCam.Director;

/// <summary>
/// What the game side reads from the controlled vehicle in one frame. A sequence whose parts all
/// leave the vehicle is removed at once, so only <see cref="Fired"/> shows that it fired.
/// <see cref="FreshEngines"/> counts engines not seen running before in this take, so hot staging
/// shows. <see cref="RadialSpeed"/> is in meters per second away from the center of <see cref="Body"/>.
/// <see cref="StableOrbit"/> on a body without an atmosphere also needs the periapsis above the
/// highest terrain. <see cref="VehicleChanged"/> is true in the first sample after the take moved to
/// another vehicle object, which the game makes when vehicles dock or undock. <see cref="Ground"/> is the height in
/// meters of the lowest corner of the part bounding box over the terrain or the sea under the vehicle (see
/// <see cref="GroundClearance"/>), or null when it is not known.
/// <para>
/// The flight state: <see cref="Controllable"/> is null for a vehicle without a control module, and
/// <see cref="ControlReason"/> says why a vehicle has no control. <see cref="ProbesRebooting"/> and
/// <see cref="ProbesFailed"/> count the probe cores that reboot after an upset or have failed.
/// <see cref="Incapacitated"/> counts the kittens on board that cannot act. <see cref="MissionArrived"/> is true
/// once the mission of the vehicle reached its destination body <see cref="MissionBody"/>. <see cref="Evas"/>,
/// <see cref="Boardings"/> and <see cref="Explosions"/> are what happened since the sample before, and each
/// explosion is a text that says what blew up. <see cref="GimbalLimit"/> is the deflection of the most deflected
/// gimbal as a share of its limit, see <see cref="GimbalMath.Share"/>. <see cref="DynamicPressure"/> is in pascals.
/// </para>
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
    bool VehicleChanged = false,
    double? Ground = null,
    bool? Controllable = null,
    string? ControlReason = null,
    int ProbesRebooting = 0,
    int ProbesFailed = 0,
    PowerWarning ProbePower = PowerWarning.None,
    SupplyWarning LifeSupport = SupplyWarning.None,
    int Incapacitated = 0,
    bool MissionArrived = false,
    string? MissionBody = null,
    int Evas = 0,
    int Boardings = 0,
    IReadOnlyList<string>? Explosions = null,
    double GimbalLimit = 0.0,
    double DynamicPressure = 0.0,
    EventStamp? Stamp = null);

/// <summary>The worst power state of the probe cores, as the status light of the game shows it.</summary>
public enum PowerWarning { None, Low, Out }

/// <summary>The life support forecast of the crewed zones, as the game rates it.</summary>
public enum SupplyWarning { None, Low, Critical, Out }

public readonly record struct EventStamp(double GameSeconds, int Frame, double Speed);

public sealed record DetectedEvent(string Name, double TakeTime, string Detail)
{
    public EventStamp? Stamp { get; init; }
}

/// <summary>
/// Turns the per-frame samples of the vehicle into the events of the shot file, each reported once,
/// and predicts the altitude, speed, ground, slow, q and maxq events so that a shot can start before them. A ground or
/// slow event comes the first time its value falls to the target after the take saw the vehicle without ground
/// contact and the value above <see cref="EventName.ArmLevel"/>, so a vehicle that is low, slow or landed at the
/// start does not give the event until it flew above that level.
/// </summary>
public sealed class EventDetector
{
    // The rate of altitude, speed and the height over the ground is smoothed over about this many seconds.
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

    // A power or life support warning that the forecast of the game gives again comes as a new event only after
    // the state was clear for this many seconds, because a forecast near its limit can flicker.
    private const double WarningRearmSeconds = 10.0;

    private readonly List<double> _altitudeTargets = new();
    private readonly List<double> _speedTargets = new();
    private readonly List<double> _groundTargets = new();
    private readonly List<double> _slowTargets = new();
    private readonly List<double> _pressureTargets = new();
    private readonly HashSet<string> _maxQWatched = new(StringComparer.Ordinal);
    private readonly MaxQTracker _maxQ = new();
    private EventStamp? _maxQStamp;
    private readonly HashSet<string> _above = new(StringComparer.Ordinal);
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
    private double? _lastGround;
    private double _groundRate;
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
    private bool? _controllable;
    private int _controlLosses;
    private int _controlReturns;
    private int _rebooting;
    private int _reboots;
    private int _failedProbes;
    private int _probeFailures;
    private readonly WarningState _power = new();
    private readonly WarningState _lifeSupport = new();
    private int _incapacitated;
    private int _incapacitations;
    private bool _arrived;
    private int _arrivals;
    private int _evas;
    private int _boardings;
    private int _explosions;
    private bool _gimbalArmed = true;
    private int _gimbalLimits;
    private int _maxQs;
    private double _lastPressure;
    private double _pressureRate;
    private double _pressureRateChange;

    private sealed class WarningState
    {
        public int Level;
        public bool Armed;
        public double ClearSince;
        public int Count;
    }

    public EventDetector(IEnumerable<EventName> watched)
    {
        foreach (EventName name in watched)
        {
            if (name.Kind == EventKind.Altitude && !_altitudeTargets.Contains(name.Value))
                _altitudeTargets.Add(name.Value);
            else if (name.Kind == EventKind.Speed && !_speedTargets.Contains(name.Value))
                _speedTargets.Add(name.Value);
            else if (name.Kind == EventKind.Ground && !_groundTargets.Contains(name.Value))
                _groundTargets.Add(name.Value);
            else if (name.Kind == EventKind.Slow && !_slowTargets.Contains(name.Value))
                _slowTargets.Add(name.Value);
            else if (name.Kind == EventKind.Q && !_pressureTargets.Contains(name.Value))
                _pressureTargets.Add(name.Value);
            else if (name.Kind == EventKind.MaxQ)
                _maxQWatched.Add(name.Canonical);
        }
    }

    /// <summary>Canonical event names and the take time at which each was first seen.</summary>
    public IReadOnlyDictionary<string, double> Seen => _seen;

    /// <summary>Canonical names of the predictable events and the seconds until each, from the last update.</summary>
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
            _lastGround = sample.Ground;
            if (sample.EngineOn)
            {
                _engineOnSince = takeTime;
                CountIgnition(found, takeTime, UiText.MessageText("text.51de4d008e70", "already on at the start"));
            }

            _groundAltitude = sample.Altitude;
            if (!sample.Contact)
                Add(found, "liftoff", takeTime, UiText.MessageText("text.f06b2b90809e", "already flying at the start"));
            _airSince = takeTime;
            _body = sample.Body;
            _radialSign = RadialSign(sample.RadialSpeed);
            _docked = sample.DockedPorts;
            _lastPressure = sample.DynamicPressure;
            StartFlightState(takeTime, sample);
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
                CountIgnition(found, takeTime, UiText.MessageText("text.e53e2a96516f", "another engine lit while the engines ran"));
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
                        Add(found, UiText.Concat("sequence:", n.ToString(CultureInfo.InvariantCulture)), takeTime,
                            n == fired && sample.ActiveSequence < fired ? UiText.MessageText("text.a150a438cc6b", "fired; its parts left the vehicle at once") : "");
                    _lastSequence = Math.Max(_lastSequence, fired);
                }
            }

            if (sample.ActiveSequence > _lastSequence)
            {
                int first = _lastSequence + 1;
                int last = Math.Min(sample.ActiveSequence, first + 999);
                for (int n = first; n <= last; n++)
                    Add(found, UiText.Concat("sequence:", n.ToString(CultureInfo.InvariantCulture)), takeTime, "");
            }

            UpdateSoi(found, takeTime, sample);
            UpdateApsis(found, takeTime, sample);
            UpdateDocking(found, takeTime, sample);
            UpdateFlightState(found, takeTime, sample);

            if (dt > 1e-6)
            {
                double blend = 1.0 - Math.Exp(-dt / RateTimeConstant);
                _altitudeRate += ((sample.Altitude - _lastAltitude) / dt - _altitudeRate) * blend;
                _speedRate += ((sample.Speed - _lastSpeed) / dt - _speedRate) * blend;
                if (sample.Ground is double ground && _lastGround is double lastGround)
                    _groundRate += ((ground - lastGround) / dt - _groundRate) * blend;
                double pressureRate = _pressureRate;
                _pressureRate += ((sample.DynamicPressure - _lastPressure) / dt - _pressureRate) * blend;
                _pressureRateChange += ((_pressureRate - pressureRate) / dt - _pressureRateChange) * blend;
            }
        }

        AddCounted(found, "eva", ref _evas, sample.Evas, takeTime);
        AddCounted(found, "board", ref _boardings, sample.Boardings, takeTime);
        foreach (string explosion in sample.Explosions ?? [])
        {
            _explosions++;
            Add(found, EventName.Counted("explosion", _explosions), takeTime, explosion);
        }

        UpdateMaxQ(found, takeTime, sample);

        for (int i = 0; i < sample.Marks; i++)
        {
            _marks++;
            Add(found, EventName.Counted("mark", _marks), takeTime, "");
        }

        foreach (double target in _altitudeTargets)
            Reach(found, new EventName(EventKind.Altitude, target).Canonical, target, sample.Altitude, _altitudeRate, takeTime);
        foreach (double target in _speedTargets)
            Reach(found, new EventName(EventKind.Speed, target).Canonical, target, sample.Speed, _speedRate, takeTime);
        foreach (double target in _groundTargets)
            Fall(found, new EventName(EventKind.Ground, target), sample.Ground, sample.Contact, _groundRate, takeTime);
        foreach (double target in _slowTargets)
            Fall(found, new EventName(EventKind.Slow, target), sample.Speed, sample.Contact, _speedRate, takeTime);
        foreach (double target in _pressureTargets)
            Reach(found, new EventName(EventKind.Q, target).Canonical, target, sample.DynamicPressure, _pressureRate, takeTime);

        _wasEngineOn = sample.EngineOn;
        _wasContact = sample.Contact;
        _lastSequence = Math.Max(_lastSequence, sample.ActiveSequence);
        _lastAltitude = sample.Altitude;
        _lastSpeed = sample.Speed;
        _lastGround = sample.Ground;
        _lastPressure = sample.DynamicPressure;
        _lastTime = takeTime;
        return found;
    }

    /// <summary>
    /// The explosions of a vehicle that the game destroyed with them, so no sample of it can come any more. The
    /// take or the recording ends then, and the events go into its log.
    /// </summary>
    public IReadOnlyList<DetectedEvent> Destroyed(double takeTime, IReadOnlyList<string> explosions)
    {
        var found = new List<DetectedEvent>();
        foreach (string explosion in explosions)
        {
            _explosions++;
            Add(found, EventName.Counted("explosion", _explosions), takeTime, explosion);
        }

        return found;
    }

    // The state at the start gives no event: a take that starts without control or with low power does not see
    // control-lost or power-low, because those events are for the moment of the change.
    private void StartFlightState(double takeTime, in VehicleSample sample)
    {
        _controllable = sample.Controllable;
        _rebooting = sample.ProbesRebooting;
        _failedProbes = sample.ProbesFailed;
        StartWarning(_power, (int)sample.ProbePower, takeTime);
        StartWarning(_lifeSupport, (int)sample.LifeSupport, takeTime);
        _incapacitated = sample.Incapacitated;
        _arrived = sample.MissionArrived;
        _gimbalArmed = sample.GimbalLimit < GimbalMath.RearmShare;
    }

    // A new vehicle object (a docking, an undocking or a breakup) brings its own probe cores, crew and mission, so
    // their counts start again from its state. The control is compared, because control that is lost with the
    // vehicle before is lost for the take too.
    private void UpdateFlightState(List<DetectedEvent> found, double takeTime, in VehicleSample sample)
    {
        if (sample.Controllable is bool controllable && _controllable is bool was && controllable != was)
        {
            if (controllable)
            {
                _controlReturns++;
                Add(found, EventName.Counted("control-back", _controlReturns), takeTime, "");
            }
            else
            {
                _controlLosses++;
                Add(found, EventName.Counted("control-lost", _controlLosses), takeTime, sample.ControlReason ?? "");
            }
        }

        _controllable = sample.Controllable;

        if (sample.ProbesRebooting > 0 && _rebooting == 0 && !sample.VehicleChanged)
        {
            _reboots++;
            Add(found, EventName.Counted("reboot", _reboots), takeTime, "");
        }

        _rebooting = sample.ProbesRebooting;

        if (sample.ProbesFailed > _failedProbes && !sample.VehicleChanged)
        {
            _probeFailures++;
            Add(found, EventName.Counted("probe-failed", _probeFailures), takeTime,
                UiText.Format("text.b1976078e491", $"failed probe cores {_failedProbes} to {sample.ProbesFailed}"));
        }

        _failedProbes = sample.ProbesFailed;

        if (Warn(_power, (int)sample.ProbePower, takeTime))
            Add(found, EventName.Counted("power-low", _power.Count), takeTime, sample.ProbePower == PowerWarning.Out ? UiText.MessageText("text.f06a21e7fb12", "out of power") : UiText.MessageText("text.6c1ff09db3a7", "low"));
        if (Warn(_lifeSupport, (int)sample.LifeSupport, takeTime))
            Add(found, EventName.Counted("life-support-low", _lifeSupport.Count), takeTime, EventText.Words(sample.LifeSupport.ToString()));

        if (sample.Incapacitated > _incapacitated && !sample.VehicleChanged)
        {
            _incapacitations++;
            Add(found, EventName.Counted("incapacitated", _incapacitations), takeTime,
                UiText.Format("text.ce91fbe71a64", $"kittens incapacitated {_incapacitated} to {sample.Incapacitated}"));
        }

        _incapacitated = sample.Incapacitated;

        if (sample.MissionArrived && !_arrived && !sample.VehicleChanged)
        {
            _arrivals++;
            Add(found, EventName.Counted("arrival", _arrivals), takeTime, sample.MissionBody ?? "");
        }

        _arrived = sample.MissionArrived;

        if (sample.GimbalLimit < GimbalMath.RearmShare)
            _gimbalArmed = true;
        else if (_gimbalArmed && sample.EngineOn && sample.GimbalLimit >= GimbalMath.LimitShare)
        {
            _gimbalArmed = false;
            _gimbalLimits++;
            Add(found, EventName.Counted("gimbal-limit", _gimbalLimits), takeTime,
                UiText.Format("text.335e0f8d3664", $"deflection {sample.GimbalLimit * 100.0:F0} percent of the limit"));
        }
    }

    private static void StartWarning(WarningState state, int level, double takeTime)
    {
        state.Level = level;
        state.Armed = false;
        state.ClearSince = level == 0 ? takeTime : double.PositiveInfinity;
        if (level == 0)
            state.Armed = true;
    }

    // Returns true when the level rises from clear to a warning while the warning is armed. After a warning it
    // arms again when the level was clear for WarningRearmSeconds.
    private static bool Warn(WarningState state, int level, double takeTime)
    {
        if (level == 0)
        {
            if (state.Level != 0)
                state.ClearSince = takeTime;
            if (takeTime - state.ClearSince >= WarningRearmSeconds)
                state.Armed = true;
            state.Level = 0;
            return false;
        }

        bool fire = state.Level == 0 && (state.Armed || takeTime - state.ClearSince >= WarningRearmSeconds);
        state.Level = level;
        state.ClearSince = double.PositiveInfinity;
        if (!fire)
            return false;
        state.Armed = false;
        state.Count++;
        return true;
    }

    private void AddCounted(List<DetectedEvent> found, string head, ref int count, int added, double takeTime)
    {
        for (int i = 0; i < added; i++)
        {
            count++;
            Add(found, EventName.Counted(head, count), takeTime, "");
        }
    }

    // The event has the time of the peak, which the detector knows only when the pressure has fallen below it,
    // so the take sees it a few seconds late. A watched max Q has a prediction while the pressure rises toward a
    // peak, from the rate of the pressure and the change of that rate.
    private void UpdateMaxQ(List<DetectedEvent> found, double takeTime, in VehicleSample sample)
    {
        string next = EventName.Counted("maxq", _maxQs + 1);
        if (_maxQ.Update(takeTime, sample.DynamicPressure, sample.Contact) is var (time, pascals))
        {
            _maxQs++;
            _predictions.Remove(next);
            Add(found, next, time, UiText.Format("text.12590f0abb89", $"peak {pascals / 1000.0:F2} kPa, found {takeTime - time:F1} s later"));
            found[^1] = found[^1] with { Stamp = _maxQStamp };
            return;
        }
        if (_maxQ.PeakTime == takeTime)
            _maxQStamp = sample.Stamp;

        if (!_maxQWatched.Contains(next))
            return;
        double? seconds = _maxQ.Armed && !sample.Contact ? MaxQTracker.SecondsToPeak(_pressureRate, _pressureRateChange) : null;
        if (seconds.HasValue)
            _predictions[next] = seconds.Value;
        else
            _predictions.Remove(next);
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
            Add(found, EventName.Counted("soi", _soiChanges), takeTime, UiText.Format("text.7b833f8425b7", $"from {_body} to {sample.Body}"));
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
        string detail = UiText.Format("text.2976533afa4f", $"docked ports {before} to {sample.DockedPorts}");
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

    private void Reach(List<DetectedEvent> found, string name, double target, double value, double rate, double takeTime)
    {
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

    // A sample without the value neither arms nor fires the event.
    private void Fall(List<DetectedEvent> found, EventName eventName, double? value, bool contact, double rate, double takeTime)
    {
        string name = eventName.Canonical;
        double target = eventName.Value;
        if (_seen.ContainsKey(name))
            return;
        if (value is not double current)
        {
            _predictions.Remove(name);
            return;
        }

        if (!contact && current > eventName.ArmLevel)
            _above.Add(name);
        // An event that is not armed cannot come, so it has no prediction that could start a shot early.
        if (current > target)
        {
            double? seconds = _above.Contains(name) ? EventPrediction.SecondsUntilFall(current, target, rate) : null;
            if (seconds.HasValue)
                _predictions[name] = seconds.Value;
            else
                _predictions.Remove(name);
            return;
        }

        _predictions.Remove(name);
        if (_above.Contains(name))
            Add(found, name, takeTime, "");
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
