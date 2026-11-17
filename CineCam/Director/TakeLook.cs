using System.Text.Json;

namespace CineCam.Director;

/// <summary>
/// The game sound of a take, as a share of the volumes of the game settings: 0 mutes the group, 1
/// keeps it. A null share keeps the volume of the game.
/// </summary>
public sealed class TakeSound
{
    public double? Music { get; set; }

    /// <summary>The sound effects of the game: engines, wind, alerts and the other sounds of the world.</summary>
    public double? Effects { get; set; }

    public Dictionary<string, JsonElement> Extras { get; } = new();

    public bool IsEmpty => Music == null && Effects == null && Extras.Count == 0;

    public TakeSound Clone()
    {
        var copy = new TakeSound { Music = Music, Effects = Effects };
        foreach (var pair in Extras)
            copy.Extras[pair.Key] = pair.Value;
        return copy;
    }
}

/// <summary>
/// The picture settings of a take. They replace the graphics settings of the player while the take
/// runs or renders, so the look of a take does not depend on them. A null field keeps the setting of
/// the player.
/// </summary>
public sealed class TakePicture
{
    public const double MinExposure = 0.01;
    public const double MaxExposure = 100.0;

    /// <summary>The exposure of the tonemapper, the value of the game setting Tonemap Exposure.</summary>
    public double? Exposure { get; set; }

    public bool? CloudAmbientLight { get; set; }

    /// <summary>Only off can be pinned for certain, because the game sets the quality of the cloud shadows at its start.</summary>
    public bool? CloudShadows { get; set; }

    /// <summary>The setting Reflections, which the game also names "Vessel Ambient Lighting".</summary>
    public bool? Reflections { get; set; }

    public Dictionary<string, JsonElement> Extras { get; } = new();

    public bool IsEmpty => Exposure == null && CloudAmbientLight == null && CloudShadows == null && Reflections == null && Extras.Count == 0;

    public TakePicture Clone()
    {
        var copy = new TakePicture
        {
            Exposure = Exposure,
            CloudAmbientLight = CloudAmbientLight,
            CloudShadows = CloudShadows,
            Reflections = Reflections,
        };
        foreach (var pair in Extras)
            copy.Extras[pair.Key] = pair.Value;
        return copy;
    }
}

/// <summary>How the offline capture and the render of a replay take each picture.</summary>
public enum CaptureGrab
{
    /// <summary>A copy of the finished window image, with everything that is drawn on the screen.</summary>
    Swapchain,

    /// <summary>The scene of the main view, drawn into its own image without the interface.</summary>
    Clean,
}
