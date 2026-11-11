namespace CineCam.Director;

// The state of the offline capture of one take. A picture is requested at the start of a
// frame and collected at the start of the next one, so the pictures stay in order and at
// most one picture is in flight.
public sealed class OfflineCapture
{
    public OfflineCapture(double fps, string outputFolder, string framesFolder, ScreenshotCollector collector)
    {
        Fps = fps;
        OutputFolder = outputFolder;
        FramesFolder = framesFolder;
        Collector = collector;
    }

    /// <summary>The step of a frame that makes a picture again. It is not zero because the game divides by the step in some places.</summary>
    public const double RetryStepSeconds = 1e-4;

    public double Fps { get; }

    public string OutputFolder { get; }

    public string FramesFolder { get; }

    public ScreenshotCollector Collector { get; }

    public double FrameStep => 1.0 / Fps;

    /// <summary>The number of pictures saved so far.</summary>
    public int Count { get; set; }

    public int FrameNumber => Count + 1;

    /// <summary>The time of the take. It moves only when a picture was saved.</summary>
    public double TakeSeconds => Count / Fps;

    public long BytesWritten { get; set; }

    /// <summary>True from the request of a picture until the picture is collected or lost.</summary>
    public bool Pending { get; set; }

    /// <summary>True when the frame of the pending request reached its end, so the game had the chance to record the picture.</summary>
    public bool Rendered { get; set; }

    /// <summary>The value of the end-of-frame counter when the picture was requested.</summary>
    public long PostRenderMark { get; set; }

    public int CarryFrames { get; set; }

    public bool Retry { get; set; }

    public int FailuresInRow { get; set; }

    public int PicturesLost { get; set; }

    /// <summary>The time of the game loop at the first frame, the zero of the time that the game sees.</summary>
    public double BaseTime { get; set; } = double.NaN;

    /// <summary>The time that all frames of the capture added to the time of the game loop.</summary>
    public double GameLoopSeconds { get; set; }

    /// <summary>The reason to end the take once the last picture is saved.</summary>
    public string? EndAfterPicture { get; set; }

    public bool WarnedSlowSim { get; set; }

    public double NextStep => Retry ? RetryStepSeconds : FrameStep;

    /// <summary>Moves the time of the game loop by a step and returns the time the game must see.</summary>
    public double Advance(double step, double currentPlayerTime)
    {
        if (double.IsNaN(BaseTime))
            BaseTime = currentPlayerTime;
        GameLoopSeconds += step;
        return BaseTime + GameLoopSeconds;
    }

    public void RecordSaved(int number)
    {
        Count = number;
        FailuresInRow = 0;
        Retry = false;
    }

    /// <summary>Returns true when the take must end, because the pictures were lost <paramref name="maxInRow"/> times in a row.</summary>
    public bool RecordLost(int maxInRow)
    {
        Retry = true;
        FailuresInRow++;
        PicturesLost++;
        return FailuresInRow >= maxInRow;
    }
}
