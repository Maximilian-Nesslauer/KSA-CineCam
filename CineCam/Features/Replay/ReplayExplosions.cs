using System.Reflection;
using Brutal.Logging;
using Brutal.Numerics;
using CineCam.Core;
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
    private static readonly FieldInfo? SpawnerField = AccessTools.Field(typeof(ExplosionVolumeManager), "_spawner");
    private static readonly MethodInfo? SpawnVolume = AccessTools.DeclaredMethod(typeof(ExplosionVolumeSpawner), "SpawnVolume",
        [typeof(ExplosionVolumeTemplate), typeof(Celestial), typeof(double3), typeof(double3), typeof(float), typeof(float),
            typeof(ExplosionTimingProfile).MakeByRefType()]);
    private static bool _missingLogged;

    private readonly ExplosionVolumeManager _manager;
    private readonly ExplosionVolumeStore _store;
    private readonly ReplayExplosionSchedule _schedule;
    private readonly CelestialSystem _system;
    private readonly double _startTime;
    private readonly Dictionary<string, ExplosionVolumeTemplate?> _templates = new(StringComparer.Ordinal);
    private readonly List<ExplosionVolume> _live = [];
    private readonly Dictionary<int, ExplosionVolume> _volumeIds = new();
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private bool _entered;
    private double _time = double.NaN;

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

    /// <summary>The recorded game time used by volume maintenance and rendering.</summary>
    public double Clock { get; private set; }

    /// <summary>The step of the recording time since the frame before, for the motion of the volumes.</summary>
    public float Step { get; private set; }

    public double Now => Clock;

    /// <summary>
    /// The explosions of a replay, or null when the patches or the store of the game are missing. Without them the
    /// replay shows no explosion volume and no plume trail.
    /// </summary>
    public static ReplayExplosions? TryCreate(ReplayFlight flight, CelestialSystem system)
    {
        if (!PatchesReady)
            return null;
        if (SpawnerField == null || SpawnVolume == null)
        {
            LogHelper.WarnOnce("replay-volume-spawn", "[CineCam] Replay explosions are disabled because ExplosionVolumeSpawner.SpawnVolume is unavailable.");
            return null;
        }
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
        {
            _manager.Clear();
            _volumeIds.Clear();
        }
        for (int i = from; i < to; i++)
            Spawn(_schedule[i]);
    }

    // Merges address recorded identities rather than running the live nearest-volume search again.
    // Births use the native SpawnVolume method with the recorded time and seed.
    private void Spawn(ReplayExplosion item)
    {
        if (item.MergeTargetId > 0)
        {
            if (_volumeIds.TryGetValue(item.MergeTargetId, out ExplosionVolume? target) && Contains(target) && !target.IsExpired(Clock))
            {
                target.Intensity = KSA.Rendering.ExplosionIntensity.ClampIntensity(target.Intensity + item.Intensity);
                target.RadiusScale = KSA.Rendering.ExplosionIntensity.CubeRootScale(target.Intensity);
            }
            return;
        }
        // An older merge without a target cannot safely grow an arbitrary remaining fireball.
        if (item.Seed <= 0) return;
        if (item.ReplacesId > 0 && _volumeIds.TryGetValue(item.ReplacesId, out ExplosionVolume? replaced))
            Remove(replaced);
        if (Template(item.Template) is not { } template || _system.Get<Celestial>(item.Body) is not { } body)
            return;
        float intensity = KSA.Rendering.ExplosionIntensity.ClampIntensity(item.Intensity);
        if (Clock - (_startTime + item.Time) >= template.GetScaledLifeSeconds(intensity, item.AmbientPressure))
            return;
        HashSet<ExplosionVolume> before = Snapshot(_store);
        // Recorded births must bypass the live merge search. The native birth method keeps the
        // game's lifetime, pressure, shape and capacity rules, with the recorded birth time.
        SpawnVolume!.Invoke(SpawnerField!.GetValue(_manager),
            [template, body, ReplayCamera.Vector(item.Position), ReplayCamera.Vector(item.Velocity), intensity,
                item.AmbientPressure, new ExplosionTimingProfile(_startTime + item.Time, 0)]);
        ExplosionVolume? volume = Added(_store, before);
        if (volume == null)
            return;
        if (item.VolumeId > 0) _volumeIds[item.VolumeId] = volume;
        volume.NoiseSeed = item.Seed;
        volume.Shape = ExplosionVolumeShape.Build(volume.Template, volume.Parent, volume.PositionCcf,
            volume.Template.GetMaximumRadius(volume.Expansion) * volume.RadiusScale, item.Seed);
    }

    private bool Contains(ExplosionVolume volume)
    {
        for (int i = 0; i < _store.Count; i++) if (ReferenceEquals(_store[i], volume)) return true;
        return false;
    }

    private void Remove(ExplosionVolume volume)
    {
        for (int i = _store.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_store[i], volume)) _store.RemoveAtSwapBack(i);
    }

    public static HashSet<ExplosionVolume> Snapshot(ExplosionVolumeStore store)
    {
        var result = new HashSet<ExplosionVolume>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < store.Count; i++) result.Add(store[i]);
        return result;
    }

    public static ExplosionVolume? Added(ExplosionVolumeStore store, HashSet<ExplosionVolume> before)
    {
        for (int i = 0; i < store.Count; i++)
            if (!before.Contains(store[i])) return store[i];
        return null;
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
        _volumeIds.Clear();
    }
}
