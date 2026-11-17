using System.Diagnostics;

namespace CineCam.Director.Sound;

/// <summary>The published progress or final result of a sound capture.</summary>
public sealed record SoundCaptureResult(string Path, int SampleRate, long Frames, long DroppedFrames, bool Full, string? Error)
{
    public double Seconds => SampleRate > 0 ? Frames / (double)SampleRate : 0.0;

    public double DroppedSeconds => SampleRate > 0 ? DroppedFrames / (double)SampleRate : 0.0;
}

/// <summary>
/// Records blocks of sound to a stereo WAV file. <see cref="Push"/> runs on the thread of the sound
/// mixer: it takes no lock, does not allocate and never waits, and puts the samples into a ring that a
/// writer thread empties to the disk. A block that does not fit into the ring is lost, and the writer
/// puts silence of the same length in its place, so the file keeps the timing of the mix.
/// </summary>
public sealed partial class SoundCapture
{
    /// <summary>The frames of one push that are mixed at a time, which bounds the scratch buffer.</summary>
    public const int ChunkFrames = 4096;

    private const int GapSlots = 64;
    private const int OutputChannels = 2;

    private readonly SoundRing _ring;
    private readonly float[] _scratch = new float[ChunkFrames * OutputChannels];
    private readonly long[] _gapAt = new long[GapSlots];
    private readonly long[] _gapFrames = new long[GapSlots];
    private readonly WavWriter _writer;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly int _flushMilliseconds;

    // Written only by the producer.
    private long _gapWritten;
    private long _pendingGapAt = -1;
    private long _pendingGapFrames;
    private long _unplacedFrames;
    private long _droppedFrames;
    private long _firstTimestamp;

    // Written only by the writer thread.
    private long _gapRead;
    private string? _error;
    private long _frames;
    private bool _full;

    private volatile bool _accepting = true;

    /// <param name="ringSeconds">How long the writer thread may stall before blocks are lost.</param>
    public SoundCapture(string path, int sampleRate, double ringSeconds = 8.0, int flushMilliseconds = 1000)
    {
        if (sampleRate < 8000 || sampleRate > 384000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        Path = path;
        SampleRate = sampleRate;
        _flushMilliseconds = flushMilliseconds;
        _ring = new SoundRing((int)Math.Clamp(sampleRate * ringSeconds * OutputChannels, ChunkFrames * OutputChannels * 4, 1 << 26));
        _writer = new WavWriter(path, sampleRate, OutputChannels);
        _thread = new Thread(WriteLoop) { IsBackground = true, Name = "CineCam sound writer" };
        _thread.Start();
    }

    public string Path { get; }

    public int SampleRate { get; }

    internal bool IsWriting => _thread.IsAlive;

    /// <summary>The <see cref="Stopwatch"/> timestamp of the first block, or 0 before it came.</summary>
    public long FirstTimestamp => Volatile.Read(ref _firstTimestamp);

    /// <summary>Mixer thread. The source holds <paramref name="frames"/> frames of <paramref name="channels"/> interleaved samples.</summary>
    public void Push(ReadOnlySpan<float> source, int channels, int frames, long timestamp)
    {
        if (!_accepting || channels < 1 || frames <= 0 || source.Length < frames * channels)
            return;
        if (_firstTimestamp == 0)
            Volatile.Write(ref _firstTimestamp, timestamp == 0 ? 1 : timestamp);
        int done = 0;
        while (done < frames)
        {
            int count = Math.Min(ChunkFrames, frames - done);
            Span<float> stereo = _scratch.AsSpan(0, count * OutputChannels);
            SoundDownmix.ToStereo(source.Slice(done * channels, count * channels), channels, count, stereo);
            if (_pendingGapAt >= 0 && TryPublishGap())
                _pendingGapAt = -1;
            if (_pendingGapAt < 0 && _ring.TryWrite(stereo))
            {
                done += count;
                continue;
            }

            if (_pendingGapAt < 0)
            {
                _pendingGapAt = _ring.Written;
                _pendingGapFrames = 0;
            }

            _pendingGapFrames += count;
            Volatile.Write(ref _droppedFrames, _droppedFrames + count);
            done += count;
        }
    }

    // A gap goes into the list only when the ring has room again, so the writer finds it at the
    // ring position where the lost blocks belong.
    private bool TryPublishGap()
    {
        if (_ring.Capacity - _ring.Available < ChunkFrames * OutputChannels)
            return false;
        if (_gapWritten - Volatile.Read(ref _gapRead) >= GapSlots)
        {
            Volatile.Write(ref _unplacedFrames, _unplacedFrames + _pendingGapFrames);
            return true;
        }

        int slot = (int)(_gapWritten % GapSlots);
        _gapAt[slot] = _pendingGapAt;
        _gapFrames[slot] = _pendingGapFrames;
        Volatile.Write(ref _gapWritten, _gapWritten + 1);
        return true;
    }

    private void WriteLoop()
    {
        var buffer = new float[16384];
        long lastFlush = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                bool stopping = _stop.Wait(20);
                Drain(buffer);
                PublishProgress();
                long now = Stopwatch.GetTimestamp();
                if ((now - lastFlush) * 1000 >= _flushMilliseconds * Stopwatch.Frequency)
                {
                    _writer.Flush();
                    lastFlush = now;
                }

                if (stopping)
                    break;
            }

            Drain(buffer);
            long tail = _pendingGapAt >= 0 ? _pendingGapFrames : 0;
            _writer.WriteSilence(tail + Volatile.Read(ref _unplacedFrames));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Volatile.Write(ref _error, ex.Message);
            _accepting = false;
        }
        finally
        {
            try
            {
                _writer.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                Volatile.Write(ref _error, _error ?? ex.Message);
            }

            PublishProgress();
        }
    }

    private void PublishProgress()
    {
        Volatile.Write(ref _frames, _writer.Frames);
        Volatile.Write(ref _full, _writer.Full);
    }

    private void Drain(float[] buffer)
    {
        while (true)
        {
            // The producer publishes a gap before any later audio. Read the audio cursor first,
            // so a gap published after the check cannot be crossed by this read.
            long limit = _ring.Written - _ring.Read;
            if (_gapRead < Volatile.Read(ref _gapWritten))
            {
                int slot = (int)(_gapRead % GapSlots);
                if (_ring.Read >= _gapAt[slot])
                {
                    _writer.WriteSilence(_gapFrames[slot]);
                    Volatile.Write(ref _gapRead, _gapRead + 1);
                    continue;
                }

                limit = Math.Min(limit, _gapAt[slot] - _ring.Read);
            }

            BeforeRead();
            int count = _ring.ReadInto(buffer.AsSpan(0, (int)Math.Min(buffer.Length, limit)));
            if (count == 0)
                return;
            _writer.Write(buffer.AsSpan(0, count));
        }
    }

    partial void BeforeRead();

    /// <summary>
    /// Main thread, after the producer stopped calling <see cref="Push"/>. Requests that the writer
    /// drain the sound and close the file. A timeout returns the last published progress with an
    /// error while the writer keeps ownership of the file. Call again to wait for its final result.
    /// </summary>
    public SoundCaptureResult Stop(int timeoutMilliseconds = 5000)
    {
        _accepting = false;
        _stop.Set();
        bool joined = _thread.Join(timeoutMilliseconds);
        string? error = Volatile.Read(ref _error);
        if (!joined)
            error ??= "the sound writer did not end in time";
        return new SoundCaptureResult(Path, SampleRate, Volatile.Read(ref _frames), Volatile.Read(ref _droppedFrames),
            Volatile.Read(ref _full), error);
    }
}
