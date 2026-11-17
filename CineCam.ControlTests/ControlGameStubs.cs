using System.Reflection;

// These stand-ins exercise the linked control code without loading KSA or a renderer.
namespace KSA
{
    internal sealed class GameSettings
    {
        public enum CloudShadows { Off, High }
        public sealed class GraphicsSettings
        {
            public float TonemapExposure = 1;
            public bool CloudAmbientLight = true;
            public CloudShadows CloudShadows = CloudShadows.High;
            public bool Reflections = true;
        }
        public sealed class InterfaceSettings { public bool FrameStatistics = true; }
        public static GameSettings Current = new();
        public GraphicsSettings Graphics = new();
        public InterfaceSettings Interface = new();
        public bool FailSave;
        public string SavedJson = "";
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void Save()
        {
            SavedJson = System.Text.Json.JsonSerializer.Serialize(Graphics, new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
            if (FailSave) throw new IOException("test save failure");
        }
    }

    internal sealed class Program
    {
        private readonly ScreenshotCapture _screenshotCapture = new();
        public static Program Instance = new();
        public static bool DrawUI = true;
        public static bool IsLoaded = true;
        public static object? Editor = null;
        public bool IsScreenshotCaptureActive => _screenshotCapture.IsActive;
        public ScreenshotCapture Capture => _screenshotCapture;
        private static void Screenshot(int scale, string flags) => Instance.Capture.Request(scale, flags);
    }

    internal sealed class ScreenshotCapture
    {
        private string _state = "Idle";
        private bool _copyRecordedThisFrame;
        private bool _savedDrawUi;
        public bool IsActive => _state != "Idle";
        public int Scale { get; private set; }
        public string Flags { get; private set; } = "";
        public void Request(int scale, string flags)
        {
            Scale = scale;
            Flags = flags;
            _state = "PendingHiRes";
        }
        public void Prepare()
        {
            _savedDrawUi = Program.DrawUI;
            Program.DrawUI = false;
            _state = "Warming";
        }
        public void Ready() { _state = "CaptureFrame"; _copyRecordedThisFrame = true; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void OnPostRender()
        {
            if (_state != "CaptureFrame" || !_copyRecordedThisFrame)
                return;
            Abort();
        }
        public void Abort() { Program.DrawUI = _savedDrawUi; _state = "Idle"; }
    }
}

namespace Brutal.Logging
{
    internal static class DefaultCategory { public static TestLog Log { get; } = new(); }
    internal sealed class TestLog
    {
        public void Info(string text) { }
        public void Warning(string text) { }
    }
}

namespace CineCam.Core
{
    internal static class GameReflection
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        public static readonly MethodInfo? Program_Screenshot = typeof(KSA.Program).GetMethod("Screenshot", BindingFlags.NonPublic | BindingFlags.Static);
        public static readonly FieldInfo? Program_ScreenshotCapture = typeof(KSA.Program).GetField("_screenshotCapture", PrivateInstance);
        public static readonly MethodInfo? Program_SetTonemapperSettings = null;
        public static readonly FieldInfo? ScreenshotCapture_State = typeof(KSA.ScreenshotCapture).GetField("_state", PrivateInstance);
        public static readonly FieldInfo? ScreenshotCapture_CopyRecorded = typeof(KSA.ScreenshotCapture).GetField("_copyRecordedThisFrame", PrivateInstance);
    }
    internal static class LogHelper
    {
        public static void WarnOnce(string key, string text) { }
        public static void ErrorOnce(string key, string text) { }
    }
    internal static class CineCamSettings
    {
        public const int StillScale = 2, MinStillScale = 1, MaxStillScale = 8;
    }
    internal static class CineCamPaths
    {
        public static string Root = "";
        public static string StillsFolder => Path.Combine(Root, "stills");
        public static string ScreenshotsFolder => Path.Combine(Root, "screenshots");
    }
}

namespace CineCam.Features.Replay
{
    internal static class ReplaySession
    {
        public static bool IsPlaying { get; private set; }
        public static bool IsRendering => false;
        public static void SetPlaying(bool playing) => IsPlaying = playing;
    }
}

namespace CineCam.Features.Takes
{
    internal static class TakeRunner
    {
        public static StubTake? Current => null;
    }
    internal sealed class StubTake { public object? Offline => null; }
}
