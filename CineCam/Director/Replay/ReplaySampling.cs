namespace CineCam.Director.Replay;

// The recorder samples on a fixed grid of simulation time, so the sample count does not depend on the
// frame rate. Every capture tick samples all recorded vehicles, so the tick limit is also the sample
// limit of each vehicle.
public static class ReplaySampling
{
    public const double Interval = 1.0 / 30.0;
    public const int SamplesPerActor = 36_000;
    public const int ActorLimit = 16;

    // Bytes kept free in the replay file for the markers and the metadata.
    public const long SizeReserve = 8L * 1024 * 1024;

    // A vehicle that did not split from the followed vehicle is recorded when it is this near at a scan,
    // and only while SplitReserve actor slots stay free for the stages that can still split.
    public const double NeighborMeters = 2000.0;
    public const int NeighborLimit = 4;
    public const int SplitReserve = 8;

    // A piece of the followed vehicle gets any free slot but the last, which stays for a change of the
    // followed vehicle. A piece of another recorded vehicle needs OtherMinParts parts, a parent with fewer
    // than OtherChildLimit recorded pieces, and StageReserve more free slots, so debris never fills the
    // slots. A vehicle that gets no slot is skipped and the recording goes on.
    public const int FollowReserve = 1;
    public const int StageReserve = 4;
    public const int OtherMinParts = 2;
    public const int OtherChildLimit = 2;

    /// <param name="OfFollowed">True when it split from the followed vehicle or from a piece of it.</param>
    /// <param name="Required">True for the vehicle that the recording follows from now on, which always takes a slot.</param>
    public readonly record struct SplitCandidate(int Parent, bool OfFollowed, int Parts, bool Required = false);

    /// <summary>
    /// <paramref name="followNeedsSlot"/> is true when the followed vehicle is not recorded yet and is not a
    /// candidate. A required candidate gets a slot even when none is free, and the recorder then ends the recording.
    /// </summary>
    public static bool[] AdmitSplits(int actors, bool followNeedsSlot, IReadOnlyList<SplitCandidate> candidates, Func<int, int> children)
    {
        var admitted = new bool[candidates.Count];
        int free = ActorLimit - actors - (followNeedsSlot ? 1 : 0);
        var added = new Dictionary<int, int>();
        IEnumerable<int> order = Enumerable.Range(0, candidates.Count)
            .OrderBy(i => candidates[i].Required ? 0 : candidates[i].OfFollowed ? 1 : 2)
            .ThenByDescending(i => candidates[i].Parts)
            .ThenBy(i => i);
        foreach (int i in order)
        {
            SplitCandidate candidate = candidates[i];
            bool fits;
            if (candidate.Required)
            {
                fits = true;
            }
            else if (candidate.OfFollowed)
            {
                fits = free > FollowReserve;
            }
            else
            {
                int pieces = children(candidate.Parent) + added.GetValueOrDefault(candidate.Parent);
                fits = candidate.Parts >= OtherMinParts && pieces < OtherChildLimit && free > FollowReserve + StageReserve;
            }

            if (!fits)
                continue;
            admitted[i] = true;
            free--;
            if (!candidate.OfFollowed)
                added[candidate.Parent] = added.GetValueOrDefault(candidate.Parent) + 1;
        }

        return admitted;
    }

    // About once per second.
    public const int NeighborScanTicks = 30;

    public static double MinimumSeconds => SamplesPerActor * Interval;

    public static bool NeighborScanDue(int ticks) => ticks % NeighborScanTicks == 0;

    public static bool CanAddNeighbor(int actors, int pendingSplits, int neighbors) =>
        neighbors < NeighborLimit && actors + pendingSplits + 1 <= ActorLimit - SplitReserve;

    // A frame longer than one cell gives one sample, so a time warp records at most one sample per frame.
    // The small offset keeps a frame time equal to the interval from losing a cell to rounding.
    public static bool IsDue(double time, double lastTime) =>
        lastTime < 0 || Cell(time) > Cell(lastTime);

    private static double Cell(double time) => Math.Floor(time / Interval + 1e-6);

    public static bool SizeReached(long bytes) => bytes >= ReplayFile.MaxBytes - SizeReserve;

    public static bool TickFits(long bytes, long tickBytes) => bytes + Math.Max(tickBytes, 0) < ReplayFile.MaxBytes - SizeReserve;
}
