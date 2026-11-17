using CineCam.Director;
using CineCam.Director.Replay;

namespace Brutal.Logging
{
    public static class DefaultCategory
    {
        public static Sink Log { get; } = new();
        public sealed class Sink { public void Error(string text) => Console.Error.WriteLine(text); }
    }
}
namespace KSA
{
    public static class Universe { public static double GetElapsedSeconds() => 1000; }
}
namespace CineCam.Features.Takes
{
    internal static class TakeRunner { public static bool HidesInterface => false; }
    internal sealed record ActiveTake(Take Take, bool DryRun, bool HidUi);
}
namespace CineCam.Features.Replay
{
    internal sealed record ReplayScene(ReplayTakePlayer Player)
    {
        public ReplayFlight Flight => Player.World.Flight;
    }
    internal static class ReplaySession
    {
        public static ReplayScene? Scene { get; set; }
        public static string? TakeName { get; set; }
        public static bool IsRendering => false;
        public static bool HidesInterface => false;
    }
}
