using System.Globalization;
using Brutal.Logging;
using CineCam.Core;
using CineCam.Director.Sound;
using CineCam.Features.Sound;

namespace CineCam.Features.Takes;

internal static partial class TakeRunner
{
    #region Game sound

    // The game mixes its sound in real time, so only a real-time take records it. The pictures of an
    // offline capture come slower than real time and would not match the sound.
    private static void StartSound(ActiveTake take)
    {
        if (!take.IsRecording || !CineCamSettings.RecordTakeSound && !SoundOverride)
            return;
        if (take.Offline != null)
        {
            WriteRow(take, "info", "sound", "not recorded: an offline capture runs slower than real time, so the game sound would not match the pictures");
            return;
        }

        string path = Path.Combine(take.Folder, GameSoundRecorder.FileName);
        string? why;
        int rate = 0;
        try
        {
            why = GameSoundRecorder.Start(path, out rate);
        }
        catch (Exception ex)
        {
            why = ex.Message;
        }

        if (why != null)
        {
            WriteRow(take, "warning", "sound", "the game sound is not recorded: " + why);
            DefaultCategory.Log.Warning($"[CineCam] The game sound of the take is not recorded: {why}");
            return;
        }

        take.SoundFile = path;
        WriteRow(take, "info", "sound", string.Create(CultureInfo.InvariantCulture,
            $"recording; file={GameSoundRecorder.FileName}; rate={rate}; channels=2; format=32-bit float"));
    }

    // The mixer delivers the first block shortly after the start. Its take time is where the WAV
    // file starts on the timeline of the take. The file then runs in real time like a screen
    // recording, also through a pause for an edit, whose length the pause rows give.
    private static void TickSound(ActiveTake take)
    {
        if (take.SoundFile == null || take.SoundStartLogged || GameSoundRecorder.FirstSampleSeconds(take.ClockStartSeconds) is not double first)
            return;
        take.SoundStartLogged = true;
        WriteRow(take, "info", "soundStart", string.Create(CultureInfo.InvariantCulture,
            $"file={GameSoundRecorder.FileName}; firstSampleTakeSeconds={first:F4}"));
        DefaultCategory.Log.Info(FormattableString.Invariant(
            $"[CineCam] The game sound of take \"{take.Take.Name}\" records to {take.SoundFile}. Its first sample is at take time {first:F4} s."));
    }

    private static void EndSound(ActiveTake take)
    {
        if (take.SoundFile == null)
        {
            GameSoundRecorder.Stop();
            return;
        }

        TickSound(take);
        SoundCaptureResult? result = GameSoundRecorder.Stop();
        take.SoundFile = null;
        if (result == null)
            return;
        string detail = string.Create(CultureInfo.InvariantCulture,
            $"file={GameSoundRecorder.FileName}; seconds={result.Seconds:F3}; droppedSeconds={result.DroppedSeconds:F3}{(result.Full ? "; the file reached its size limit" : "")}{(result.Error != null ? "; error=" + result.Error : "")}");
        WriteRow(take, result.Error != null || result.DroppedFrames > 0 ? "warning" : "info", "soundEnd", detail);
        DefaultCategory.Log.Info($"[CineCam] The game sound of take \"{take.Take.Name}\" is saved: {detail}.");
    }

    /// <summary>Set by the self test, so its takes record the sound without a change of the settings file.</summary>
    public static bool SoundOverride { get; set; }

    #endregion
}
