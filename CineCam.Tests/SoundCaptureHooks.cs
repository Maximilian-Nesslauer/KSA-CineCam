namespace CineCam.Director.Sound;

// This part is compiled only by the game-free test project. The partial call is removed from the mod.
public sealed partial class SoundCapture
{
    internal volatile Action<long, long>? BeforeReadForTest;

    internal bool JoinWriterForTest(int milliseconds) => _thread.Join(milliseconds);

    partial void BeforeRead() => BeforeReadForTest?.Invoke(_ring.Read, _ring.Written);
}
