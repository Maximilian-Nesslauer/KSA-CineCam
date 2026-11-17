using System.Text.Json.Serialization;

namespace CineCam.Director.Replay;

/// <summary>
/// One explosion volume as the game made it: the template, the body and the place and speed in the body-fixed
/// frame of that body, the intensity and the air pressure that size it, and the noise seed of its shape. A seed of
/// 0 means that the game merged it into a volume that burned already. Volume and target identities
/// distinguish that volume from other nearby fireballs and preserve replacements in a full store.
/// </summary>
public sealed record ReplayExplosion(double Time, string Template, string Body, V3 Position, V3 Velocity, float Intensity,
    float AmbientPressure, int Seed)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int VolumeId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MergeTargetId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ReplacesId { get; init; }
}

/// <summary>
/// A destruction that asked the game for an explosion. <see cref="Cause"/> is the name of the game cause, the actor is
/// the recorded vehicle that broke, and <see cref="Part"/> the part id when only one part failed.
/// </summary>
public sealed record ReplayDestruction(double Time, string Cause, float Intensity, float PropellantMass, float AmbientPressure,
    int? ActorId = null, string? Part = null);

/// <summary>
/// The explosions and destructions of a recording that wait for the next sample. The game makes them later in the
/// frame than the recorder samples. They enter the flight when their time lies within its duration,
/// which can include a terminal effects tail after the last vehicle pose.
/// </summary>
public sealed class ReplayEffectLog
{
    /// <summary>The label of a destruction marker. The marker list of the replay shows the labels that start with it.</summary>
    public static readonly string MarkerLabel = UiText.MessageText("text.ca6482a42bab", "Explosion");

    private readonly List<ReplayExplosion> _explosions = [];
    private readonly List<(ReplayDestruction Item, string? Vehicle)> _destructions = [];

    public int Pending => _explosions.Count + _destructions.Count;

    public void Add(ReplayExplosion item) => _explosions.Add(item);

    /// <param name="vehicle">The name of the vehicle for the marker, or null.</param>
    public void Add(ReplayDestruction item, string? vehicle) => _destructions.Add((item, vehicle));

    /// <summary>
    /// Moves the items up to <paramref name="time"/> into the flight, with one marker for the destructions of
    /// one moment, and returns the bytes they add to the file. The flight keeps at most
    /// <see cref="ReplayFile.EventLimit"/> items of each kind.
    /// </summary>
    public long Flush(ReplayFlight flight, double time)
    {
        long bytes = 0;
        if (flight.Explosions is { } explosions)
        {
            foreach (ReplayExplosion item in _explosions)
            {
                if (item.Time > time || explosions.Count >= ReplayFile.EventLimit)
                    continue;
                explosions.Add(item);
                bytes += ReplayFile.MeasureExplosion(item);
            }
        }
        _explosions.RemoveAll(item => item.Time <= time);

        if (flight.Destructions is { } destructions)
        {
            foreach ((ReplayDestruction item, string? vehicle) in _destructions)
            {
                if (item.Time > time || destructions.Count >= ReplayFile.EventLimit)
                    continue;
                destructions.Add(item);
                bytes += ReplayFile.MeasureDestruction(item);
                string label = vehicle is { Length: > 0 } name ? $"{MarkerLabel}: {name}" : MarkerLabel;
                if (flight.Markers.Count < 100000 && !flight.Markers.Exists(marker => marker.Time == item.Time && marker.Label == label))
                {
                    flight.Markers.Add(new ReplayMarker(item.Time, label));
                    bytes += ReplayFile.MeasureText(label) + 32;
                }
            }
        }
        _destructions.RemoveAll(entry => entry.Item.Time <= time);
        return bytes;
    }
}

/// <summary>
/// The explosion volumes that the replay makes for a move of the recording time. A move forward makes the volumes
/// that start after the time before and up to the new time. Any other move, and the first one, removes the volumes
/// of the replay and considers every volume before the new time. The renderer tests the actual template
/// lifetime, so a long-lived volume still appears after a seek.
/// </summary>
public sealed class ReplayExplosionSchedule
{
    private readonly IReadOnlyList<ReplayExplosion> _items;
    private double _time = double.NaN;

    /// <param name="items">In time order, as <see cref="ReplayFile"/> keeps them.</param>
    public ReplayExplosionSchedule(IReadOnlyList<ReplayExplosion> items) => _items = items;

    public int Count => _items.Count;

    /// <summary>Makes the next <see cref="Advance"/> a rebuild, for example after a seek.</summary>
    public void Jump() => _time = double.NaN;

    /// <summary>
    /// The range of items to make for a move to <paramref name="time"/>. <paramref name="reset"/> is true when the
    /// volumes of the replay must go first.
    /// </summary>
    public (int From, int To) Advance(double time, out bool reset)
    {
        reset = !(time >= _time);
        int from = reset ? 0 : UpperBound(_time);
        int to = UpperBound(time);
        _time = time;
        return (from, Math.Max(from, to));
    }

    public ReplayExplosion this[int index] => _items[index];

    // The first item whose time is greater than the time.
    private int UpperBound(double time)
    {
        int lo = 0, hi = _items.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (_items[mid].Time <= time)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    // The first item whose time is not less than the time.
    private int LowerBound(double time)
    {
        int lo = 0, hi = _items.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (_items[mid].Time < time)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }
}
