using Brutal.Logging;
using CineCam.Director.Replay;

namespace CineCam.Features.Replay;

// The facts of each recording come from a worker thread, one file at a time, and stay cached by
// path, size and write time, so the frame never reads a recording.
internal static partial class ReplaySession
{
    private sealed record CachedHeader(DateTime WriteTime, long Bytes, RecordingHeader? Header, string? Error);

    private static readonly Dictionary<string, CachedHeader> Headers = new(StringComparer.Ordinal);
    private static IReadOnlyList<string>? _entriesFiles;
    private static List<RecordingEntry> _entries = [];
    private static Dictionary<string, (DateTime WriteTime, long Bytes)> _stats = new(StringComparer.Ordinal);
    private static (string Path, DateTime WriteTime, long Bytes, Task<RecordingHeader> Task, CancellationTokenSource Cancel)? _reading;
    private static int _entriesVersion;

    /// <summary>The recordings, newest first. The list is made again when a file was read or the folder changed.</summary>
    public static IReadOnlyList<RecordingEntry> Recordings()
    {
        IReadOnlyList<string> files = ListFiles();
        bool changed = false;
        if (!ReferenceEquals(files, _entriesFiles))
        {
            _entriesFiles = files;
            _stats = new Dictionary<string, (DateTime, long)>(StringComparer.Ordinal);
            foreach (string path in files)
            {
                try
                {
                    var info = new FileInfo(path);
                    _stats[path] = (info.LastWriteTimeUtc, info.Length);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _stats[path] = (DateTime.MinValue, 0);
                }
            }

            foreach (string gone in Headers.Keys.Where(path => !_stats.ContainsKey(path)).ToList())
                Headers.Remove(gone);
            changed = true;
        }

        changed |= CollectHeader();
        if (changed)
        {
            var entries = new List<RecordingEntry>(files.Count);
            foreach (string path in files)
            {
                (DateTime write, long bytes) = _stats.TryGetValue(path, out var stat) ? stat : (DateTime.MinValue, 0L);
                string id = Path.GetFileNameWithoutExtension(path);
                CachedHeader? cached = Headers.TryGetValue(path, out CachedHeader? c) && c.WriteTime == write && c.Bytes == bytes ? c : null;
                DateTime? date = RecordingList.DateFromId(id) ?? (write == DateTime.MinValue ? null : write.ToLocalTime());
                entries.Add(new RecordingEntry(path, id, date, bytes, cached?.Header, cached?.Error));
            }

            _entries = RecordingList.NewestFirst(entries);
            _entriesVersion++;
        }

        StartNextHeader();
        return _entries;
    }

    /// <summary>Counts up each time the list of recordings changes, so a view can make its rows again.</summary>
    public static int RecordingsVersion => _entriesVersion;

    // Returns true when the cache changed.
    private static bool CollectHeader()
    {
        if (_reading is not { } reading || !reading.Task.IsCompleted)
            return false;
        _reading = null;
        reading.Cancel.Dispose();
        if (reading.Task.IsCanceled)
            return false;
        if (reading.Task.Exception is { } error)
        {
            Exception cause = error.GetBaseException();
            Headers[reading.Path] = new CachedHeader(reading.WriteTime, reading.Bytes, null, cause.Message);
            DefaultCategory.Log.Warning($"[CineCam] The recording {Path.GetFileName(reading.Path)} could not be read for the list: {cause.Message}");
        }
        else
        {
            Headers[reading.Path] = new CachedHeader(reading.WriteTime, reading.Bytes, reading.Task.Result, null);
        }

        return true;
    }

    // Starts the read of the newest file that has no fresh entry. An open replay leaves the worker
    // idle, because the replay needs the disk and the processor.
    private static void StartNextHeader()
    {
        if (_reading != null || IsPlayingBack)
            return;
        foreach (RecordingEntry entry in _entries)
        {
            string path = entry.Path;
            if (!_stats.TryGetValue(path, out var stat))
                continue;
            if (Headers.TryGetValue(path, out CachedHeader? cached) && cached.WriteTime == stat.WriteTime && cached.Bytes == stat.Bytes)
                continue;
            var cancel = new CancellationTokenSource();
            CancellationToken token = cancel.Token;
            _reading = (path, stat.WriteTime, stat.Bytes, Task.Run(() => RecordingHeaderReader.Read(path, token), token), cancel);
            return;
        }
    }

    private static void ShutdownLibrary()
    {
        if (_reading is { } reading)
        {
            reading.Cancel.Cancel();
            _reading = null;
        }

        Headers.Clear();
        _entriesFiles = null;
        _entries = [];
        _stats = new Dictionary<string, (DateTime, long)>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Deletes a recording of the list and the marker of its import. Only a file that the list shows
    /// and that lies in the replay folder is deleted. Returns why it was not deleted, or null.
    /// </summary>
    public static string? DeleteRecording(string path)
    {
        if (IsRecording)
            return "Stop the recording first.";
        if (IsPlayingBack)
            return "Exit the replay first.";
        if (!RecordingList.IsDeletable(Folder, path, ListFiles()))
            return "This file is not in the list of recordings.";
        string id = Path.GetFileNameWithoutExtension(path);
        try
        {
            if (_reading is { } reading && string.Equals(reading.Path, path, StringComparison.Ordinal))
                reading.Cancel.Cancel();

            File.Delete(path);
            string marker = ImportMarker(id);
            if (File.Exists(marker))
                File.Delete(marker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The recording could not be deleted: {ex.Message}";
        }
        finally
        {
            _files = null;
        }

        Status = $"Deleted the recording {id}.";
        return null;
    }
}
