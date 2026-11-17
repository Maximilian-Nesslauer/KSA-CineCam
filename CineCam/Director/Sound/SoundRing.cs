namespace CineCam.Director.Sound;

/// <summary>
/// A ring of samples for one producer thread and one consumer thread. Neither side takes a lock or
/// allocates: each side writes only its own position, and the other side reads it with a volatile
/// read, so the producer can be the sound mixer thread of the game.
/// </summary>
public sealed class SoundRing
{
    private readonly float[] _buffer;
    private readonly int _mask;
    private long _written;
    private long _read;

    /// <param name="capacity">The number of samples, rounded up to a power of two.</param>
    public SoundRing(int capacity)
    {
        if (capacity < 2 || capacity > 1 << 28)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        int size = 1;
        while (size < capacity)
            size <<= 1;
        _buffer = new float[size];
        _mask = size - 1;
    }

    public int Capacity => _buffer.Length;

    /// <summary>All samples that the producer wrote since the start.</summary>
    public long Written => Volatile.Read(ref _written);

    /// <summary>All samples that the consumer read since the start.</summary>
    public long Read => Volatile.Read(ref _read);

    public int Available => (int)(Volatile.Read(ref _written) - Volatile.Read(ref _read));

    /// <summary>Producer side. Writes all samples or none, and returns false when they do not fit.</summary>
    public bool TryWrite(ReadOnlySpan<float> samples)
    {
        long written = _written;
        long free = _buffer.Length - (written - Volatile.Read(ref _read));
        if (samples.Length > free)
            return false;
        int start = (int)(written & _mask);
        int first = Math.Min(samples.Length, _buffer.Length - start);
        samples[..first].CopyTo(_buffer.AsSpan(start));
        samples[first..].CopyTo(_buffer);
        Volatile.Write(ref _written, written + samples.Length);
        return true;
    }

    /// <summary>Consumer side. Reads at most the length of the target and returns the count.</summary>
    public int ReadInto(Span<float> target)
    {
        long read = _read;
        int count = (int)Math.Min(target.Length, Volatile.Read(ref _written) - read);
        if (count <= 0)
            return 0;
        int start = (int)(read & _mask);
        int first = Math.Min(count, _buffer.Length - start);
        _buffer.AsSpan(start, first).CopyTo(target);
        _buffer.AsSpan(0, count - first).CopyTo(target[first..]);
        Volatile.Write(ref _read, read + count);
        return count;
    }
}
