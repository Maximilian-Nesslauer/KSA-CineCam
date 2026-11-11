using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace CineCam.Director.Replay;

public sealed record RecordingHeader(int Version, string GameVersion, string SystemId, string BodyId, string Vehicle, double Duration);

/// <summary><see cref="Header"/> is null until the file was read, and <see cref="Error"/> says why a read failed.</summary>
public sealed record RecordingEntry(string Path, string Id, DateTime? Date, long Bytes, RecordingHeader? Header, string? Error);

/// <summary>
/// Streams once through the JSON without building the flight and takes the largest sample time as the length of the flight.
/// </summary>
public static class RecordingHeaderReader
{
    private enum Section { Other, Actors }

    private enum Wanted { None, Version, GameVersion, SystemId, BodyId, Name, Time }

    public static RecordingHeader Read(string path, CancellationToken cancel = default)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > ReplayFile.MaxBytes)
            throw new InvalidDataException("Replay is too large.");
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return Read(gzip, cancel);
    }

    public const int MaxBuffer = 16 * 1024 * 1024;

    public static RecordingHeader Read(Stream json, CancellationToken cancel = default)
    {
        var scan = new Scan();
        byte[] buffer = new byte[64 * 1024];
        int length = 0;
        long total = 0;
        var state = new JsonReaderState(new JsonReaderOptions { MaxDepth = 64 });
        bool final = false;
        try
        {
            while (!final)
            {
                cancel.ThrowIfCancellationRequested();
                if (length == buffer.Length)
                {
                    // The reader needs a whole token in the buffer, so a huge token of a hostile file
                    // must not grow it without end. A real recording has no token near this size.
                    if (buffer.Length >= MaxBuffer)
                        throw new InvalidDataException("Replay has a value that is too long.");
                    Array.Resize(ref buffer, Math.Min(buffer.Length * 2, MaxBuffer));
                }
                int read = json.Read(buffer, length, buffer.Length - length);
                total += read;
                if (total > ReplayFile.MaxBytes)
                    throw new InvalidDataException("Replay is too large.");
                length += read;
                final = read == 0;
                var reader = new Utf8JsonReader(buffer.AsSpan(0, length), final, state);
                while (reader.Read())
                    scan.Take(ref reader);
                state = reader.CurrentState;
                int used = (int)reader.BytesConsumed;
                Buffer.BlockCopy(buffer, used, buffer, 0, length - used);
                length -= used;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Replay JSON is invalid.", ex);
        }

        if (scan.Version is not (1 or ReplayFile.CurrentVersion))
            throw new InvalidDataException($"Unsupported replay version {scan.Version}.");
        if (scan.Vehicle == null)
            throw new InvalidDataException("Invalid replay: actor");
        return new RecordingHeader(scan.Version, scan.GameVersion ?? "", scan.SystemId ?? "", scan.BodyId ?? "", scan.Vehicle,
            double.IsFinite(scan.Duration) ? Math.Max(0.0, scan.Duration) : 0.0);
    }

    // The envelope is {"Version": n, "Flight": {...}}, so the fields of the envelope have depth 1,
    // the fields of the flight depth 2, and the fields of an actor depth 4.
    private sealed class Scan
    {
        public int Version = -1;
        public string? GameVersion, SystemId, BodyId, Vehicle;
        public double Duration = double.NegativeInfinity;
        private Section _section;
        private Wanted _wanted;

        public void Take(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _wanted = Wanted.None;
                int depth = reader.CurrentDepth;
                if (depth == 1)
                {
                    if (reader.ValueTextEquals("Version"u8))
                        _wanted = Wanted.Version;
                }
                else if (depth == 2)
                {
                    _section = reader.ValueTextEquals("Actors"u8) ? Section.Actors : Section.Other;
                    if (reader.ValueTextEquals("GameVersion"u8))
                        _wanted = Wanted.GameVersion;
                    else if (reader.ValueTextEquals("SystemId"u8))
                        _wanted = Wanted.SystemId;
                    else if (reader.ValueTextEquals("BodyId"u8))
                        _wanted = Wanted.BodyId;
                }
                else if (_section == Section.Actors)
                {
                    if (depth == 4 && Vehicle == null && reader.ValueTextEquals("Name"u8))
                        _wanted = Wanted.Name;
                    else if (reader.ValueTextEquals("Time"u8))
                        _wanted = Wanted.Time;
                }

                return;
            }

            Wanted wanted = _wanted;
            _wanted = Wanted.None;
            switch (wanted)
            {
                case Wanted.Version when reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int version):
                    Version = version;
                    break;
                case Wanted.GameVersion when reader.TokenType == JsonTokenType.String:
                    GameVersion = reader.GetString();
                    break;
                case Wanted.SystemId when reader.TokenType == JsonTokenType.String:
                    SystemId = reader.GetString();
                    break;
                case Wanted.BodyId when reader.TokenType == JsonTokenType.String:
                    BodyId = reader.GetString();
                    break;
                case Wanted.Name when reader.TokenType == JsonTokenType.String:
                    Vehicle = reader.GetString();
                    break;
                case Wanted.Time when reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double time):
                    if (time > Duration)
                        Duration = time;
                    break;
            }
        }
    }
}

public static class RecordingList
{
    public const string Extension = ".ccreplay";

    /// <summary>The date in the name that the recorder gives a file ("Flight 2026-11-11 15-46-52 ..."), or null for another name.</summary>
    public static DateTime? DateFromId(string id)
    {
        const string Prefix = "Flight ";
        const string Format = "yyyy-MM-dd HH-mm-ss";
        if (!id.StartsWith(Prefix, StringComparison.Ordinal) || id.Length < Prefix.Length + Format.Length)
            return null;
        return DateTime.TryParseExact(id.AsSpan(Prefix.Length, Format.Length), Format, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTime date) ? date : null;
    }

    public static List<RecordingEntry> NewestFirst(IEnumerable<RecordingEntry> entries) =>
        entries.OrderByDescending(e => e.Date ?? DateTime.MinValue).ThenByDescending(e => e.Id, StringComparer.Ordinal).ToList();

    public static string DateText(DateTime? date) =>
        date is DateTime d ? d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "-";

    public static string SizeText(long bytes)
    {
        if (bytes < 1024)
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        if (bytes < 1024 * 1024)
            return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    public static string LengthText(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0.0)
            return "-";
        long total = (long)Math.Round(seconds);
        return total < 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{total / 60}:{total % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}");
    }

    public static string[] Cells(RecordingEntry entry) =>
    [
        DateText(entry.Date),
        entry.Header?.Vehicle ?? (entry.Error != null ? "cannot be read" : "reading..."),
        entry.Header is { } header ? LengthText(header.Duration) : "-",
        SizeText(entry.Bytes),
    ];

    public static string DisplayName(RecordingEntry entry) =>
        entry.Header is { } header ? $"{header.Vehicle}, {DateText(entry.Date)}" : entry.Date.HasValue ? DateText(entry.Date) : entry.Id;

    /// <summary>Guards a delete so that it can only remove a recording file that the list showed, directly in the replay folder.</summary>
    public static bool IsDeletable(string folder, string path, IEnumerable<string> listed)
    {
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(path))
            return false;
        string full, root;
        try
        {
            full = System.IO.Path.GetFullPath(path);
            root = System.IO.Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        string? parent = System.IO.Path.GetDirectoryName(full);
        return parent != null
            && string.Equals(parent.TrimEnd(System.IO.Path.DirectorySeparatorChar), root.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            && string.Equals(System.IO.Path.GetExtension(full), Extension, StringComparison.OrdinalIgnoreCase)
            && listed.Any(item => string.Equals(item, path, StringComparison.Ordinal));
    }
}
