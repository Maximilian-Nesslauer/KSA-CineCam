using System.Globalization;
using System.Xml;
using System.Xml.Serialization;
using Brutal.Logging;
using Brutal.Numerics;
using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Features.Takes;
using KSA;

namespace CineCam.Features.Replay;

internal sealed class FlightRecorder
{
    private readonly Celestial _body;
    private readonly CelestialSystem _system;

    // Follows the controlled vehicle through an undocking, a docking and a breakup, as a live take
    // does (VehicleReader.TryFollow).
    private readonly VehicleReader _primary;
    private readonly EventDetector _events = new([]);
    private readonly Dictionary<Vehicle, Track> _actors = new(ReferenceEqualityComparer.Instance);
    private readonly List<(Vehicle Source, Vehicle Child, bool Neighbor, bool OfFollowed)> _splits = [];
    private readonly Dictionary<Vehicle, int> _pieces = new(ReferenceEqualityComparer.Instance);
    private int _skipped;
    private string? _note;
    private readonly List<Vehicle> _neighbors = [];
    private readonly HashSet<Vehicle> _seen = new(ReferenceEqualityComparer.Instance);
    private double _lastTime = -1;
    private int _ticks;
    private long _bytes;
    private bool _partStateFailed;
    private bool _channelLimitLogged;
    private bool _partStateMissed;
    private bool _followChanged;
    private int _pendingMarks;

    private sealed record Track(Vehicle Vehicle, ReplayActor Actor)
    {
        public RocketNozzle[] Nozzles { get; set; } = [];
        public int PartCount { get; set; }
        public ReplayPartState? PartState { get; set; }
        public ReplayChannelWriter? Channels { get; set; }
    }

    private sealed record CaptureFrame(Track Track, RocketNozzle[] Nozzles, ReplaySegment? Segment, ReplaySample Sample,
        ReplayPartState? PartState, ReplayChannelWriter? Channels, long ChannelBytes);

    private const int PrimaryId = 0;

    public FlightRecorder(CelestialSystem system, Celestial body, Vehicle vehicle)
    {
        _system = system;
        _body = body;
        _primary = new VehicleReader(vehicle);
        Flight = new ReplayFlight(VersionInfo.Current.VersionString, system.Id, body.Id,
            Universe.GetElapsedSeconds(), [], []) { Follows = [] };
        AddTrack(new Track(vehicle, new ReplayActor(PrimaryId, vehicle.Id, [])));
        Capture();
    }

    public ReplayFlight Flight { get; }
    public double Duration => Math.Max(0, _lastTime);

    public string FollowedName => _primary.Vehicle.Id;

    /// <summary>The next sample gives the marks to the event detector as "mark", "mark:2" and so on.</summary>
    public void AddMark() => _pendingMarks++;

    /// <summary>A status note once, after the first skipped vehicle, and null otherwise.</summary>
    public string? TakeNote()
    {
        string? note = _note;
        _note = null;
        return note;
    }

    public void Split(Vehicle source, Vehicle child)
    {
        if (_actors.TryGetValue(source, out Track? track))
        {
            bool ofFollowed = ReferenceEquals(source, _primary.Vehicle);
            _splits.Add((source, child, track.Actor.Neighbor && !ofFollowed, ofFollowed));
        }
        else if (_splits.FindIndex(split => ReferenceEquals(split.Child, source)) is var index and >= 0)
        {
            _splits.Add((source, child, _splits[index].Neighbor, _splits[index].OfFollowed));
        }
    }

    public string? Capture()
    {
        if (!ReferenceEquals(Universe.CurrentSystem, _system) || Program.Editor != null)
            return "The active scene changed.";
        if (_ticks >= ReplaySampling.SamplesPerActor)
            return "The recording reached its length limit.";
        if (ReplaySampling.SizeReached(_bytes))
            return "The recording reached its file size limit.";
        FollowControlled();
        if (_primary.Vehicle.IsDisposed)
            FollowSurvivor();
        Vehicle followed = _primary.Vehicle;
        if (Lost(followed))
            return "The recorded vehicle was lost or changed its parent body.";

        double time = Universe.GetElapsedSeconds() - Flight.StartTime;
        if (time < _lastTime && _splits.Count == 0)
            return time < _lastTime - 1e-9 ? "The game clock moved backwards." : null;
        if (time <= _lastTime && _splits.Count == 0)
            return null;
        if (time <= _lastTime)
            time = Math.BitIncrement(_lastTime);

        // Another vehicle that is lost or leaves the body ends its track, and the recording goes on.
        EndLost(time);
        AdmitSplits(followed);

        // Only a change of the followed vehicle can need a slot that is not free, because the slot
        // rule keeps one free for it. Without the followed vehicle the recording has no subject.
        if (!_actors.ContainsKey(followed) && Flight.Actors.Count + _splits.Count + (_splits.Any(split => ReferenceEquals(split.Child, followed)) ? 0 : 1) > ReplaySampling.ActorLimit)
            return "The recording reached its vehicle limit.";

        // A split or a changed part count starts a new segment at once, so the segment starts
        // where the vehicle changed. Other samples wait for the next step of the sampling grid.
        bool changed = _splits.Count > 0 || _followChanged || AnyPartCountChanged();
        if (!changed && !ReplaySampling.IsDue(time, _lastTime))
            return null;
        if (ReplaySampling.NeighborScanDue(_ticks))
            FindNeighbors();

        // The detector reads the vehicle once per tick, and the same values go into the state of the
        // sample, so a replay sees what the detector saw.
        _primary.RefreshParts();
        VehicleSample vehicleSample = _primary.Sample(_pendingMarks);
        ReplayState state = StateOf(vehicleSample);
        doubleQuat eclToBodyFixed = _body.GetCce2Ccf();
        double3 bodyPosition = _body.GetPositionEcl();
        var frames = new List<CaptureFrame>(_actors.Count + _splits.Count + _neighbors.Count);
        foreach (Track track in _actors.Values)
        {
            bool newSegment = track.Actor.Segments.Count == 0 || track.PartCount != track.Vehicle.Parts.Count
                || _splits.Any(split => ReferenceEquals(split.Source, track.Vehicle));
            frames.Add(ReadFrame(track, newSegment));
        }

        int nextActorId = Flight.Actors.Count;
        foreach ((Vehicle _, Vehicle child, bool neighbor, bool _) in _splits)
            frames.Add(ReadFrame(new Track(child, new ReplayActor(nextActorId++, child.Id, []) { Neighbor = neighbor }), true));
        foreach (Vehicle neighbor in _neighbors)
            frames.Add(ReadFrame(new Track(neighbor, new ReplayActor(nextActorId++, neighbor.Id, []) { Neighbor = true }), true));

        // A vehicle that the recording follows from now on and that it did not record yet, for
        // example the other vehicle of a docking that was too far for a neighbor scan.
        if (!frames.Any(frame => ReferenceEquals(frame.Track.Vehicle, followed)))
            frames.Add(ReadFrame(new Track(followed, new ReplayActor(nextActorId++, followed.Id, [])), true));

        // The new segments of one tick hold the part definitions of whole vehicles, which can be
        // larger than the reserve, so the tick is measured before it is added.
        long tickBytes = 0;
        foreach (CaptureFrame frame in frames)
        {
            if (frame.Segment != null)
                tickBytes += ReplayFile.MeasureText(frame.Segment.Definition) + 64;
            tickBytes += ReplayFile.MeasureSample(frame.Sample) + frame.ChannelBytes;
        }

        if (!ReplaySampling.TickFits(_bytes, tickBytes))
            return "The recording reached its file size limit.";

        IReadOnlyList<DetectedEvent> markers = _events.Update(time, vehicleSample);
        _pendingMarks = 0;
        foreach (CaptureFrame frame in frames)
        {
            Track track = frame.Track;
            if (!_actors.ContainsKey(track.Vehicle))
                AddTrack(track);
            if (frame.Segment != null)
            {
                track.Nozzles = frame.Nozzles;
                track.PartCount = track.Vehicle.Parts.Count;
                track.PartState = frame.PartState;
                track.Channels = frame.Channels;
                track.Actor.Segments.Add(frame.Segment);
            }
            frame.Channels?.Commit();
            track.Actor.Segments[^1].Samples.Add(frame.Sample);
        }
        _bytes += tickBytes;
        _ticks++;

        if (_followChanged)
        {
            _followChanged = false;
            int followedId = _actors[followed].Actor.Id;
            List<ReplayFollow> follows = Flight.Follows!;
            int current = follows.Count > 0 ? follows[^1].ActorId : PrimaryId;
            if (followedId != current && follows.Count < ReplayFile.EventLimit)
                follows.Add(new ReplayFollow(time, followedId));
        }

        foreach ((Vehicle source, Vehicle child, bool neighbor, bool ofFollowed) in _splits)
        {
            if (!ofFollowed)
                _pieces[source] = _pieces.GetValueOrDefault(source) + 1;
            if (!neighbor)
                Flight.Markers.Add(new ReplayMarker(time, $"{(ofFollowed ? "Separation" : "Breakup")}: {child.Id}"));
        }
        _splits.Clear();
        _neighbors.Clear();
        foreach (DetectedEvent marker in markers)
        {
            if (Flight.Events.Count < ReplayFile.EventLimit)
                Flight.Events.Add(new ReplayEvent(time, marker.Name));
            if (marker.Name.StartsWith("ignition", StringComparison.Ordinal))
                Flight.Markers.Add(new ReplayMarker(time, "Ignition" + marker.Name["ignition".Length..]));
            else if (marker.Name == "liftoff")
                Flight.Markers.Add(new ReplayMarker(time, "Lift-off"));
        }

        _lastTime = time;
        return null;

        CaptureFrame ReadFrame(Track track, bool newSegment)
        {
            PartTree parts = track.Vehicle.Parts;
            RocketNozzle[] nozzles = newSegment ? ReplayParts.OrderedNozzles(parts) : track.Nozzles;
            (ReplayPartState? partState, ReplayChannelWriter? channels, long channelBytes) = ReadPartState(track, newSegment, time);
            ReplaySegment? segment = newSegment
                ? new ReplaySegment(time, ReplayParts.Encode(parts), [])
                {
                    CenterOfMass = ToV3(track.Vehicle.CenterOfMassAsmb),
                    Channels = channels?.Channels,
                    PartKey = channels != null ? partState?.Key : null,
                }
                : null;
            ReplaySample sample = Sample(track.Vehicle, nozzles, time, bodyPosition, eclToBodyFixed);
            if (ReferenceEquals(track.Vehicle, followed))
                sample = sample with { State = state };
            return new CaptureFrame(track, nozzles, segment, sample, partState, channels, channelBytes);
        }
    }

    // The part state is a part of the picture, not of the flight, so a failure to read it leaves the
    // segment without channels and the recording goes on.
    private (ReplayPartState? State, ReplayChannelWriter? Channels, long Bytes) ReadPartState(Track track, bool newSegment, double time)
    {
        ReplayPartState? partState = newSegment ? null : track.PartState;
        ReplayChannelWriter? channels = newSegment ? null : track.Channels;
        try
        {
            if (newSegment)
            {
                partState = ReplayPartState.Of(track.Vehicle.Parts);
                if (partState.Kinds.Count > ReplayFile.ChannelLimit)
                {
                    // ReplayFile drops the channels of such a segment, so the recorder does not read them.
                    if (!_channelLimitLogged)
                        DefaultCategory.Log.Warning($"[CineCam] The replay records no part state of {track.Vehicle.Id}, because it has {partState.Kinds.Count} part channels and the limit is {ReplayFile.ChannelLimit}.");
                    _channelLimitLogged = true;
                    return (null, null, 0);
                }
                if (partState.Kinds.Count > 0)
                    channels = new ReplayChannelWriter(partState.Kinds);
            }
            if (partState == null || channels == null)
                return (partState, null, 0);
            long bytes = channels.Prepare(time, partState.Read());
            if (partState.Missed is { } part && !_partStateMissed)
            {
                DefaultCategory.Log.Warning($"[CineCam] The replay keeps the last part state of part {part} of {track.Vehicle.Id}, because the game has no state of a module of it now.");
                _partStateMissed = true;
            }
            return (partState, channels, bytes);
        }
        catch (Exception ex)
        {
            if (!_partStateFailed)
                DefaultCategory.Log.Warning($"[CineCam] The replay records no part state of {track.Vehicle.Id}: {ex.Message}");
            _partStateFailed = true;
            // The keys that a segment already has stay, and the rest of the segment holds the last values.
            if (!newSegment)
            {
                track.PartState = null;
                track.Channels = null;
            }
            return (null, null, 0);
        }
    }

    // The recording follows the controlled vehicle when it has parts of the followed one, as a live
    // take does. DockingPort.Undock and PartFailureEvent.Apply give the control to a vehicle that
    // Vehicle.Split made from the parts, and DockingPort.Dock gives it to the other vehicle, which took
    // the parts in Vehicle.MergeFrom. A switch to another vehicle does not change the recording.
    private void FollowControlled()
    {
        Vehicle? current = Program.ControlledVehicle;
        if (current == null || current.IsDisposed || current is KittenEva || ReferenceEquals(current, _primary.Vehicle)
            || !ReferenceEquals(current.Parent, _body))
            return;
        if (_primary.TryFollow(current))
        {
            _followChanged = true;
            DefaultCategory.Log.Info($"[CineCam] The recording follows the controlled vehicle {current.Id}, which has parts of the vehicle before.");
        }
    }

    // DockingPort.Dock merges the vehicle into the other one and disposes it (Vehicle.MergeFrom), but
    // the control moves to the survivor only in some cases (the docking input, or DockingEvent.Apply
    // while the camera followed the merged vehicle). So the recording looks for the vehicle that has
    // the parts now, whatever has the control.
    private void FollowSurvivor()
    {
        foreach (Vehicle vehicle in _system.All.OfType<Vehicle>())
        {
            if (vehicle is KittenEva || Lost(vehicle))
                continue;
            if (_primary.TryFollow(vehicle))
            {
                _followChanged = true;
                DefaultCategory.Log.Info($"[CineCam] The recording follows {vehicle.Id}, which has the parts of the recorded vehicle now.");
                return;
            }
        }
    }

    // Gives the vehicles that split in this tick a slot by the rule of ReplaySampling.AdmitSplits. A
    // skipped vehicle is never recorded later, also not as a neighbor, and its own pieces are skipped
    // with it, because Split only takes pieces of recorded or admitted vehicles.
    private void AdmitSplits(Vehicle followed)
    {
        if (_splits.Count == 0)
            return;
        var parents = new List<Vehicle>();
        var candidates = new ReplaySampling.SplitCandidate[_splits.Count];
        bool followedIsCandidate = false;
        for (int i = 0; i < _splits.Count; i++)
        {
            (Vehicle source, Vehicle child, bool _, bool ofFollowed) = _splits[i];
            int parent = parents.FindIndex(vehicle => ReferenceEquals(vehicle, source));
            if (parent < 0)
            {
                parent = parents.Count;
                parents.Add(source);
            }

            bool required = ReferenceEquals(child, followed);
            followedIsCandidate |= required;
            candidates[i] = new ReplaySampling.SplitCandidate(parent, ofFollowed || required, child.Parts.Count, required);
        }

        bool followNeedsSlot = !_actors.ContainsKey(followed) && !followedIsCandidate;
        bool[] admitted = ReplaySampling.AdmitSplits(Flight.Actors.Count, followNeedsSlot, candidates,
            parent => _pieces.GetValueOrDefault(parents[parent]));
        var kept = new List<(Vehicle Source, Vehicle Child, bool Neighbor, bool OfFollowed)>(_splits.Count);
        int skipped = 0;
        var dropped = new HashSet<Vehicle>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < _splits.Count; i++)
        {
            if (admitted[i] && (!dropped.Contains(_splits[i].Source) || ReferenceEquals(_splits[i].Child, followed)))
            {
                kept.Add(_splits[i]);
                continue;
            }

            dropped.Add(_splits[i].Child);
            _seen.Add(_splits[i].Child);
            skipped++;
        }

        if (skipped == 0)
            return;
        _splits.Clear();
        _splits.AddRange(kept);
        if (_skipped == 0)
        {
            _note = "Some debris is not recorded, because the recording keeps its vehicle slots for the followed vehicle and its stages.";
            DefaultCategory.Log.Info($"[CineCam] The recording skips {skipped} new vehicles, because it keeps its {ReplaySampling.ActorLimit} vehicle slots for the followed vehicle and its stages. Later skips are not logged.");
        }
        _skipped += skipped;
    }

    private bool AnyPartCountChanged()
    {
        foreach (Track track in _actors.Values)
        {
            if (track.PartCount != track.Vehicle.Parts.Count)
                return true;
        }
        return false;
    }

    private bool Lost(Vehicle vehicle) => vehicle.IsDisposed || !ReferenceEquals(vehicle.Parent, _body);

    // A docking disposes the vehicle that merges into the other one (Vehicle.MergeFrom), and a stage
    // can crash or leave the body. Such a track ends, and the replay stops drawing it at that time.
    private void EndLost(double time)
    {
        List<Track>? lost = null;
        foreach (Track track in _actors.Values)
        {
            if (!ReferenceEquals(track.Vehicle, _primary.Vehicle) && Lost(track.Vehicle))
                (lost ??= []).Add(track);
        }
        foreach (Track track in lost ?? [])
        {
            int index = Flight.Actors.FindIndex(actor => actor.Id == track.Actor.Id);
            if (index >= 0)
                Flight.Actors[index] = Flight.Actors[index] with { End = time };
            _actors.Remove(track.Vehicle);
        }
        if (_splits.Count > 0)
            _splits.RemoveAll(split => Lost(split.Child));
        if (_neighbors.Count > 0)
            _neighbors.RemoveAll(Lost);
    }

    // Vehicles near the followed vehicle that did not split from it, for example a docking target or a
    // station. A vehicle that was recorded once is not added again.
    private void FindNeighbors()
    {
        double3 origin = _primary.Vehicle.GetPositionEcl();
        int neighbors = Flight.Actors.Count(actor => actor.Neighbor) + _neighbors.Count;
        foreach (Vehicle vehicle in _system.All.OfType<Vehicle>())
        {
            if (vehicle is KittenEva || Lost(vehicle) || _seen.Contains(vehicle)
                || _splits.Any(split => ReferenceEquals(split.Child, vehicle)))
                continue;
            if ((vehicle.GetPositionEcl() - origin).Length() > ReplaySampling.NeighborMeters)
                continue;
            if (!ReplaySampling.CanAddNeighbor(Flight.Actors.Count + _neighbors.Count, _splits.Count, neighbors))
                return;
            _neighbors.Add(vehicle);
            _seen.Add(vehicle);
            neighbors++;
        }
    }

    private void AddTrack(Track track)
    {
        Flight.Actors.Add(track.Actor);
        _actors.Add(track.Vehicle, track);
        _seen.Add(track.Vehicle);
    }

    // The position is the assembly origin relative to the body center and the rotation is assembly
    // to body-fixed, both in the body-fixed frame (CCF), so a replay follows the turning ground.
    private ReplaySample Sample(Vehicle vehicle, RocketNozzle[] modules, double time, double3 bodyPosition, doubleQuat eclToBodyFixed)
    {
        double3 origin = vehicle.GetPositionEcl() - bodyPosition
            - vehicle.CenterOfMassAsmb.Transform(vehicle.Asmb2Cce);
        doubleQuat orientation = doubleQuat.Concatenate(vehicle.Asmb2Cce, eclToBodyFixed);
        var nozzles = new ReplayNozzle[modules.Length];
        bool burning = false;
        for (int i = 0; i < modules.Length; i++)
        {
            RocketNozzle nozzle = modules[i];
            RocketNozzleState state = vehicle.Parts.RocketNozzles.GetStateByIdx(nozzle.StatesIdx);
            if (!(state.DutyCycle > 0) || !(state.Exhaust.Pressure > 0) || !(state.ExhaustVelocity > 0))
                continue;
            RocketCoreConditions core = vehicle.Parts.RocketCores.States[nozzle.Rocket.Core.StatesIdx].Conditions;
            nozzles[i] = new ReplayNozzle(state.DutyCycle, state.ExhaustVelocity,
                state.Exhaust.Pressure, state.Exhaust.Temperature, core.Gas.Gamma, core.Gas.SpecificGasConstant,
                core.Core.Pressure, core.Core.Temperature, core.Exit.Pressure, core.Exit.Temperature,
                ToV3(double3.Unpack(state.FxExhaustLocationVehicleAsmb)),
                ToV3(double3.Unpack(state.FxExhaustDirectionVehicleAsmb)),
                ToV3(double3.Unpack(state.FxRestExhaustLocationVehicleAsmb)),
                ToV3(double3.Unpack(state.FxRestExhaustDirectionVehicleAsmb)));
            burning = true;
        }

        float airDensity = (float)(_body.GetPhysicalAtmosphere()?.ComputeDensityAtAltitude(
            (vehicle.GetPositionEcl() - bodyPosition).Length() - _body.MeanRadius) ?? 0.0);
        double3 airVelocity = vehicle.GetSurfaceVelocityCci().Transform(_body.GetCci2Ccf());
        // A sample without a burning engine stores no engine list, which keeps long coasts small.
        return new ReplaySample(time, ToV3(origin.Transform(eclToBodyFixed)), ToRotation(orientation),
            burning ? nozzles : [], vehicle.GetPhysicsStatesMutable().Environment.AtmosphericPressure,
            airDensity, ToV3(airVelocity));
    }

    private static ReplayState StateOf(in VehicleSample sample)
    {
        int flags = (sample.EngineOn ? ReplayStateFlags.EngineOn : 0) | (sample.Contact ? ReplayStateFlags.Contact : 0)
            | (sample.StableOrbit ? ReplayStateFlags.StableOrbit : 0) | (sample.OutsideAtmosphere ? ReplayStateFlags.OutsideAtmosphere : 0);
        return new ReplayState(flags, Math.Max(sample.ActiveSequence, 0), Math.Max(sample.DockedPorts, 0),
            Finite(sample.Altitude), Finite(sample.Speed), Finite(sample.RadialSpeed));

        // The file reader rejects a value that is not finite, so such a value is stored as 0.
        static float Finite(double value) => (float)value is float f && float.IsFinite(f) ? f : 0f;
    }

    internal static V3 ToV3(double3 value) => new(value.X, value.Y, value.Z);
    internal static ReplayRotation ToRotation(doubleQuat value) => new(value.X, value.Y, value.Z, value.W);
}

internal static class ReplayParts
{
    private static readonly Lazy<XmlSerializer> Serializer = new(() =>
        new XmlSerializer(typeof(PartTreeData), XmlHelper.AttributeOverrides));

    public static string Encode(PartTree parts)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Serializer.Value.Serialize(writer, parts.Serialize());
        return writer.ToString();
    }

    public static PartTree Decode(string definition)
    {
        using var text = new StringReader(definition);
        using var xml = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        if (Serializer.Value.Deserialize(xml) is not PartTreeData data || data.Root == null)
            throw new InvalidDataException("The recorded vehicle has no part tree.");
        data.Root.OnDataLoad(KSA.Mod.Empty);
        if (data.Root.FindMissingParts() is { } missing)
            throw new InvalidDataException($"Install the recorded vehicle parts: {missing}");
        return PartTree.Deserialize(data);
    }

    // The state list can reorder when a stage leaves. The serializer uses the part tree order.
    public static RocketNozzle[] OrderedNozzles(PartTree tree)
    {
        var result = new List<RocketNozzle>();
        var iterator = new PartTreeIterator(tree.Root);
        while (iterator.GetNextNode() is { } part)
        {
            Add(part);
            foreach (Part subpart in part.SubParts)
                Add(subpart);
        }
        return result.ToArray();

        void Add(Part part)
        {
            foreach (RocketNozzle nozzle in part.Modules.Get<RocketNozzle>())
                result.Add(nozzle);
        }
    }
}
