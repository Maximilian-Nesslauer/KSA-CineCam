using System.Globalization;
using CineCam.Director;

int failures = 0;

void Check(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

void Expect(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

bool Near(double a, double b, double tolerance = 1e-9) => Math.Abs(a - b) <= tolerance;

bool Near3(V3 a, V3 b, double tolerance = 1e-9) => (a - b).Length <= tolerance;

ShotFileException ExpectInvalid(string json)
{
    try
    {
        ShotFileJson.Parse(json);
    }
    catch (ShotFileException ex)
    {
        return ex;
    }

    throw new InvalidOperationException("the file was accepted");
}

Dictionary<string, double> Events(params (string Name, double Time)[] seen) =>
    seen.ToDictionary(e => e.Name, e => e.Time);

Take ParseTake(string json, out ShotFile file)
{
    file = ShotFileJson.Parse(json).File;
    return file.DefaultTake;
}

Shot MakeShot(ShotType type, double duration = 10.0)
{
    var shot = new Shot { Type = type, Duration = duration, Target = Shot.DefaultTarget(type) };
    return shot;
}

Console.WriteLine(failures == 0 ? "All tests passed." : $"{failures} test(s) failed.");
return failures == 0 ? 0 : 1;

sealed class FlushCounter : StringWriterBase
{
    public FlushCounter(StringWriter inner) : base(inner) { }
}

abstract class StringWriterBase : TextWriter
{
    private readonly StringWriter _inner;

    protected StringWriterBase(StringWriter inner) => _inner = inner;

    public int Flushes { get; private set; }

    public override System.Text.Encoding Encoding => _inner.Encoding;

    public override void Write(char value) => _inner.Write(value);

    public override void Write(string? value) => _inner.Write(value);

    public override void Flush()
    {
        Flushes++;
        _inner.Flush();
    }
}
