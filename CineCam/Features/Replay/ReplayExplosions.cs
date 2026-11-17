using System.Reflection;
using Brutal.Logging;
using Brutal.Numerics;
using CineCam.Director;
using CineCam.Director.Replay;
using HarmonyLib;
using KSA;

namespace CineCam.Features.Replay;

/// <summary>
/// The explosion volumes of a replay. While a replay is open, the store of ExplosionVolumeManager holds only the
/// volumes of the replay: the live volumes wait outside it and come back when the replay closes, because the
/// game time does not move in a replay. Every time read of the volumes goes through
/// ExplosionVolumeSettings.CreateTimingProfile, which the replay patches answer with <see cref="Clock"/>, so the
/// volumes burn on the recording time.
/// </summary>
internal sealed class ReplayExplosions : IDisposable
{
    private static readonly FieldInfo? StoreField = AccessTools.Field(typeof(ExplosionVolumeManager), "_store");
    private static readonly FieldInfo? TemplatesField = AccessTools.Field(typeof(ModLibrary), "AllExplosionVolumeTemplates");
    private static bool _missingLogged;

    private readonly ExplosionVolumeManager _manager;
    private readonly ExplosionVolumeStore _store;
    private readonly ReplayExplosionSchedule _schedule;
    private readonly CelestialSystem _system;
    private readonly double _startTime;
    private readonly Dictionary<string, ExplosionVolumeTemplate?> _templates = new(StringComparer.Ordinal);
    private readonly List<ExplosionVolume> _live = [];
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private bool _entered;
    private double _time = double.NaN;
    private double? _spawnClock;

    private ReplayExplosions(ExplosionVolumeManager manager, ExplosionVolumeStore store, ReplayFlight flight, CelestialSystem system)
    {
        _manager = manager;
        _store = store;
        _schedule = new ReplayExplosionSchedule(flight.Explosions ?? []);
        _system = system;
        _startTime = flight.StartTime;
        Clock = flight.StartTime;
    }

    /// <summary>True when the replay patches answer the time reads of the volumes and hide the live plume trails.</summary>
    public static bool PatchesReady { get; set; }

    /// <summary>The game time that the volumes read now: the recorded moment, or the start of a volume while the replay makes it.</summary>
    public double Clock { get; private set; }

    /// <summary>The step of the recording time since the frame before, for the motion of the volumes.</summary>
    public float Step { get; private set; }

    public double Now => _spawnClock ?? Clock;

    /// <summary>
    /// The explosions of a replay, or null when the patches or the store of the game are missing. Without them the
    /// replay shows no explosion volume and no plume trail.
    /// </summary>
    public static ReplayExplosions? TryCreate(ReplayFlight flight, CelestialSystem system)
    {
        if (!PatchesReady)
            return null;
        try
        {
            ExplosionVolumeManager manager = Program.Instance.ExplosionVolumes;
            if (StoreField?.GetValue(manager) is ExplosionVolumeStore store)
                return new ReplayExplosions(manager, store, flight, system);
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Warning($"[CineCam] The replay shows no explosions: {ex.GetBaseException().Message}");
            return null;
        }

        DefaultCategory.Log.Warning("[CineCam] The replay shows no explosions, because ExplosionVolumeManager._store is missing.");
        return null;
    }

    /// <summary>The store of the explosion volumes of the game, for the recorder, or null.</summary>
    public static ExplosionVolumeStore? StoreOf(ExplosionVolumeManager manager) => StoreField?.GetValue(manager) as ExplosionVolumeStore;

    public void Enter()
    {
        _live.Clear();
        for (int i = 0; i < _store.Count; i++)
            _live.Add(_store[i]);
        _manager.Clear();
        _entered = true;
        _schedule.Jump();
    }

    /// <summary>Makes the next <see cref="Update"/> build the volumes again, after a seek.</summary>
    public void Jump() => _schedule.Jump();

    /// <summary>Makes the volumes that start up to the recording time, see <see cref="ReplayExplosionSchedule"/>.</summary>
    public void Update(double time)
    {
        if (!_entered)
            return;
        (int from, int to) = _schedule.Advance(time, out bool reset);
        Step = reset || !double.IsFinite(_time) ? 0f : (float)Math.Max(0, time - _time);
        _time = time;
        Clock = _startTime + time;
        if (reset)
            _manager.Clear();
        for (int i = from; i < to; i++)
            Spawn(_schedule[i]);
    }

    // ExplosionVolumeSpawner.Spawn reads the clock for the start of the volume and for the merge into a volume
    // that burns, so the clock is the recorded start while it runs. A new volume gets the recorded noise seed
    // and its shape again, because the spawner picks a random seed.
    private void Spawn(ReplayExplosion item)
    {
        if (Template(item.Template) is not { } template || _system.Get<Celestial>(item.Body) is not { } body)
            return;
        int count = _store.Count;
        _spawnClock = _startTime + item.Time;
        try
        {
            _manager.Spawn(template, body, ReplayCamera.Vector(item.Position), ReplayCamera.Vector(item.Velocity), item.Intensity, item.AmbientPressure);
        }
        finally
        {
            _spawnClock = null;
        }

        if (item.Seed <= 0 || _store.Count <= count)
            return;
        ExplosionVolume volume = _store[_store.Count - 1];
        volume.NoiseSeed = item.Seed;
        volume.Shape = ExplosionVolumeShape.Build(volume.Template, volume.Parent, volume.PositionCcf,
            volume.Template.GetMaximumRadius(volume.Expansion) * volume.RadiusScale, item.Seed);
    }

    private ExplosionVolumeTemplate? Template(string id)
    {
        if (_templates.TryGetValue(id, out ExplosionVolumeTemplate? template))
            return template;
        template = (TemplatesField?.GetValue(null) as SerializedCollection<ExplosionVolumeTemplate>)?.GetList()
            .Find(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        _templates[id] = template;
        if (template == null && _missing.Add(id) && !_missingLogged)
        {
            _missingLogged = TemplatesField == null;
            DefaultCategory.Log.Warning(TemplatesField == null
                ? "[CineCam] The replay shows no explosions, because ModLibrary.AllExplosionVolumeTemplates is missing."
                : $"[CineCam] The replay leaves out the explosions of the missing template {id}.");
        }
        return template;
    }

    public void Dispose()
    {
        if (!_entered)
            return;
        _entered = false;
        _manager.Clear();
        foreach (ExplosionVolume volume in _live)
            _store.Add(volume);
        _live.Clear();
    }
}
