using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Serialization;
using HarmonyLib;
using SpotifyAPI.Web;

public static class StartupHook
{
    private static readonly object LogGate = new();

    public static void Initialize()
    {
        var directory = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (name.Name != "0Harmony") return null;
            var path = Path.Combine(directory, "0Harmony.dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };

        // ponytail: Lyricify 4.4.0's protected initializer crashed when patched immediately; revisit after a client update.
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000).ConfigureAwait(false);
            try
            {
                LocalBridgeServer.Start();
                PlaybackPatch.Install();
            }
            catch (Exception error)
            {
                LocalBridgeServer.Stop();
                Log($"PATCH FAILED: {error.GetType().Name}: {error.Message}");
            }
        });
    }

    internal static void Log(string message)
    {
        var path = Environment.GetEnvironmentVariable("LYRICIFY_HOOK_LOG");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            lock (LogGate) File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class PlaybackPatch
{
    public static void Install()
    {
        var harmony = new Harmony("app.lyricify.spotify-local-bridge.playback");
        var prefix = new HarmonyMethod(typeof(PlaybackPatch).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static));
        var methods = typeof(PlayerClient).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == nameof(PlayerClient.GetCurrentPlayback)).ToArray();
        if (methods.Length != 2) throw new InvalidOperationException($"Expected 2 playback overloads; found {methods.Length}");
        foreach (var method in methods) harmony.Patch(method, prefix: prefix);
        var controlPrefix = new HarmonyMethod(typeof(PlaybackPatch).GetMethod(nameof(ControlPrefix), BindingFlags.NonPublic | BindingFlags.Static));
        PatchControl(harmony, controlPrefix, nameof(PlayerClient.PausePlayback), 1);
        PatchControl(harmony, controlPrefix, nameof(PlayerClient.ResumePlayback), 1);
        PatchControl(harmony, controlPrefix, nameof(PlayerClient.SkipNext), 1);
        PatchControl(harmony, controlPrefix, nameof(PlayerClient.SkipPrevious), 1);
        PatchControl(harmony, controlPrefix, nameof(PlayerClient.SeekTo), 2);
        StartupHook.Log("PATCHED playback reads and controls (2 reads, 5 controls)");
    }

    private static void PatchControl(Harmony harmony, HarmonyMethod prefix, string name, int parameterCount)
    {
        var method = typeof(PlayerClient).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SingleOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == parameterCount);
        if (method == null) throw new InvalidOperationException($"Expected {name} overload with {parameterCount} parameters");
        harmony.Patch(method, prefix: prefix);
        StartupHook.Log($"PATCHED {name} control");
    }

    private static bool Prefix(ref Task<CurrentlyPlayingContext> __result)
    {
        __result = ReadLocalPlayback();
        return false;
    }

    private static bool ControlPrefix(MethodBase __originalMethod, object[] __args, ref Task<bool> __result)
    {
        var command = __originalMethod.Name switch
        {
            nameof(PlayerClient.PausePlayback) => "pause",
            nameof(PlayerClient.ResumePlayback) => "resume",
            nameof(PlayerClient.SkipNext) => "next",
            nameof(PlayerClient.SkipPrevious) => "previous",
            nameof(PlayerClient.SeekTo) => "seek",
            _ => null
        };
        if (command == null)
        {
            __result = Task.FromResult(false);
            return false;
        }

        int? value = command == "seek" ? ReadPosition(__args) : null;
        __result = command == "seek" && !value.HasValue
            ? Task.FromResult(false)
            : LocalBridgeServer.EnqueueCommand(command, value);
        return false;
    }

    private static int? ReadPosition(object[] args)
    {
        foreach (var arg in args)
        {
            if (arg == null) continue;
            foreach (var propertyName in new[] { "Position", "PositionMs" })
            {
                var property = arg.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                var value = property?.GetValue(arg);
                if (value is long position && position >= 0 && position <= int.MaxValue) return (int)position;
                if (value is int positionInt && positionInt >= 0) return positionInt;
            }
        }
        return null;
    }

    private static Task<CurrentlyPlayingContext> ReadLocalPlayback()
    {
        try
        {
            var snapshot = LocalBridgeServer.Read();
            var playback = Map(snapshot?.State);
            if (playback != null) playback.ProgressMs = snapshot!.ProgressMs;
            return Task.FromResult<CurrentlyPlayingContext>(playback!);
        }
        catch (Exception error)
        {
            StartupHook.Log($"BRIDGE ERROR: {error.GetType().Name}");
            return Task.FromResult<CurrentlyPlayingContext>(null!);
        }
    }

    internal static CurrentlyPlayingContext Map(BridgeState? state)
    {
        if (state?.TrackUri == null || !state.TrackUri.StartsWith("spotify:track:", StringComparison.Ordinal))
            return null!;

        var trackId = Id(state.TrackUri, "track");
        var artistNames = state.Artists ?? Array.Empty<string>();
        var artists = artistNames.Select((name, index) =>
        {
            var uri = state.ArtistUris?.ElementAtOrDefault(index);
            return new SimpleArtist { Name = name, Uri = uri!, Id = Id(uri, "artist")!, Type = "artist" };
        }).ToList();
        var image = ImageUrl(state.ImageUrl);
        var album = new SimpleAlbum
        {
            Name = state.Album ?? "",
            Uri = state.AlbumUri!,
            Id = Id(state.AlbumUri, "album")!,
            Type = "album",
            Artists = artists,
            Images = image == null ? new List<Image>() : new List<Image> { new Image { Url = image } }
        };
        var track = new FullTrack
        {
            Id = trackId!,
            Uri = state.TrackUri,
            Name = state.Title ?? "",
            DurationMs = state.DurationMs,
            IsPlayable = true,
            IsLocal = state.IsLocal,
            Type = ItemType.Track,
            Artists = artists,
            Album = album,
            ExternalIds = state.Isrc == null ? null! : new Dictionary<string, string> { ["isrc"] = state.Isrc },
            ExternalUrls = trackId == null ? null! : new Dictionary<string, string>
            {
                ["spotify"] = $"https://open.spotify.com/track/{trackId}"
            }
        };
        var contextType = state.ContextUri?.Split(':').ElementAtOrDefault(1);
        return new CurrentlyPlayingContext
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ProgressMs = state.ProgressMs,
            IsPlaying = state.IsPlaying,
            ShuffleState = state.Shuffle,
            RepeatState = state.Repeat switch { 1 => "context", 2 => "track", _ => "off" },
            CurrentlyPlayingType = "track",
            Item = track,
            // Lyricify updates the track ID and starts lyric loading only when Device is present.
            Device = new Device
            {
                Id = "",
                Name = "Spotify",
                Type = "Computer",
                IsActive = true,
                SupportsVolume = false
            },
            Context = state.ContextUri == null ? null! : new Context
            {
                Uri = state.ContextUri,
                Type = contextType!
            },
            Actions = new Actions { Disallows = new Dictionary<string, bool>() }
        };
    }

    private static string? Id(string? uri, string type)
    {
        var prefix = $"spotify:{type}:";
        return uri?.StartsWith(prefix, StringComparison.Ordinal) == true ? uri[prefix.Length..] : null;
    }

    private static string? ImageUrl(string? image)
    {
        if (image == null) return null;
        const string prefix = "spotify:image:";
        if (image.StartsWith(prefix, StringComparison.Ordinal))
            return $"https://i.scdn.co/image/{image[prefix.Length..]}";
        return Uri.TryCreate(image, UriKind.Absolute, out var uri) && uri.Scheme == "https" ? image : null;
    }
}

internal sealed class BridgeState
{
    [JsonPropertyName("track_uri")] public string? TrackUri { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("artists")] public string[]? Artists { get; set; }
    [JsonPropertyName("artist_uris")] public string?[]? ArtistUris { get; set; }
    [JsonPropertyName("album")] public string? Album { get; set; }
    [JsonPropertyName("album_uri")] public string? AlbumUri { get; set; }
    [JsonPropertyName("isrc")] public string? Isrc { get; set; }
    [JsonPropertyName("image_url")] public string? ImageUrl { get; set; }
    [JsonPropertyName("is_local")] public bool IsLocal { get; set; }
    [JsonPropertyName("duration_ms")] public int DurationMs { get; set; }
    [JsonPropertyName("progress_ms")] public int ProgressMs { get; set; }
    [JsonPropertyName("is_playing")] public bool IsPlaying { get; set; }
    [JsonPropertyName("context_uri")] public string? ContextUri { get; set; }
    [JsonPropertyName("shuffle")] public bool Shuffle { get; set; }
    [JsonPropertyName("repeat")] public int Repeat { get; set; }
}
