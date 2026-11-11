using System.Diagnostics;
using CineCam.Director;
using CineCam.Features.Takes;
using KSA;

namespace CineCam.Features.Ui;

// Watches the controlled vehicle with the same detector as a take, so the events can be checked on
// the pad. While a take runs, the take reports the events and this watcher rests. Its times are
// wall clock seconds since the watch started, not game time.
internal sealed class LiveEvents
{
    private readonly Stopwatch _clock = new();
    private VehicleReader? _reader;
    private EventDetector? _detector;
    private string _watchKey = "";
    private int _version = -1;

    public bool HasVehicle => _reader != null;

    public VehicleSample? Sample { get; private set; }

    public IReadOnlyDictionary<string, double> Seen =>
        _detector?.Seen ?? (IReadOnlyDictionary<string, double>)EmptySeen;

    public IReadOnlyDictionary<string, double> Predictions =>
        _detector?.Predictions ?? (IReadOnlyDictionary<string, double>)EmptySeen;

    public double Seconds => _clock.Elapsed.TotalSeconds;

    private static readonly Dictionary<string, double> EmptySeen = new();

    public void Reset()
    {
        _detector = null;
        _watchKey = "";
        _version = -1;
        Sample = null;
        _clock.Restart();
    }

    public void Update(Take? take, ShotFile? file, int version)
    {
        Vehicle? vehicle = Program.ControlledVehicle;
        if (vehicle == null || vehicle.IsDisposed || Program.Editor != null)
        {
            _reader = null;
            Sample = null;
            _detector = null;
            return;
        }

        if (_reader == null || !ReferenceEquals(_reader.Vehicle, vehicle))
        {
            _reader = new VehicleReader(vehicle);
            Reset();
        }

        if (_detector == null || version != _version)
        {
            _version = version;
            List<EventName> watched = take != null && file != null
                ? EventDetector.Watched(take, file.Events)
                : new List<EventName>();
            string key = string.Join("|", watched.Where(w => w.IsPredictable).Select(w => w.Canonical).Distinct().OrderBy(n => n, StringComparer.Ordinal));
            if (_detector == null || key != _watchKey)
            {
                _watchKey = key;
                _detector = new EventDetector(watched);
                _clock.Restart();
            }
        }

        VehicleSample sample = _reader.Sample();
        Sample = sample;
        _detector.Update(_clock.Elapsed.TotalSeconds, sample);
    }
}
