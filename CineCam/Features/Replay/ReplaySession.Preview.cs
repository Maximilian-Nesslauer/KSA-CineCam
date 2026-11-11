using System.Diagnostics;
using CineCam.Core;
using CineCam.Director;
using CineCam.Director.Replay;
using CineCam.Features.Takes;
using KSA;

namespace CineCam.Features.Replay;

internal static partial class ReplaySession
{
    private sealed record PendingPreview(ScreenshotCollector Collector, long Started, bool InterfaceVisible,
        Action<ReplayResult>? Completion);

    private static PendingPreview? _preview;
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromHours(1);
    private static string PreviewRoot => Path.Combine(CineCamPaths.Root, "previews");

    // The caller reads a preview after the response, so a preview folder is removed only when it
    // is older than PreviewLifetime. Only folders with the generated name are touched.
    private static void CleanPreviews()
    {
        try
        {
            if (!Directory.Exists(PreviewRoot))
                return;
            DateTime limit = DateTime.UtcNow - PreviewLifetime;
            foreach (string folder in Directory.EnumerateDirectories(PreviewRoot))
            {
                string name = Path.GetFileName(folder);
                if (name.Length != 32 || !Guid.TryParseExact(name, "N", out _) || Directory.GetLastWriteTimeUtc(folder) >= limit)
                    continue;
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A later preview tries again.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cleanup is optional, the preview continues without it.
        }
    }

    private static void BeginPreview(Action<ReplayResult>? completion)
    {
        ReplayScene scene = NeedScene();
        if (!OfflineHooks.Available)
            throw new Refusal("preview_unavailable", "This game build does not support replay preview capture.");
        if (Program.Instance?.IsScreenshotCaptureActive != false)
            throw new Refusal("capture_busy", "Wait for the current game screenshot to finish.");

        CleanPreviews();
        string folder = Path.Combine(PreviewRoot, Guid.NewGuid().ToString("N"));
        var collector = new ScreenshotCollector(CineCamPaths.ScreenshotsFolder, folder);
        bool visible = Program.DrawUI;
        scene.SetPlaying(false);
        Program.DrawUI = false;
        try
        {
            if (!OfflineHooks.RequestPicture())
                throw new Refusal("capture_busy", "Wait for the current game screenshot to finish.");
            _preview = new PendingPreview(collector, Stopwatch.GetTimestamp(), visible, completion);
        }
        catch
        {
            Program.DrawUI = visible;
            throw;
        }
    }

    public static void UpdatePreview()
    {
        if (_preview is not { } pending)
            return;
        try
        {
            if (pending.Collector.TryCollect(1, out string? path) == CollectResult.Done)
                FinishPreview(true, "ok", "Preview saved.", path);
            else if (Stopwatch.GetElapsedTime(pending.Started) > TimeSpan.FromSeconds(6))
                FinishPreview(false, "preview_timeout", "KSA did not save the preview frame in time.");
        }
        catch (Exception ex)
        {
            Report(ex);
            FinishPreview(false, "preview_error", Status);
        }
    }

    private static void CancelPreview(string reason) => FinishPreview(false, "preview_cancelled", reason);

    private static void FinishPreview(bool ok, string code, string message, string? path = null)
    {
        if (_preview is not { } pending)
            return;
        _preview = null;
        Program.DrawUI = pending.InterfaceVisible;
        Complete(pending.Completion, ok, code, message, path);
    }
}
