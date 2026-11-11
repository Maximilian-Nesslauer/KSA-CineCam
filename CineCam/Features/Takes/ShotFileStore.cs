using Brutal.Logging;
using CineCam.Core;
using CineCam.Director;

namespace CineCam.Features.Takes;

internal sealed record EditorCopy(ShotFile File, string Path);

internal static class ShotFileStore
{
    // The path that the window loaded or saved last, so the keys and the console run the file
    // that the player works on.
    public static string? PathOverride { get; set; }

    // Returns null when the window has no unsaved changes, and throws ShotFileException when the
    // unsaved document has errors.
    public static Func<EditorCopy?>? EditorCopyProvider { get; set; }

    // Gives the message of the window when the window could not save the file.
    public static Func<string, string, (Editing.CheckpointRecord Record, string Message)>? CheckpointRecorder { get; set; }

    // Writes into the window document when it has the take, else into the shot file on disk.
    public static bool RecordCheckpoint(string takeName, string saveName, out string result)
    {
        var recorded = CheckpointRecorder?.Invoke(takeName, saveName);
        switch (recorded?.Record)
        {
            case Editing.CheckpointRecord.Stored:
                result = "the file of the window";
                return true;
            case Editing.CheckpointRecord.NotSaved:
                result = "the window has it as an unsaved change, because the window could not save the file: " + recorded.Value.Message.TrimEnd('.');
                return false;
        }

        string path = CurrentPath;
        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            result = ex.Message;
            return false;
        }

        if (!Editing.CheckpointFile.Record(path, takeName, saveName, out string error))
        {
            result = error;
            return false;
        }

        result = path;
        return true;
    }

    // The Landmark that the shot preview uses as its site.
    public static string? PreviewSiteId { get; set; }

    // The anchor that the shot preview uses when no Landmark is named.
    public static WorldAnchor? PreviewAnchor { get; set; }

    public static string CurrentPath => string.IsNullOrWhiteSpace(PathOverride) ? CineCamPaths.ShotFile : PathOverride;

    // Writes the embedded default when the file is missing. Returns null and logs the reason when
    // the file cannot be used.
    public static ShotFile? Load(out string path)
    {
        path = CurrentPath;
        try
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, DefaultShotFile.Json);
                DefaultCategory.Log.Info($"[CineCam] Wrote the default shot file to {path}");
            }

            ShotFileReadResult result = ShotFileJson.Load(path);
            foreach (string warning in result.Warnings)
                DefaultCategory.Log.Warning($"[CineCam] Shot file: {warning}");
            return result.File;
        }
        catch (ShotFileException ex)
        {
            DefaultCategory.Log.Error($"[CineCam] The shot file {path} is not valid: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DefaultCategory.Log.Error($"[CineCam] The shot file {path} could not be read: {ex.Message}");
        }

        return null;
    }

    // Builds a director for every take, so a bad event name shows at load and not at the start of a flight.
    public static bool Check(ShotFile file)
    {
        bool ok = true;
        foreach (Take take in file.Takes)
        {
            try
            {
                _ = new TakeDirector(take, file);
            }
            catch (ArgumentException ex)
            {
                DefaultCategory.Log.Error($"[CineCam] Take \"{take.Name}\": {ex.Message}");
                ok = false;
            }
        }

        return ok;
    }
}
