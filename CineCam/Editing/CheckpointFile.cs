using System.Text;
using CineCam.Director;

namespace CineCam.Editing;

/// <summary>For a caller that has no open editor session.</summary>
public static class CheckpointFile
{
    /// <summary>The previous file stays as a .bak copy, because the writer does not keep the comments of a file.</summary>
    public static bool Record(string path, string takeName, string saveName, out string error)
    {
        error = "";
        try
        {
            ShotFile file = ShotFileJson.Load(path).File;
            Take? take = file.FindTake(takeName);
            if (take == null)
            {
                error = $"the file has no take \"{takeName}\"";
                return false;
            }

            take.Checkpoint = saveName;
            File.Copy(path, path + ".bak", overwrite: true);
            string temp = path + ".tmp";
            File.WriteAllText(temp, ShotFileJson.Write(file), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is ShotFileException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }
}
