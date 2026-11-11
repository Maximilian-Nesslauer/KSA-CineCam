using System.Globalization;
using System.Text;

namespace CineCam.Director;

/// <summary>
/// Writes the event log of a take as CSV. Numbers use the invariant culture and every line is
/// flushed at once, so the log is complete after a crash.
/// </summary>
public sealed class EventLogCsv : IDisposable
{
    public const string Header = "realSeconds,gameSeconds,missionSeconds,simSpeed,frame,kind,name,detail";

    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;

    public EventLogCsv(TextWriter writer, bool ownsWriter = false)
    {
        _writer = writer;
        _ownsWriter = ownsWriter;
        WriteLine(Header);
    }

    public static EventLogCsv Create(string path)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        return new EventLogCsv(new StreamWriter(stream, new UTF8Encoding(false)), ownsWriter: true);
    }

    /// <param name="frame">The frame number of an offline capture, or a negative value in real time.</param>
    public void Write(double realSeconds, double gameSeconds, double missionSeconds, double simSpeed,
        long frame, string kind, string name, string detail = "")
    {
        var line = new StringBuilder();
        line.Append(Seconds(realSeconds)).Append(',')
            .Append(Seconds(gameSeconds)).Append(',')
            .Append(Seconds(missionSeconds)).Append(',')
            .Append(simSpeed.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
            .Append(frame >= 0 ? frame.ToString(CultureInfo.InvariantCulture) : "").Append(',')
            .Append(Escape(kind)).Append(',')
            .Append(Escape(name)).Append(',')
            .Append(Escape(detail));
        WriteLine(line.ToString());
    }

    public static string Escape(string field)
    {
        if (field.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
            return field;
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    public void Dispose()
    {
        if (_ownsWriter)
            _writer.Dispose();
        else
            _writer.Flush();
    }

    private static string Seconds(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    private void WriteLine(string line)
    {
        _writer.Write(line);
        _writer.Write('\n');
        _writer.Flush();
    }
}
