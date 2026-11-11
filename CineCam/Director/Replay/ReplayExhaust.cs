namespace CineCam.Director.Replay;

public static class ReplayExhaust
{
    /// <summary>
    /// Vehicle.AddVolumetricExhaustInstances shows the plume of a pulsed thruster in a frame with the
    /// probability speed^(2/3) * duty. The replay uses the probability at speed 1 and a hash of the time
    /// and the nozzle instead of a random number, so the same replay time always gives the same picture.
    /// </summary>
    public static bool Lit(float dutyCycle, double time, int nozzle)
    {
        if (!(dutyCycle > 0f))
            return false;
        if (dutyCycle >= 1f)
            return true;
        ulong hash = (ulong)BitConverter.DoubleToInt64Bits(time) ^ ((ulong)(uint)nozzle * 0x9E3779B97F4A7C15UL);
        hash ^= hash >> 33;
        hash *= 0xFF51AFD7ED558CCDUL;
        hash ^= hash >> 33;
        hash *= 0xC4CEB9FE1A85EC53UL;
        hash ^= hash >> 33;
        return (hash >> 40) / (double)(1UL << 24) < dutyCycle;
    }
}
