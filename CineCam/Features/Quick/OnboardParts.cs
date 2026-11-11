using Brutal.Numerics;
using CineCam.Director;
using KSA;

namespace CineCam.Features.Quick;

// The parts an onboard camera can sit on, from the nose to the base. The selection follows the Part
// object when the list is rebuilt after a staging.
internal sealed class OnboardParts
{
    private readonly List<Part> _parts = new();
    private int _partCount = -1;
    private Part? _selected;

    public int Count => _parts.Count;

    public int Index { get; private set; }

    public string SelectedName => _selected == null ? "" : NameOf(_selected);

    public void Refresh(Vehicle vehicle)
    {
        int count = vehicle.Parts.Count;
        if (count == _partCount)
            return;
        _partCount = count;
        _parts.Clear();
        ReadOnlySpan<Part> parts = vehicle.Parts.Parts;
        for (int i = 0; i < parts.Length; i++)
        {
            (double3 min, double3 max) = parts[i].BoundingBoxVehicleAsmb;
            if (max.X > min.X && max.Y >= min.Y && max.Z >= min.Z)
                _parts.Add(parts[i]);
        }

        _parts.Sort((a, b) => b.BoundingBoxVehicleAsmb.Max.X.CompareTo(a.BoundingBoxVehicleAsmb.Max.X));
        int kept = _selected == null ? -1 : _parts.IndexOf(_selected);
        Index = kept >= 0 ? kept : Math.Clamp(Index, 0, Math.Max(_parts.Count - 1, 0));
        _selected = _parts.Count > 0 ? _parts[Index] : null;
    }

    public void Step(int step)
    {
        if (_parts.Count == 0)
            return;
        Index = Director.Quick.QuickMath.Step(Index, step, _parts.Count);
        _selected = _parts[Index];
    }

    // In the body frame of the vehicle, with the origin at the center of mass.
    public bool TryBox(Vehicle vehicle, out V3 min, out V3 max)
    {
        min = max = V3.Zero;
        if (_selected == null)
            return false;
        (double3 lo, double3 hi) = _selected.BoundingBoxVehicleAsmb;
        min = QuickScene.ToV3(vehicle.PosAsmbToBody(lo));
        max = QuickScene.ToV3(vehicle.PosAsmbToBody(hi));
        return true;
    }

    private static string NameOf(Part part) =>
        !string.IsNullOrWhiteSpace(part.DisplayName) ? part.DisplayName : part.Template?.Id ?? part.Id;
}
