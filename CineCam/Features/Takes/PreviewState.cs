using CineCam.Director;

namespace CineCam.Features.Takes;

// Shot is the shot object of the editor itself, not a copy, so an edit shows at once.
internal sealed class PreviewState
{
    public PreviewState(Shot shot, double raw, bool playing)
    {
        Shot = shot;
        Raw = raw;
        Playing = playing;
    }

    public Shot Shot { get; }

    /// <summary>Progress of the shot before the ease, 0 to 1.</summary>
    public double Raw { get; set; }

    public bool Playing { get; set; }

    public bool Loop { get; set; }

    /// <summary>A problem of the last pose, for example a part that the vehicle does not have.</summary>
    public string? Warning { get; set; }

    /// <summary>The take time of the last update, in seconds.</summary>
    public double LastSeconds { get; set; } = double.NaN;
}
