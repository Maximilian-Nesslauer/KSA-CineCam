using Brutal.Numerics;
using CineCam.Director;
using KSA;

namespace CineCam.Features.Takes;

// Vehicle.Split, which Decoupler.SetIsActive calls, makes the separated part a new Vehicle in the
// same CelestialSystem with the same Parent. So a vehicle that was not in CelestialSystem.All at
// the take start, has the same Parent and is close to the controlled vehicle when it first shows up
// came from a split. Candidates stay in the order they appeared.
internal sealed class SeparationTracker
{
    // A new vehicle farther than this from the controlled one when it first shows up is not a split part.
    private const double NearMeters = 200.0;

    private sealed class Candidate
    {
        public Candidate(Vehicle vehicle) => Reader = new VehicleReader(vehicle);

        public VehicleReader Reader { get; }
    }

    private readonly HashSet<Vehicle> _known = new(ReferenceEqualityComparer.Instance);
    private readonly List<Candidate> _candidates = new();
    private readonly Vehicle _controlled;

    public SeparationTracker(Vehicle controlled)
    {
        _controlled = controlled;
        if (Universe.CurrentSystem is { } system)
        {
            foreach (Vehicle vehicle in system.All.OfType<Vehicle>())
                _known.Add(vehicle);
        }
    }

    public int Count => _candidates.Count;

    public void Update(double realSeconds)
    {
        if (Universe.CurrentSystem is not { } system)
            return;
        double3 controlledPosition = _controlled.GetPositionEcl();
        var present = new HashSet<Vehicle>(ReferenceEqualityComparer.Instance);
        foreach (Vehicle vehicle in system.All.OfType<Vehicle>())
        {
            present.Add(vehicle);
            if (!_known.Add(vehicle) || ReferenceEquals(vehicle, _controlled) || vehicle is KittenEva || vehicle.IsDisposed)
                continue;
            if (!ReferenceEquals(vehicle.Parent, _controlled.Parent))
                continue;
            if ((vehicle.GetPositionEcl() - controlledPosition).Length() > NearMeters)
                continue;
            _candidates.Add(new Candidate(vehicle));
        }

        for (int i = _candidates.Count - 1; i >= 0; i--)
        {
            Vehicle candidate = _candidates[i].Reader.Vehicle;
            if (candidate.IsDisposed || !present.Contains(candidate))
                _candidates.RemoveAt(i);
            else
                _candidates[i].Reader.RefreshExtent(realSeconds);
        }
    }

    public void Sample(SiteFrame site, out SubjectSample? newest, out SubjectSample? nearest)
    {
        newest = null;
        nearest = null;
        if (_candidates.Count == 0)
            return;

        double3 controlledPosition = _controlled.GetPositionEcl();
        Candidate nearestCandidate = _candidates[0];
        double best = double.MaxValue;
        foreach (Candidate candidate in _candidates)
        {
            double distance = (candidate.Reader.Vehicle.GetPositionEcl() - controlledPosition).Length();
            if (distance < best)
            {
                best = distance;
                nearestCandidate = candidate;
            }
        }

        newest = Describe(_candidates[^1], site, controlledPosition);
        nearest = Describe(nearestCandidate, site, controlledPosition);
    }

    private static SubjectSample Describe(Candidate candidate, SiteFrame site, double3 controlledPosition) => new()
    {
        Name = candidate.Reader.Vehicle.Id,
        Frame = candidate.Reader.Frame(site),
        Height = candidate.Reader.Height,
        Width = candidate.Reader.Width,
        PartLookup = candidate.Reader.FindPart,
        Distance = (candidate.Reader.Vehicle.GetPositionEcl() - controlledPosition).Length(),
    };
}
