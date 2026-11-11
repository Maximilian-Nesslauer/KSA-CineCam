using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using CineCam.Core;
using CineCam.Director.Replay;

namespace CineCam.Features.Replay;

internal static class ReplayBridge
{
    private const int Version = 1;
    private const int MaxBytes = 16384;
    private const int MaxResponseBytes = 1048576;
    private const int MaxPerPoll = 8;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) },
    };
    private static string? _nonce;
    private static FileStream? _leaseLock;
    private static long _lastPoll;
    private static DateTime _nextLease;
    private static string Root => Path.Combine(CineCamPaths.Root, "control");
    private static string Requests => Path.Combine(Root, "requests");
    private static string Responses => Path.Combine(Root, "responses");

    internal static void Poll()
    {
        if (!CineCamSettings.EnableLocalControl)
            return;
        long now = Stopwatch.GetTimestamp();
        if (_lastPoll != 0 && Stopwatch.GetElapsedTime(_lastPoll, now) < TimeSpan.FromMilliseconds(50))
            return;
        _lastPoll = now;
        try
        {
            if (_leaseLock == null)
            {
                Directory.CreateDirectory(Requests);
                Directory.CreateDirectory(Responses);
                _leaseLock = new FileStream(Path.Combine(Root, "session.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                _nonce = Guid.NewGuid().ToString("N");
            }
            if (DateTime.UtcNow >= _nextLease)
            {
                WriteAtomic(Path.Combine(Root, "session.json"), new Lease(Version, _nonce!, DateTime.UtcNow));
                _nextLease = DateTime.UtcNow.AddSeconds(1);
            }

            foreach (string path in Directory.EnumerateFiles(Requests, "*.json")
                         .Where(path => IsRequestId(Path.GetFileNameWithoutExtension(path))).Take(MaxPerPoll))
                Process(path);
            DateTime expired = DateTime.UtcNow.AddMinutes(-1);
            foreach (string path in Directory.EnumerateFiles(Responses, "*.json")
                         .Where(path => IsRequestId(Path.GetFileNameWithoutExtension(path))
                             && File.GetLastWriteTimeUtc(path) < expired).Take(MaxPerPoll))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A later frame retries after the file or directory becomes available.
        }
    }

    internal static void Shutdown()
    {
        try
        {
            string path = Path.Combine(Root, "session.json");
            if (_leaseLock != null && File.Exists(path))
            {
                Lease? lease = JsonSerializer.Deserialize<Lease>(File.ReadAllText(path), Json);
                if (lease?.Nonce == _nonce)
                    File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        finally
        {
            _leaseLock?.Dispose();
            _leaseLock = null;
            _nonce = null;
            _lastPoll = 0;
            _nextLease = default;
        }
    }

    private static bool IsRequestId(string id) => id.Length == 32 && Guid.TryParseExact(id, "N", out _)
        && id == id.ToLowerInvariant();

    private static void Process(string path)
    {
        string id = Path.GetFileNameWithoutExtension(path);
        Request? request;
        try
        {
            if (new FileInfo(path).Length > MaxBytes)
                throw new JsonException("Request exceeds the size limit.");
            request = JsonSerializer.Deserialize<Request>(File.ReadAllText(path), Json);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Reply(id, new ReplayResult(false, "invalidRequest", "The request could not be read."));
            return;
        }

        if (request == null || request.Version != Version || request.Id != id || request.Nonce != _nonce || request.Command == null)
        {
            Reply(id, new ReplayResult(false, "invalidRequest", "The request version, ID, session or command is invalid."));
            return;
        }
        ReplaySession.Submit(request.Command, result => Reply(id, result));
    }

    private static void Reply(string id, ReplayResult result)
    {
        try
        {
            var response = new Response(Version, id, _nonce!, result);
            if (System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(response, Json)) > MaxResponseBytes)
                response = new Response(Version, id, _nonce!, new ReplayResult(false, "response_too_large", "The response exceeds the size limit."));
            WriteAtomic(Path.Combine(Responses, id + ".json"), response);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private sealed record Lease(int Version, string Nonce, DateTime UpdatedUtc);
    private sealed record Request(int Version, string? Id, string? Nonce, ReplayCommand? Command);
    private sealed record Response(int Version, string Id, string Nonce, ReplayResult Result);
}
