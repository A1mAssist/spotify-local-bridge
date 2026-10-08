# Spotify Local Bridge (Lyricify 4 prototype)

This package has three parts. A standalone XPUI script reads Spotify's own `PlayerAPI` and sends playback state to a loopback-only Node.js companion at `http://127.0.0.1:48711`. A .NET startup hook replaces the two `SpotifyAPI.Web.PlayerClient.GetCurrentPlayback` overloads in Lyricify with that local state. The installer patches Spotify XPUI's local bootstrap to load the script; no Spicetify runtime, browser wrapper or Windows media session is used. The bridge does not handle lyrics or credentials.

Scope: current song, play/pause state and timeline only. Both Lyricify SDK overloads use `GET /v1/me/player`; the recovered Lyricify assembly has no `GetCurrentlyPlaying` call. Lyrics continue to come from Lyricify's server. The other Spotify calls listed below are documented dependencies, not planned local replacements. Lyricify can still contact Spotify for login and unrelated features.

Spotify's [Get Playback State](https://developer.spotify.com/documentation/web-api/reference/get-information-about-the-users-current-playback) defines `item.external_ids.isrc` for tracks when available. The script passes an ISRC through if it exists in Spotify's current item or metadata, then tries the client's `sp://metadata/track/{id}` Cosmos route as an optional fallback. The hook maps a valid result to `FullTrack.ExternalIds` for Lyricify's lyric request. ISRC is not guaranteed; the lyric path still works without it. The installed Spotify version's underlying cache shows `spclient.wg.spotify.com/metadata/4/track/<32-hex-gid>?market=from_token`, but its body is binary and has not been decoded by this bridge.

## Run on Windows

1. Use the desktop Spotify installation from spotify.com and its original `Apps\xpui.spa`. Close Spotify and run `./install.ps1`. On a machine previously modified by Spicetify, pass the original archive explicitly: `./install.ps1 -XpuiArchive "$env:APPDATA\spicetify\Backup\xpui.spa"`. This uses that archive once; Spicetify is not needed afterward. The installer preserves the previous XPUI directory and fails if the inspected Spotify bootstrap has changed. Run `./uninstall.ps1` with Spotify closed to restore the previous directory. If Spotify creates a new `xpui.spa` in the meantime, uninstall stops before changing files so that archive is not overwritten.
2. Start the companion with `start.cmd` (or `node bridge.mjs`), then start Spotify. Play a track and verify that `http://127.0.0.1:48711/state` returns JSON. HTTP 204 means there is no recent state. The service listens only on `127.0.0.1`.
3. Close Lyricify completely. In PowerShell, run `./lyricify-hook/run.ps1 -LyricifyExe 'C:\path\to\Lyricify for Spotify.exe'`. The script starts that process with a process-specific startup hook. It prints the log path; look for `PATCHED PlayerClient.GetCurrentPlayback (2 overloads)`.

The package includes compiled .NET 6 hook binaries and Harmony 2.3.3. To rebuild against a different Lyricify package, run `dotnet build lyricify-hook/StartupHook.csproj -c Release '-p:SpotifyApiDll=C:\path\to\SpotifyAPI.Web.dll'`. Run `node test.mjs` to check the companion and XPUI script's state format, `./install.ps1 -XpuiArchive 'C:\path\to\xpui.spa' -ValidateOnly` to check the Spotify bootstrap without changing its files, then `./lyricify-hook/check-map.ps1 -SpotifyApiDll 'C:\path\to\SpotifyAPI.Web.dll'` to check the SDK playback mapping.

## Verified scope

The [Lyricify custom API Client guide](https://docs.lyricify.app/lyricify-4/custom-api-client/) configures a Spotify OAuth Client ID and Secret, but has no API base URL setting. In the Lyricify 4.4.0 portable package, `SpotifyAPI.Web.dll` defines the Spotify API base as `https://api.spotify.com/v1/`; both `PlayerClient.GetCurrentPlayback` overloads target `me/player`. Recovered Lyricify method bodies show direct calls to `SpotifyAPIClient.Player.GetCurrentPlayback` during startup and in its playback helper. A synthetic call through the bundled SDK returned local playback through this hook. This is not a logged-in Lyricify end-to-end test.

The recovered authorization flow exchanges a code for a Spotify refresh token, reads `/v1/me`, and passes the refresh token and custom Client credentials to `RegisterLyricify`. This hook does not intercept login or profile lookup, so Lyricify still requires Spotify authorization and can contact Spotify. It cannot currently replace Lyricify's complete Spotify connection. Spotify Connect playback on another device has not been tested. Lyricify updates may change the SDK method or data expectations; the hook fails closed and logs a patch error if it no longer finds exactly two overloads.

| Lyricify 4.4.0 call path | Endpoint | When used | Local coverage |
| --- | --- | --- | --- |
| `UserProfile.Current` | `GET /v1/me` | Required before the main window finishes loading; supplies ID, email, display name, country, product and images | None |
| `Player.GetCurrentPlayback` | `GET /v1/me/player` | Required at startup and polled for song changes; lyrics load from its track and device fields | Both SDK overloads hooked |
| `Player.GetQueue` | `GET /v1/me/player/queue` | Automatic for Premium accounts with default queue preloading settings | None |
| `Library.CheckTracks` | `GET /v1/me/tracks/contains` | Background saved-track status on each nonlocal song | None |
| `Tracks.GetSeveral` | `GET /v1/tracks?ids=...` | Conditional on a relinked track | None |
| `Player.GetAvailableDevices` | `GET /v1/me/player/devices` | Conditional on no playback and the built-in WebView2 player setting | None |
| `Player.GetRecentlyPlayed` | `GET /v1/me/player/recently-played` | Home page; other Home panels also call personalization, browse, artist and follow APIs | None |
| Player control methods | `PUT/POST /v1/me/player/*` | Pause, resume, seek, skip, shuffle, repeat, volume and queue controls | None |
| Spotify OAuth | `GET https://accounts.spotify.com/authorize`, `POST https://accounts.spotify.com/api/token` | First login and token renewal | None |

For lyric display with an existing valid Lyricify account, the hard startup dependencies are `/v1/me` and `/v1/me/player`. Preventing **all** Spotify Web API traffic also requires handling automatic queue, saved-track and conditional calls or suppressing those features. A first-time login requires a separate identity/bootstrap solution: `Custom API Client` changes OAuth credentials, not the API host. The lyric request itself goes to Lyricify's `/lyrics/get` server with track metadata and Spotify user fields; this bridge does not replace it. The recovered code has a few decompilation errors in auxiliary preload methods, and no logged-in runtime request trace exists yet, so this table is a scoped static call-path inventory rather than an exhaustive request log.

The endpoint mapping comes from the bundled SDK and [SpotifyAPI-NET's PlayerClient](https://github.com/JohnnyCrazy/SpotifyAPI-NET/blob/master/SpotifyAPI.Web/Clients/PlayerClient.cs). Recovered Lyricify code establishes call sites, not actual runtime request counts. The recovered DLL at `../../work/lyricify-audit/slayer-input/Lyricify for Spotify_Slayed.dll` is for static analysis only: the unpacker still reports one resource error and possible code virtualization.

## Validation status

The standalone XPUI script and companion passed a test without any Spicetify global object, and the installer validated both exact bootstrap insertion points against the original Spotify 1.2.89.539 archive. On the development machine, the installed XPUI index contains the bridge script and no Spicetify wrapper; the restarted Spotify client sent a fresh track URI, artists, album, image and a progress delta of 2024 ms over a two-second interval. ISRC was null for that track, so the live Cosmos fallback result is still unconfirmed. The hook passed a synthetic call through both overloads of Lyricify's bundled `SpotifyAPI.Web.dll`. After adding a synthetic `Device` (required by Lyricify's lyric-loading branch), the hook rebuilt without warnings and a mapping check confirmed a track and device. No logged-in Lyricify end-to-end run was performed.

The companion accepts state updates only with Spotify XPUI's browser Origin and does not expose a network listener beyond loopback. Other local processes can forge HTTP headers, so this is intended for a trusted local machine. Neither the companion nor the hook stores Spotify tokens. Spotify updates may replace XPUI; the installer validates its known bootstrap anchors and will require adaptation when they change.
