namespace CineCam.Director;

/// <summary>
/// The share of the volume of the game settings that each channel group of the game plays at, from 0
/// (muted) to 1 (as set). The master share acts on every sound, also the interface.
/// </summary>
public readonly record struct SoundLevels(double Master, double Music, double Effects)
{
    public static readonly SoundLevels Full = new(1.0, 1.0, 1.0);

    public static readonly SoundLevels Muted = new(0.0, 0.0, 0.0);

    public bool IsFull => Master >= 1.0 && Music >= 1.0 && Effects >= 1.0;

    /// <summary>The quieter share of each group, so a mute of one owner always wins.</summary>
    public SoundLevels Min(SoundLevels other) =>
        new(Math.Min(Master, other.Master), Math.Min(Music, other.Music), Math.Min(Effects, other.Effects));

    public static SoundLevels Of(TakeSound? sound) => sound == null
        ? Full
        : new SoundLevels(1.0, Clamp(sound.Music), Clamp(sound.Effects));

    private static double Clamp(double? share) => share is double value && double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 1.0;
}
