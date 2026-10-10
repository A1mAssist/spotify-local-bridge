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
    private static int trackInfoRefreshPending;

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
        PatchControl(harmony, controlPrefix, nameof(PlayerClient.SetShuffle), 2);
        PatchControl(harmony, controlPrefix, nameof(PlayerClient.SetRepeat), 2);
        PatchControl(harmony, controlPrefix, nameof(PlayerClient.SetVolume), 2);
        PatchLibrary(harmony);
        PatchMediaSessionControls(harmony);
        StartupHook.Log("PATCHED playback reads, modes, volume, and library controls");
    }

    private static void PatchControl(Harmony harmony, HarmonyMethod prefix, string name, int parameterCount)
    {
        var method = typeof(PlayerClient).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SingleOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == parameterCount);
        if (method == null) throw new InvalidOperationException($"Expected {name} overload with {parameterCount} parameters");
        harmony.Patch(method, prefix: prefix);
        StartupHook.Log($"PATCHED {name} control");
    }

    private static void PatchMediaSessionControls(Harmony harmony)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("Lyricify_for_Spotify.Helpers.MediaSession.MediaSessionHelper", throwOnError: false))
            .FirstOrDefault(candidate => candidate != null);
        if (type == null)
        {
            throw new InvalidOperationException("MediaSessionHelper type not found.");
        }

        var init = type.GetMethod("InitMediaSessionManager", BindingFlags.Instance | BindingFlags.Public);
        var timer = type.GetMethod("MediaSessionTimerEvent", BindingFlags.Instance | BindingFlags.Public);
        if (init == null || timer == null)
            throw new InvalidOperationException("Expected MediaSession initialization and timer methods.");
        // Lyricify's Plus correction issues pause/resume commands; XPUI supplies the timeline instead.
        harmony.Patch(init, prefix: new HarmonyMethod(typeof(PlaybackPatch).GetMethod(nameof(MediaSessionInitPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
        harmony.Patch(timer, prefix: new HarmonyMethod(typeof(PlaybackPatch).GetMethod(nameof(MediaSessionTimerPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
        StartupHook.Log("PATCHED MediaSession timeline enhancement bypass (including Plus)");

        foreach (var name in new[] { "PausePlayback", "ResumePlayback", "SkipPrevious", "SkipNext", "SeekTo", "SetShuffle", "SetRepeat" })
        {
            var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                StartupHook.Log($"MEDIA SESSION CONTROL NOT FOUND {name}");
                continue;
            }
            var prefix = new HarmonyMethod(typeof(PlaybackPatch).GetMethod(nameof(MediaSessionControlPrefix), BindingFlags.NonPublic | BindingFlags.Static));
            harmony.Patch(method, prefix: prefix);
            StartupHook.Log($"PATCHED MediaSessionHelper.{name} fallback");
        }
    }

    private static bool MediaSessionTimerPrefix(ref Task<bool> __result)
    {
        __result = Task.FromResult(false);
        return false;
    }

    private static bool MediaSessionInitPrefix() => false;

    private static void PatchLibrary(Harmony harmony)
    {
        var check = typeof(LibraryClient).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SingleOrDefault(method => method.Name == nameof(LibraryClient.CheckTracks) && method.GetParameters().Length == 2);
        var save = typeof(LibraryClient).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SingleOrDefault(method => method.Name == nameof(LibraryClient.SaveTracks) && method.GetParameters().Length == 2);
        var remove = typeof(LibraryClient).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SingleOrDefault(method => method.Name == nameof(LibraryClient.RemoveTracks) && method.GetParameters().Length == 2);
        if (check == null || save == null || remove == null)
            throw new InvalidOperationException("Expected LibraryAPI track methods.");

        harmony.Patch(check, prefix: new HarmonyMethod(typeof(PlaybackPatch).GetMethod(nameof(LibraryCheckPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
        harmony.Patch(save, prefix: new HarmonyMethod(typeof(PlaybackPatch).GetMethod(nameof(LibraryMutationPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
        harmony.Patch(remove, prefix: new HarmonyMethod(typeof(PlaybackPatch).GetMethod(nameof(LibraryMutationPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
        StartupHook.Log("PATCHED LibraryAPI CheckTracks, SaveTracks, RemoveTracks");
    }

    private static bool Prefix(ref Task<CurrentlyPlayingContext> __result)
    {
        __result = ReadLocalPlayback();
        return false;
    }

    private static bool ControlPrefix(MethodBase __originalMethod, object[] __args, ref Task<bool> __result)
    {
        if (__originalMethod.Name == nameof(PlayerClient.SetVolume))
        {
            __result = SetLocalVolume(__args.OfType<PlayerVolumeRequest>().FirstOrDefault()?.VolumePercent);
            return false;
        }
        var command = __originalMethod.Name switch
        {
            nameof(PlayerClient.PausePlayback) => "pause",
            nameof(PlayerClient.ResumePlayback) => "resume",
            nameof(PlayerClient.SkipNext) => "next",
            nameof(PlayerClient.SkipPrevious) => "previous",
            nameof(PlayerClient.SeekTo) => "seek",
            nameof(PlayerClient.SetShuffle) => "shuffle",
            nameof(PlayerClient.SetRepeat) => "repeat",
            _ => null
        };
        if (command == null)
        {
            __result = Task.FromResult(false);
            return false;
        }

        int? value = command switch
        {
            "seek" => ReadPosition(__args),
            "shuffle" => ReadShuffle(__args),
            "repeat" => ReadRepeat(__args),
            _ => null
        };
        if ((command == "seek" || command == "shuffle" || command == "repeat") && !value.HasValue)
        {
            StartupHook.Log($"CONTROL REJECT {command} (missing value)");
            __result = Task.FromResult(false);
            return false;
        }

        var accepted = LocalBridgeServer.EnqueueCommand(command, value).GetAwaiter().GetResult();
        StartupHook.Log($"CONTROL {command}{(value.HasValue ? $" {value.Value}" : "")} queued={accepted}");
        __result = Task.FromResult(accepted);
        return false;
    }

    private static async Task<bool> SetLocalVolume(int? percent)
    {
        if (percent is not (>= 0 and <= 100)) return false;
        try
        {
            var result = await LocalBridgeServer.EnqueueRequestAsync("volume", percent).ConfigureAwait(false);
            StartupHook.Log($"CONTROL volume {percent} success={result.Success}");
            return result.Success;
        }
        catch (Exception error)
        {
            StartupHook.Log($"CONTROL volume failed={error.GetType().Name}");
            return false;
        }
    }

    private static bool MediaSessionControlPrefix(MethodBase __originalMethod, object[] __args, ref Task<bool> __result)
    {
        var command = __originalMethod.Name switch
        {
            "SetShuffle" => "shuffle",
            "SetRepeat" => "repeat",
            _ => null
        };
        if (command != null)
        {
            var value = command == "shuffle" ? ReadShuffle(__args) : ReadRepeat(__args);
            if (!value.HasValue && command == "repeat") value = LocalBridgeServer.NextRepeat();
            if (value.HasValue)
            {
                try
                {
                    var result = LocalBridgeServer.EnqueueRequestAsync(command, value).GetAwaiter().GetResult();
                    StartupHook.Log($"MEDIA SESSION {command} success={result.Success}");
                    __result = Task.FromResult(result.Success);
                }
                catch (Exception error)
                {
                    StartupHook.Log($"MEDIA SESSION {command} failed={error.GetType().Name}");
                    __result = Task.FromResult(false);
                }
                return false;
            }
        }

        StartupHook.Log($"MEDIA SESSION BYPASS {__originalMethod.Name}");
        __result = Task.FromResult(false);
        return false;
    }

    private static bool LibraryCheckPrefix(object[] __args, ref Task<List<bool>> __result)
    {
        var ids = ReadIds(__args);
        __result = ids == null
            ? Task.FromException<List<bool>>(new InvalidOperationException("Library check request did not contain track IDs."))
            : LocalBridgeServer.CheckTracksAsync(ids);
        return false;
    }

    private static bool LibraryMutationPrefix(MethodBase __originalMethod, object[] __args, ref Task<bool> __result)
    {
        var ids = ReadIds(__args);
        if (ids == null)
        {
            __result = Task.FromException<bool>(new InvalidOperationException("Library request did not contain track IDs."));
            return false;
        }

        var command = __originalMethod.Name == nameof(LibraryClient.SaveTracks) ? "library-add" : "library-remove";
        __result = LocalBridgeServer.MutateTracksAsync(command, ids);
        return false;
    }

    private static int? ReadPosition(object[] args)
    {
        foreach (var arg in args)
        {
            if (arg == null) continue;
            foreach (var propertyName in new[] { "Position", "PositionMs", "PositonMs" })
            {
                var property = arg.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                var value = property?.GetValue(arg);
                if (value is long position && position >= 0 && position <= int.MaxValue) return (int)position;
                if (value is int positionInt && positionInt >= 0) return positionInt;
            }
        }
        return null;
    }

    private static int? ReadShuffle(object[] args)
    {
        foreach (var arg in args)
        {
            if (arg is bool shuffle) return shuffle ? 1 : 0;
            var stateProperty = arg?.GetType().GetProperty("State", BindingFlags.Instance | BindingFlags.Public);
            if (stateProperty?.GetValue(arg) is bool requestState) return requestState ? 1 : 0;
        }
        return null;
    }

    private static int? ReadRepeat(object[] args)
    {
        foreach (var arg in args)
        {
            if (arg == null) continue;
            object value = arg;
            var stateProperty = arg.GetType().GetProperty("StateParam", BindingFlags.Instance | BindingFlags.Public);
            if (stateProperty != null) value = stateProperty.GetValue(arg)!;
            var name = value.ToString();
            if (name == null) continue;
            return name switch
            {
                "Track" => 2,
                "Context" => 1,
                "Off" => 0,
                _ => null
            };
        }
        return null;
    }

    private static string[]? ReadIds(object[] args)
    {
        foreach (var arg in args)
        {
            if (arg == null) continue;
            var property = arg.GetType().GetProperty("Ids", BindingFlags.Instance | BindingFlags.Public);
            if (property?.GetValue(arg) is IEnumerable<string> ids)
                return ids.ToArray();
        }
        return null;
    }

    private static Task<CurrentlyPlayingContext> ReadLocalPlayback()
    {
        try
        {
            var snapshot = LocalBridgeServer.Read();
            var playback = Map(snapshot?.State);
            if (playback != null)
            {
                playback.ProgressMs = snapshot!.ProgressMs;
                RefreshTrackInfo((FullTrack)playback.Item);
            }
            return Task.FromResult<CurrentlyPlayingContext>(playback!);
        }
        catch (Exception error)
        {
            StartupHook.Log($"BRIDGE ERROR: {error.GetType().Name}");
            return Task.FromResult<CurrentlyPlayingContext>(null!);
        }
    }

    private static void RefreshTrackInfo(FullTrack track)
    {
        if (track.ExternalIds?.TryGetValue("isrc", out var isrc) != true || string.IsNullOrEmpty(isrc)
            || Interlocked.Exchange(ref trackInfoRefreshPending, 1) != 0) return;
        try
        {
            var application = Type.GetType("System.Windows.Application, PresentationFramework")
                ?.GetProperty("Current")?.GetValue(null);
            var dispatcher = application?.GetType().GetProperty("Dispatcher")?.GetValue(application);
            var beginInvoke = dispatcher?.GetType().GetMethod("BeginInvoke", new[] { typeof(Delegate), typeof(object[]) });
            if (beginInvoke == null)
            {
                Interlocked.Exchange(ref trackInfoRefreshPending, 0);
                return;
            }
            // The info page copies ISRC only when opened; refresh late metadata on its UI thread.
            Action refresh = () =>
            {
                try
                {
                    var mainType = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(assembly => assembly.GetType("Lyricify_for_Spotify.MainWindow", false))
                        .FirstOrDefault(type => type != null);
                    var main = mainType?.GetMethod("GetMainWindow", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                    if (main == null) return;
                    var frame = mainType?.GetField("FrameTrackInfo", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main);
                    var page = frame?.GetType().GetProperty("Content")?.GetValue(frame);
                    if (UpdateTrackInfoPage(page, track.Id, isrc)) StartupHook.Log($"ISRC UI {track.Uri} {isrc}");
                }
                catch (Exception error) { StartupHook.Log($"ISRC UI ERROR: {error.GetType().Name}"); }
                finally { Interlocked.Exchange(ref trackInfoRefreshPending, 0); }
            };
            beginInvoke.Invoke(dispatcher, new object[] { refresh, Array.Empty<object>() });
        }
        catch (Exception error)
        {
            Interlocked.Exchange(ref trackInfoRefreshPending, 0);
            StartupHook.Log($"ISRC UI ERROR: {error.GetType().Name}");
        }
    }

    internal static bool UpdateTrackInfoPage(object? page, string trackId, string isrc)
    {
        if (page?.GetType().FullName != "Lyricify_for_Spotify.Pages.PageAudioFeatures") return false;
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var idBlock = page.GetType().GetField("TextBlock_TrackID", fields)?.GetValue(page);
        if (idBlock?.GetType().GetProperty("Text")?.GetValue(idBlock) as string != trackId) return false;
        var isrcBlock = page.GetType().GetField("TextBlock_TrackISRC", fields)?.GetValue(page);
        var text = isrcBlock?.GetType().GetProperty("Text");
        if (text == null || text.GetValue(isrcBlock) as string == isrc) return false;
        text.SetValue(isrcBlock, isrc);
        return true;
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
            Images = image == null ? new List<Image>() : new List<Image> { new Image { Url = image } },
            ReleaseDate = state.AlbumReleaseDate,
            TotalTracks = state.AlbumTotalTracks ?? 0
        };
        var track = new FullTrack
        {
            Id = trackId!,
            Uri = state.TrackUri,
            Name = state.Title ?? "",
            DurationMs = state.DurationMs,
            TrackNumber = state.TrackNumber ?? 0,
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
                SupportsVolume = state.VolumePercent.HasValue,
                VolumePercent = state.VolumePercent
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
    [JsonPropertyName("track_number")] public int? TrackNumber { get; set; }
    [JsonPropertyName("album_release_date")] public string? AlbumReleaseDate { get; set; }
    [JsonPropertyName("album_total_tracks")] public int? AlbumTotalTracks { get; set; }
    [JsonPropertyName("isrc")] public string? Isrc { get; set; }
    [JsonPropertyName("image_url")] public string? ImageUrl { get; set; }
    [JsonPropertyName("is_local")] public bool IsLocal { get; set; }
    [JsonPropertyName("duration_ms")] public int DurationMs { get; set; }
    [JsonPropertyName("progress_ms")] public int ProgressMs { get; set; }
    [JsonPropertyName("is_playing")] public bool IsPlaying { get; set; }
    [JsonPropertyName("playback_speed")] public double PlaybackSpeed { get; set; } = 1;
    [JsonPropertyName("context_uri")] public string? ContextUri { get; set; }
    [JsonPropertyName("shuffle")] public bool Shuffle { get; set; }
    [JsonPropertyName("repeat")] public int Repeat { get; set; }
    [JsonPropertyName("volume_percent")] public int? VolumePercent { get; set; }
}
