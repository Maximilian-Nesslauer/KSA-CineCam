using System.Globalization;
using System.Text;

namespace CineCam.Director;

/// <summary>
/// The files of an offline capture that need no game: the frame names, the helper scripts and the
/// code that takes a finished picture of the game out of its screenshot folder.
/// </summary>
public static class OfflineOutput
{
    public const string FramesFolderName = "frames";
    public const string VideoFileName = "video.mp4";

    public static string FrameName(int number) =>
        number.ToString("D6", CultureInfo.InvariantCulture) + ".png";

    /// <summary>
    /// The output folder of a capture. An empty or missing folder in the file means the take folder.
    /// A relative folder is relative to the take folder.
    /// </summary>
    public static string ResolveFolder(string takeFolder, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return takeFolder;
        string trimmed = folder.Trim();
        return Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(takeFolder, trimmed);
    }

    public static string FormatFps(double fps) => fps.ToString("0.###", CultureInfo.InvariantCulture);

    // The percent sign of the picture pattern is doubled in a Windows batch file.
    public static string MakeVideoCmd(double fps) =>
        "@echo off\r\n" +
        "cd /d \"%~dp0\"\r\n" +
        $"ffmpeg -framerate {FormatFps(fps)} -start_number 1 -i {FramesFolderName}\\%%06d.png -c:v libx264 -pix_fmt yuv420p -crf 16 {VideoFileName}\r\n";

    public static string MakeVideoSh(double fps) =>
        "#!/bin/sh\n" +
        "cd \"$(dirname \"$0\")\" || exit 1\n" +
        $"ffmpeg -framerate {FormatFps(fps)} -start_number 1 -i {FramesFolderName}/%06d.png -c:v libx264 -pix_fmt yuv420p -crf 16 {VideoFileName}\n";

    public static string Readme(string takeName, double fps) =>
        $"Offline capture of the take \"{takeName}\" at {FormatFps(fps)} pictures per second. " +
        $"The pictures are in the folder {FramesFolderName} as 000001.png, 000002.png and so on, one per frame of the video. " +
        "Run make-video.cmd (Windows) or make-video.sh with ffmpeg on the path to make " + VideoFileName + ". " +
        "The game makes no sound in this mode, so add the music in the video editor. " +
        "The column frame of events.csv gives the picture number of each event.\n";

    /// <summary>The scripts use a path relative to their own folder, so the folder can move after the capture.</summary>
    public static void WriteHelpers(string outputFolder, string takeFolder, string takeName, double fps)
    {
        Directory.CreateDirectory(outputFolder);
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(Path.Combine(outputFolder, "make-video.cmd"), MakeVideoCmd(fps), utf8);
        File.WriteAllText(Path.Combine(outputFolder, "make-video.sh"), MakeVideoSh(fps), utf8);
        Directory.CreateDirectory(takeFolder);
        File.WriteAllText(Path.Combine(takeFolder, "README.txt"), Readme(takeName, fps), utf8);
    }

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// The game writes its picture on a worker thread and keeps the file open until it is done, so a
    /// file that opens with no sharing at all is finished.
    /// </summary>
    public static bool IsFinishedPng(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            if (stream.Length < PngSignature.Length)
                return false;
            Span<byte> head = stackalloc byte[8];
            stream.ReadExactly(head);
            return head.SequenceEqual(PngSignature);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public enum CollectResult
{
    Done,

    Waiting,
}

/// <summary>
/// Takes the pictures that the game saves into its screenshot folder and moves each one to a numbered
/// file. The game names a picture by the second, so the caller must collect one picture before it asks
/// for the next one.
/// </summary>
public sealed class ScreenshotCollector
{
    private readonly string _sourceFolder;
    private readonly string _framesFolder;
    private readonly Dictionary<string, FileSignature> _known = new(StringComparer.OrdinalIgnoreCase);

    // A file counts as the same file only with the same size and write time. The game names a
    // picture by the second, so a new picture can come with the name of an old one.
    private readonly record struct FileSignature(DateTime WriteTimeUtc, long Length);

    public ScreenshotCollector(string sourceFolder, string framesFolder)
    {
        _sourceFolder = sourceFolder;
        _framesFolder = framesFolder;
        Directory.CreateDirectory(framesFolder);
        Snapshot();
    }

    /// <summary>Remembers the pictures that are in the screenshot folder now, so they are never taken.</summary>
    public void Snapshot()
    {
        _known.Clear();
        foreach (var entry in Entries())
            _known[entry.Name] = entry.Signature;
    }

    /// <summary>Other new files, such as a screenshot of the player, are ignored from then on.</summary>
    public CollectResult TryCollect(int frameNumber, out string? movedTo) =>
        TryMove(Path.Combine(_framesFolder, OfflineOutput.FrameName(frameNumber)), out movedTo);

    /// <summary>Waits for a new finished picture and deletes it, for a picture that the game saves after the capture ended.</summary>
    public bool Discard(TimeSpan timeout, Func<bool>? giveUp = null)
    {
        string target = Path.Combine(_framesFolder, "discarded-" + Guid.NewGuid().ToString("N") + ".png");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (TryMove(target, out _) == CollectResult.Done)
            {
                File.Delete(target);
                return true;
            }
            if (watch.Elapsed >= timeout || giveUp?.Invoke() == true)
                return false;
            Thread.Sleep(2);
        }
    }

    private CollectResult TryMove(string target, out string? movedTo)
    {
        movedTo = null;
        var present = Entries();
        foreach (string name in _known.Keys.ToList())
        {
            if (!present.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
                _known.Remove(name);
        }

        string? best = null;
        DateTime bestTime = DateTime.MinValue;
        var fresh = new List<(string Name, FileSignature Signature)>();
        foreach (var entry in present)
        {
            if (_known.TryGetValue(entry.Name, out FileSignature old) && old == entry.Signature)
                continue;
            fresh.Add(entry);
            if (best == null || entry.Signature.WriteTimeUtc >= bestTime)
            {
                best = entry.Name;
                bestTime = entry.Signature.WriteTimeUtc;
            }
        }

        if (best == null)
            return CollectResult.Waiting;

        string source = Path.Combine(_sourceFolder, best);
        if (!OfflineOutput.IsFinishedPng(source))
            return CollectResult.Waiting;

        File.Move(source, target, overwrite: false);
        // The other new files belong to someone else. A file that is still written has no final
        // signature yet, so it is judged again later.
        foreach (var entry in fresh)
        {
            if (entry.Name != best && OfflineOutput.IsFinishedPng(Path.Combine(_sourceFolder, entry.Name)))
                _known[entry.Name] = entry.Signature;
        }

        movedTo = target;
        return CollectResult.Done;
    }

    /// <summary>The give-up function can end the wait early, for example when the game reports that it saved no picture.</summary>
    public CollectResult Wait(int frameNumber, TimeSpan timeout, Func<bool>? giveUp = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (TryCollect(frameNumber, out _) == CollectResult.Done)
                return CollectResult.Done;
            if (watch.Elapsed >= timeout || giveUp?.Invoke() == true)
                return CollectResult.Waiting;
            Thread.Sleep(2);
        }
    }

    private List<(string Name, FileSignature Signature)> Entries()
    {
        var result = new List<(string, FileSignature)>();
        if (!Directory.Exists(_sourceFolder))
            return result;
        foreach (string path in Directory.EnumerateFiles(_sourceFolder, "ksa_*.png"))
        {
            try
            {
                var info = new FileInfo(path);
                result.Add((info.Name, new FileSignature(info.LastWriteTimeUtc, info.Length)));
            }
            catch (IOException)
            {
                // The file went away between the listing and the read.
            }
        }

        return result;
    }
}
