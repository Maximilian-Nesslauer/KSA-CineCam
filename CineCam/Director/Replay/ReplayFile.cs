using System.IO.Compression;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace CineCam.Director.Replay;

/// <summary>
/// The part channels, the neighbor flag and end time of an actor and the follow changes are optional
/// fields of version 2. A file of version 1 has no events, states or centers of mass, so its markers
/// become events and the rest is derived from the poses.
/// </summary>
public static class ReplayFile
{
    public const int CurrentVersion = 2;
    public const int MaxBytes = 256 * 1024 * 1024;
    public const int EventLimit = 10000;

    /// <summary>The loader drops the channels of a segment with more.</summary>
    public const int ChannelLimit = 4096;

    /// <summary>The most keys of all part channels of a file. The loader drops the channels of the segments over it.</summary>
    public const long ChannelValueLimit = 20000000;
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 32 };
    private sealed record Envelope(int Version, ReplayFlight Flight);

    public static void Save(string path, ReplayFlight flight)
    {
        ReplayFlight clean = Validate(flight);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new Envelope(CurrentVersion, clean), Options);
        if (json.Length > MaxBytes)
            throw new InvalidDataException("Replay is too large.");
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true))
                    gzip.Write(json);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // The sizes include the comma that separates an item from the next one.
    public static long MeasureSample(ReplaySample sample) => JsonSerializer.SerializeToUtf8Bytes(sample, Options).Length + 1;
    public static long MeasureText(string text) => JsonSerializer.SerializeToUtf8Bytes(text, Options).Length + 1;
    public static long MeasureNumber(double value) => JsonSerializer.SerializeToUtf8Bytes(value, Options).Length + 1;
    public static long MeasureNumber(float value) => JsonSerializer.SerializeToUtf8Bytes(value, Options).Length + 1;

    // The extra bytes are the property name of the list.
    public static long MeasureChannels(List<ReplayChannel> channels) =>
        JsonSerializer.SerializeToUtf8Bytes(channels, Options).Length + 16;

    public static ReplayFlight Load(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxBytes) throw new InvalidDataException("Replay is too large.");
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        byte[] block = new byte[81920];
        int count;
        while ((count = gzip.Read(block)) != 0)
        {
            if (buffer.Length + count > MaxBytes) throw new InvalidDataException("Replay is too large.");
            buffer.Write(block, 0, count);
        }
        Envelope envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), Options) ?? throw new InvalidDataException("Replay is empty."); }
        catch (JsonException ex) { throw new InvalidDataException("Replay JSON is invalid.", ex); }
        if (envelope.Version is not (1 or CurrentVersion)) throw new InvalidDataException($"Unsupported replay version {envelope.Version}.");
        ReplayFlight flight = Validate(envelope.Flight);
        if (envelope.Version == 1)
            flight = flight with { Events = ReplayEvents.FromMarkers(flight.Markers), FormatVersion = 1 };
        return flight;
    }

    private static ReplayFlight Validate(ReplayFlight flight)
    {
        static void Require([DoesNotReturnIf(false)] bool condition, string message)
        {
            if (!condition) throw new InvalidDataException("Invalid replay: " + message);
        }
        static bool SupportedVector(V3 v) => v.Length <= 1e16;
        static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 16 * 1024 * 1024;
        Require(flight != null, "missing flight");
        Require(Text(flight!.GameVersion) && Text(flight.SystemId) && Text(flight.BodyId) && double.IsFinite(flight.StartTime), "flight metadata");
        Require(flight.Actors != null && flight.Markers != null && flight.Views != null && flight.Events != null, "missing lists");
        Require(flight.Actors!.Count > 0 && flight.Actors.Count <= ReplaySampling.ActorLimit && flight.Markers!.Count <= 100000 && flight.Views!.Count <= 100000
            && flight.Events!.Count <= EventLimit, "invalid number of items");
        var ids = new HashSet<int>();
        var actors = new List<ReplayActor>();
        int sampleCount = 0;
        long channelValues = 0;
        foreach (ReplayActor actor in flight.Actors)
        {
            Require(actor != null && ids.Add(actor.Id) && Text(actor.Name) && actor.Segments != null && actor.Segments.Count > 0 && actor.Segments.Count <= 10000, "actor");
            var segments = new List<ReplaySegment>();
            double previousStart = -1;
            for (int i = 0; i < actor!.Segments.Count; i++)
            {
                ReplaySegment segment = actor.Segments[i];
                Require(segment != null && double.IsFinite(segment.Start) && segment.Start >= 0 && segment.Start > previousStart && Text(segment.Definition) && segment.Samples != null && segment.Samples.Count > 0, "segment");
                Require(segment!.CenterOfMass is not V3 center || (double.IsFinite(center.Length) && center.Length <= 1e5), "segment center of mass");
                previousStart = segment.Start;
                sampleCount += segment.Samples.Count;
                Require(sampleCount <= 2000000, "too many samples");
                var samples = new List<ReplaySample>();
                double previousTime = -1;
                foreach (ReplaySample sample in segment.Samples)
                {
                    Require(sample != null && double.IsFinite(sample.Time) && sample.Time >= segment.Start && sample.Time > previousTime && SupportedVector(sample.Position) && sample.Nozzles != null && sample.Nozzles.Length <= 4096 && float.IsFinite(sample.AmbientPressure) && float.IsFinite(sample.AirDensity) && SupportedVector(sample.AirVelocity), "sample");
                    Require(sample!.State is not ReplayState state || (float.IsFinite(state.Altitude) && float.IsFinite(state.Speed) && float.IsFinite(state.Radial)
                        && state.Sequence >= 0 && state.Sequence <= 1000000 && state.Docked >= 0 && state.Docked <= 100000), "sample state");
                    if (i + 1 < actor.Segments.Count)
                        Require(actor.Segments[i + 1] != null && sample.Time < actor.Segments[i + 1].Start, "sample crosses segment boundary");
                    previousTime = sample.Time;
                    foreach (ReplayNozzle nozzle in sample.Nozzles)
                        Require(float.IsFinite(nozzle.DutyCycle) && float.IsFinite(nozzle.ExhaustVelocity) && float.IsFinite(nozzle.ExhaustPressure) && float.IsFinite(nozzle.Temperature) && float.IsFinite(nozzle.Gamma) && float.IsFinite(nozzle.SpecificGasConstant) && float.IsFinite(nozzle.ChamberPressure) && float.IsFinite(nozzle.ChamberTemperature) && float.IsFinite(nozzle.ExitPressure) && float.IsFinite(nozzle.ExitTemperature) && SupportedVector(nozzle.Position) && SupportedVector(nozzle.Axis) && SupportedVector(nozzle.RestPosition) && SupportedVector(nozzle.RestAxis), "nozzle");
                    samples.Add(sample with { Rotation = ReplayTimeline.Normalize(sample.Rotation) });
                }
                List<ReplayChannel>? channels = segment.Channels;
                if (channels != null)
                {
                    Require(channels.TrueForAll(channel => channel != null && ReplayChannelKind.IsKnown(channel.Kind) && channel.Times != null && channel.Values != null
                        && channel.Times.Count > 0 && channel.Times.Count == channel.Values.Count), "part channel");
                    foreach (ReplayChannel channel in channels)
                    {
                        double previousKey = double.NegativeInfinity;
                        for (int k = 0; k < channel.Times.Count; k++)
                        {
                            double key = channel.Times[k];
                            float value = channel.Values[k];
                            Require(double.IsFinite(key) && key >= 0 && key > previousKey && float.IsFinite(value) && Math.Abs(value) <= 1e6f, "part channel key");
                            previousKey = key;
                        }
                    }

                    // The part state only changes the look of the parts, so a segment over a limit keeps
                    // its flight and loses its channels.
                    long values = channels.Sum(channel => (long)channel.Times.Count);
                    if (channels.Count > ChannelLimit || channelValues + values > ChannelValueLimit)
                        channels = null;
                    else
                        channelValues += values;
                }
                segments.Add(segment with { Samples = samples, Channels = channels, PartKey = channels == null ? null : segment.PartKey });
            }
            double lastTime = segments[^1].Samples[^1].Time;
            Require(actor.End is not double end || (double.IsFinite(end) && end >= lastTime), "actor end");
            actors.Add(actor with { Segments = segments });
        }
        double duration = ReplayTimeline.Duration(flight);
        Require(actors[0].Segments[0].Start == 0, "the primary vehicle does not start at time zero");
        foreach (ReplayMarker marker in flight.Markers)
            Require(marker != null && double.IsFinite(marker.Time) && marker.Time >= 0 && marker.Time <= duration && Text(marker.Label), "marker");
        foreach (ReplayEvent item in flight.Events)
            Require(item != null && double.IsFinite(item.Time) && item.Time >= 0 && item.Time <= duration && item.Name is { Length: > 0 and <= 64 }
                && EventName.TryParse(item.Name, out EventName name, out _) && name.Canonical == item.Name, "event");
        double previousFollow = 0.0;
        foreach (ReplayFollow follow in flight.Follows ?? [])
        {
            Require(follow != null && double.IsFinite(follow.Time) && follow.Time > previousFollow && follow.Time <= duration
                && actors.Find(actor => actor.Id == follow.ActorId) is { } followed && followed.Segments[0].Start <= follow.Time
                && (followed.End is not double followEnd || followEnd > follow.Time), "follow");
            previousFollow = follow!.Time;
        }
        Require((flight.Follows?.Count ?? 0) <= EventLimit, "too many follow changes");
        var views = new List<ReplayView>();
        foreach (ReplayView view in flight.Views)
        {
            Require(view != null && Text(view.Name) && double.IsFinite(view.Time) && view.Time >= 0 && view.Time <= duration
                && ReplayView.SupportsPosition(view.Position, view.ActorId) && double.IsFinite(view.Fov) && view.Fov > 0 && view.Fov < 180, "view");
            views.Add(view! with { Rotation = ReplayTimeline.Normalize(view.Rotation) });
        }
        views.Sort(static (a, b) => a.Time.CompareTo(b.Time));
        var events = flight.Events.OrderBy(item => item.Time).ToList();
        return flight with { Actors = actors, Views = views, Events = events, Follows = flight.Follows is { Count: > 0 } follows ? [.. follows] : null };
    }
}
