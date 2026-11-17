using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace CineCam.Director.Sound;

/// <summary>
/// Writes a WAV file of 32-bit float samples (WAVE_FORMAT_IEEE_FLOAT with a fact chunk). The sizes in
/// the header are written again at each <see cref="Flush"/>, so a file stays readable up to its last
/// flush when the game ends without a clean stop.
/// </summary>
public sealed class WavWriter : IDisposable
{
    public const int HeaderBytes = 58;

    /// <summary>The RIFF size field has 32 bits, so the data stops before 4 GiB.</summary>
    public const long MaxDataBytes = uint.MaxValue - HeaderBytes;

    private readonly FileStream _file;
    private readonly byte[] _bytes = new byte[64 * 1024];
    private long _dataBytes;
    private bool _disposed;

    public WavWriter(string path, int sampleRate, int channels)
    {
        if (sampleRate <= 0 || channels <= 0 || channels > 32)
            throw new ArgumentOutOfRangeException(sampleRate <= 0 ? nameof(sampleRate) : nameof(channels));
        SampleRate = sampleRate;
        Channels = channels;
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _file.Write(Header(sampleRate, channels, 0));
    }

    public int SampleRate { get; }

    public int Channels { get; }

    public long Frames => _dataBytes / (4L * Channels);

    /// <summary>True when samples were left out because the file reached its size limit.</summary>
    public bool Full { get; private set; }

    public void Write(ReadOnlySpan<float> samples)
    {
        while (samples.Length > 0)
        {
            long room = (MaxDataBytes - _dataBytes) / (4L * Channels) * Channels;
            if (room <= 0)
            {
                Full = true;
                return;
            }

            int count = (int)Math.Min(Math.Min(samples.Length, _bytes.Length / 4), room);
            for (int i = 0; i < count; i++)
                BinaryPrimitives.WriteSingleLittleEndian(_bytes.AsSpan(i * 4), samples[i]);
            _file.Write(_bytes, 0, count * 4);
            _dataBytes += count * 4L;
            samples = samples[count..];
        }
    }

    public void WriteSilence(long frames)
    {
        Span<float> zeros = stackalloc float[256];
        zeros.Clear();
        long samples = frames * Channels;
        while (samples > 0 && !Full)
        {
            int count = (int)Math.Min(samples, zeros.Length);
            Write(zeros[..count]);
            samples -= count;
        }
    }

    /// <summary>Writes the sizes into the header and the data to the disk.</summary>
    public void Flush()
    {
        long end = _file.Position;
        _file.Position = 0;
        _file.Write(Header(SampleRate, Channels, _dataBytes));
        _file.Position = end;
        _file.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            Flush();
        }
        finally
        {
            _file.Dispose();
        }
    }

    private static byte[] Header(int sampleRate, int channels, long dataBytes)
    {
        var header = new byte[HeaderBytes];
        Span<byte> h = header;
        Encoding.ASCII.GetBytes("RIFF").CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)(HeaderBytes - 8 + dataBytes));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(h[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 18);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)(sampleRate * channels * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)(channels * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], 32);
        BinaryPrimitives.WriteUInt16LittleEndian(h[36..], 0);
        Encoding.ASCII.GetBytes("fact").CopyTo(h[38..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[42..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(h[46..], (uint)(dataBytes / (4L * channels)));
        Encoding.ASCII.GetBytes("data").CopyTo(h[50..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[54..], (uint)dataBytes);
        return header;
    }
}

/// <summary>What a WAV file of 32-bit float samples holds, for the checks of a recorded file.</summary>
public sealed record WavInfo(int SampleRate, int Channels, long Frames, float Peak, double Rms)
{
    public double Seconds => SampleRate > 0 ? Frames / (double)SampleRate : 0.0;

    /// <summary>Reads a file that <see cref="WavWriter"/> wrote, with the level of all samples.</summary>
    public static WavInfo Read(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < WavWriter.HeaderBytes || Encoding.ASCII.GetString(data, 0, 4) != "RIFF" || Encoding.ASCII.GetString(data, 8, 4) != "WAVE")
            throw new InvalidDataException("The file is not a WAV file.");
        int position = 12;
        int sampleRate = 0, channels = 0, format = 0, bits = 0;
        while (position + 8 <= data.Length)
        {
            string id = Encoding.ASCII.GetString(data, position, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4));
            int body = position + 8;
            if (id == "fmt ")
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 14));
            }
            else if (id == "data")
            {
                if (format != 3 || bits != 32 || channels <= 0)
                    throw new InvalidDataException("The WAV file does not hold 32-bit float samples.");
                int length = (int)Math.Min(size, data.Length - body) / 4 * 4;
                ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(data.AsSpan(body, length));
                float peak = 0f;
                double sum = 0.0;
                foreach (float sample in samples)
                {
                    peak = Math.Max(peak, Math.Abs(sample));
                    sum += (double)sample * sample;
                }

                return new WavInfo(sampleRate, channels, samples.Length / channels, peak,
                    samples.Length > 0 ? Math.Sqrt(sum / samples.Length) : 0.0);
            }

            position = body + (int)size + (int)(size & 1);
        }

        throw new InvalidDataException("The WAV file has no data chunk.");
    }
}
