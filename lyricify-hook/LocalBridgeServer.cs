using System.Net;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

internal sealed record BridgeSnapshot(BridgeState State, long UpdatedAt)
{
    public int ProgressMs => State.IsPlaying
        ? (int)Math.Min(State.DurationMs, State.ProgressMs + Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - UpdatedAt))
        : State.ProgressMs;
}

internal sealed record BridgeCommand(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Value = null,
    [property: JsonPropertyName("uris")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string[]? Uris = null,
    [property: JsonPropertyName("id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Id = null);

internal sealed record BridgeControlResult(bool Success, string? Error, bool[]? Values);

internal static class LocalBridgeServer
{
    private const int MaxBody = 16 * 1024;
    private const int MaxCommands = 32;
    // ponytail: XPUI background timers can slip to one update per minute; use an unthrottled signal if that exceeds 90 seconds.
    private const int MaxAgeMs = 90_000;
    private const string SpotifyOrigin = "https://xpui.app.spotify.com";
    private const string ControlResultPath = "/control-result";
    private static HttpListener? listener;
    private static BridgeSnapshot? current;
    private static readonly ConcurrentQueue<BridgeCommand> commands = new();
    private static readonly SemaphoreSlim commandSignal = new(0);
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeControlResult>> PendingRequests = new(StringComparer.Ordinal);

    internal static void Start(int port = 48711)
    {
        if (listener != null) throw new InvalidOperationException("Bridge is already running.");
        var instance = new HttpListener();
        instance.Prefixes.Add($"http://127.0.0.1:{port}/");
        try { instance.Start(); }
        catch
        {
            instance.Close();
            throw;
        }
        listener = instance;
        _ = AcceptAsync(instance, port);
        StartupHook.Log($"BRIDGE LISTENING on 127.0.0.1:{port}");
    }

    internal static void Stop()
    {
        Interlocked.Exchange(ref listener, null)?.Close();
        Volatile.Write(ref current, null);
        while (commands.TryDequeue(out _)) { }
        while (commandSignal.Wait(0)) { }
        foreach (var pending in PendingRequests.Values)
            pending.TrySetException(new InvalidOperationException("Spotify bridge stopped."));
        PendingRequests.Clear();
    }

    internal static Task<bool> EnqueueCommand(string name, int? value = null)
    {
        if (commands.Count >= MaxCommands) return Task.FromResult(false);
        commands.Enqueue(new BridgeCommand(name, value));
        commandSignal.Release();
        return Task.FromResult(true);
    }

    internal static Task<BridgeControlResult> EnqueueRequestAsync(
        string name, int? value = null, IReadOnlyList<string>? uris = null)
    {
        if (commands.Count >= MaxCommands)
            return Task.FromException<BridgeControlResult>(new InvalidOperationException("Spotify bridge command queue is full."));

        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<BridgeControlResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!PendingRequests.TryAdd(id, completion))
            return Task.FromException<BridgeControlResult>(new InvalidOperationException("Could not allocate bridge request."));

        commands.Enqueue(new BridgeCommand(name, value, uris?.ToArray(), id));
        commandSignal.Release();
        return AwaitRequestAsync(id, completion);
    }

    private static async Task<BridgeControlResult> AwaitRequestAsync(
        string id, TaskCompletionSource<BridgeControlResult> completion)
    {
        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            PendingRequests.TryRemove(id, out _);
        }
    }

    internal static async Task<List<bool>> CheckTracksAsync(IReadOnlyList<string> ids)
    {
        var uris = TrackUris(ids);
        var result = await EnqueueRequestAsync("library-check", uris: uris).ConfigureAwait(false);
        if (!result.Success || result.Values == null || result.Values.Length != ids.Count)
            throw new InvalidOperationException(result.Error ?? "Spotify library state was unavailable.");
        return result.Values.ToList();
    }

    internal static async Task<bool> MutateTracksAsync(string command, IReadOnlyList<string> ids)
    {
        var result = await EnqueueRequestAsync(command, uris: TrackUris(ids)).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(result.Error ?? "Spotify library operation failed.");
        return true;
    }

    private static string[] TrackUris(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0 || ids.Count > 40)
            throw new ArgumentException("Spotify library requests must contain 1 to 40 tracks.", nameof(ids));
        return ids.Select(id =>
        {
            if (id.StartsWith("spotify:track:", StringComparison.Ordinal)) return id;
            if (id.Length is >= 10 and <= 64 && id.All(char.IsLetterOrDigit)) return $"spotify:track:{id}";
            throw new ArgumentException("Invalid Spotify track ID.", nameof(ids));
        }).ToArray();
    }

    private static BridgeCommand[] TakeCommands()
    {
        var result = new List<BridgeCommand>(MaxCommands);
        while (result.Count < MaxCommands && commands.TryDequeue(out var command))
        {
            result.Add(command);
            commandSignal.Wait(0);
        }
        return result.ToArray();
    }

    internal static BridgeSnapshot? Read()
    {
        var snapshot = Volatile.Read(ref current);
        return snapshot != null && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - snapshot.UpdatedAt <= MaxAgeMs
            ? snapshot : null;
    }

    internal static int? NextRepeat()
    {
        var repeat = Read()?.State.Repeat;
        return repeat is >= 0 and <= 2 ? (repeat + 1) % 3 : null;
    }

    private static async Task AcceptAsync(HttpListener instance, int port)
    {
        try
        {
            while (instance.IsListening)
            {
                var context = await instance.GetContextAsync().ConfigureAwait(false);
                _ = HandleAsync(context, port);
            }
        }
        catch (HttpListenerException) when (!instance.IsListening) { }
        catch (ObjectDisposedException) { }
        catch (Exception error)
        {
            StartupHook.Log($"BRIDGE LISTENER FAILED: {error.GetType().Name}");
            Stop();
        }
    }

    private static async Task HandleAsync(HttpListenerContext context, int port)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            response.Headers["Cache-Control"] = "no-store";
            if (request.RemoteEndPoint == null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address)
                || request.Headers["Host"] != $"127.0.0.1:{port}")
            {
                response.StatusCode = 403;
                return;
            }

            var origin = request.Headers["Origin"];
            if (origin == SpotifyOrigin)
            {
                response.Headers["Access-Control-Allow-Origin"] = SpotifyOrigin;
                response.Headers["Vary"] = "Origin";
                response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                response.Headers["Access-Control-Expose-Headers"] = "X-Spotify-Local-Bridge-Long-Poll";
                response.Headers["Access-Control-Allow-Private-Network"] = "true";
            }

            if ((request.Url?.AbsolutePath == "/ingest" || request.Url?.AbsolutePath == ControlResultPath) && request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = origin == SpotifyOrigin ? 204 : 403;
                return;
            }
            if (request.Url?.AbsolutePath == "/ingest" && request.HttpMethod == "POST")
            {
                if (origin != SpotifyOrigin || request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
                {
                    response.StatusCode = 403;
                    return;
                }
                if (request.ContentLength64 > MaxBody)
                {
                    response.StatusCode = 413;
                    return;
                }

                var body = new byte[MaxBody + 1];
                var length = 0;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                while (length < body.Length)
                {
                    var count = await request.InputStream.ReadAsync(body.AsMemory(length), timeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    length += count;
                }
                if (length > MaxBody)
                {
                    response.StatusCode = 413;
                    return;
                }
                var state = JsonSerializer.Deserialize<BridgeState>(body.AsSpan(0, length));
                if (!Valid(state))
                {
                    response.StatusCode = 400;
                    return;
                }
                if (state!.Isrc == null)
                {
                    var cachedIsrc = await SpotifyMetadataCache.LookupAsync(state.TrackUri).ConfigureAwait(false);
                    if (cachedIsrc != null)
                    {
                        state.Isrc = cachedIsrc;
                        StartupHook.Log($"ISRC CACHE {state.TrackUri} {cachedIsrc}");
                    }
                }
                Volatile.Write(ref current, new BridgeSnapshot(state!, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                response.StatusCode = 204;
                return;
            }
            if (request.Url?.AbsolutePath == "/commands" && request.HttpMethod == "GET")
            {
                if (origin != SpotifyOrigin)
                {
                    response.StatusCode = 403;
                    return;
                }
                response.ContentType = "application/json; charset=utf-8";
                var pending = TakeCommands();
                if (pending.Length == 0)
                {
                    response.Headers["X-Spotify-Local-Bridge-Long-Poll"] = "1";
                    await commandSignal.WaitAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false);
                    pending = TakeCommands();
                }
                if (pending.Length > 0) StartupHook.Log($"COMMAND POLL {pending.Length}");
                await JsonSerializer.SerializeAsync(response.OutputStream, pending).ConfigureAwait(false);
                return;
            }
            if (request.Url?.AbsolutePath == ControlResultPath && request.HttpMethod == "POST")
            {
                if (origin != SpotifyOrigin || request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
                {
                    response.StatusCode = 403;
                    return;
                }
                using var document = await JsonDocument.ParseAsync(request.InputStream).ConfigureAwait(false);
                var root = document.RootElement;
                var requestId = root.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                var name = root.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                var success = root.TryGetProperty("success", out var successValue) && successValue.GetBoolean();
                var error = root.TryGetProperty("error", out var errorValue) ? errorValue.GetString() : null;
                bool[]? values = null;
                if (root.TryGetProperty("values", out var valuesElement) && valuesElement.ValueKind == JsonValueKind.Array)
                    values = valuesElement.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.True).ToArray();
                if (requestId != null && PendingRequests.TryRemove(requestId, out var completion))
                    completion.TrySetResult(new BridgeControlResult(success, error, values));
                StartupHook.Log($"CONTROL RESULT {name ?? "?"} id={requestId ?? "-"} success={success}{(string.IsNullOrWhiteSpace(error) ? "" : $" error={error}")}");
                response.StatusCode = 204;
                return;
            }
            if (request.Url?.AbsolutePath == "/state" && request.HttpMethod == "GET")
            {
                var snapshot = Read();
                if (snapshot == null)
                {
                    response.StatusCode = 204;
                    return;
                }
                response.ContentType = "application/json; charset=utf-8";
                var body = JsonSerializer.SerializeToNode(snapshot.State)!.AsObject();
                body["progress_ms"] = snapshot.ProgressMs;
                body["updated_at_ms"] = snapshot.UpdatedAt;
                await JsonSerializer.SerializeAsync(response.OutputStream, body).ConfigureAwait(false);
                return;
            }
            response.StatusCode = 404;
        }
        catch (OperationCanceledException)
        {
            response.StatusCode = 408;
        }
        catch (Exception error)
        {
            StartupHook.Log($"BRIDGE REQUEST ERROR: {error.GetType().Name}");
            try { response.StatusCode = 400; } catch (HttpListenerException) { }
        }
        finally
        {
            try { response.Close(); }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static bool Valid(BridgeState? state)
    {
        static bool Text(string? value, int max) => value != null && value.Length <= max;
        static bool Optional(string? value, int max) => value == null || Text(value, max);
        return state != null
            && Optional(state.TrackUri, 512)
            && Text(state.Title, 1000)
            && state.Artists is { Length: <= 20 } && state.Artists.All(name => Text(name, 300))
            && (state.ArtistUris == null || (state.ArtistUris.Length <= state.Artists.Length
                && state.ArtistUris.All(uri => Optional(uri, 512))))
            && Text(state.Album, 1000) && Optional(state.AlbumUri, 512)
            && (state.Isrc == null || Regex.IsMatch(state.Isrc, "^[A-Z]{2}[A-Z0-9]{3}\\d{7}$"))
            && Optional(state.ImageUrl, 1024)
            && state.DurationMs is >= 0 and <= 43_200_000
            && state.ProgressMs is >= 0 and <= 43_200_000
            && Optional(state.ContextUri, 512)
            && state.Repeat is >= 0 and <= 2;
    }
}

internal static class SpotifyMetadataCache
{
    private const int SearchRadius = 8 * 1024;
    private const int AssociationRadius = 512;
    private const int MaxFileSize = 8 * 1024 * 1024;
    private static readonly Regex IsrcField = new(
        @"(?i)isrc[\x00-\x1F\x80-\xFF]{0,64}([A-Z]{2}[A-Z0-9]{3}\d{7})",
        RegexOptions.Compiled);
    private static readonly Regex IsrcValue = new(
        @"(?i)([A-Z]{2}[A-Z0-9]{3}\d{7})",
        RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<string, Task<string?>> Results = new(StringComparer.Ordinal);

    internal static Task<string?> LookupAsync(string? trackUri)
    {
        const string prefix = "spotify:track:";
        if (trackUri?.StartsWith(prefix, StringComparison.Ordinal) != true) return Task.FromResult<string?>(null);
        var trackId = trackUri[prefix.Length..];
        if (trackId.Length is < 10 or > 64) return Task.FromResult<string?>(null);
        return Results.GetOrAdd(trackId, static id => Task.Run(() => Lookup(id)));
    }

    private static string? Lookup(string trackId)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spotify", "Users");
        if (!Directory.Exists(root)) return null;
        foreach (var user in Directory.EnumerateDirectories(root))
        {
            var cache = Path.Combine(user, "primary.ldb");
            if (!Directory.Exists(cache)) continue;
            var files = Directory.EnumerateFiles(cache)
                .Where(path => !Path.GetFileName(path).Equals("LOCK", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path));
            foreach (var file in files)
            {
                var isrc = FindInFile(file, trackId);
                if (isrc != null) return isrc;
            }
        }
        return null;
    }

    private static string? FindInFile(string path, string trackId)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= 0 || stream.Length > MaxFileSize) return null;
            var bytes = new byte[(int)stream.Length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = stream.Read(bytes, offset, bytes.Length - offset);
                if (count == 0) break;
                offset += count;
            }
            if (offset != bytes.Length) return null;
            var text = Encoding.Latin1.GetString(bytes);
            foreach (var needle in new[] { $"spotify:track:{trackId}", trackId })
            {
                var start = 0;
                while ((start = text.IndexOf(needle, start, StringComparison.Ordinal)) >= 0)
                {
                    var isrc = FindNearbyIsrc(text, start);
                    if (isrc != null) return isrc;
                    start += needle.Length;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static string? FindNearbyIsrc(string text, int trackOffset)
    {
        var from = Math.Max(0, trackOffset - SearchRadius);
        var length = Math.Min(text.Length - from, SearchRadius * 2);
        var window = text.Substring(from, length);
        Match? best = null;
        foreach (Match match in IsrcField.Matches(window))
        {
            var distance = Math.Abs(from + match.Index - trackOffset);
            if (distance <= AssociationRadius
                && (best == null || distance < Math.Abs(from + best.Index - trackOffset)))
                best = match;
        }
        if (best != null) return best.Groups[1].Value.ToUpperInvariant();

        foreach (Match match in IsrcValue.Matches(window))
        {
            var distance = Math.Abs(from + match.Index - trackOffset);
            if (distance <= AssociationRadius
                && (best == null || distance < Math.Abs(from + best.Index - trackOffset)))
                best = match;
        }
        return best?.Groups[1].Value.ToUpperInvariant();
    }
}
