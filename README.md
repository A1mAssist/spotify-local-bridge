# Spotify Local Bridge: Local Playback for Lyricify 4

English | [简体中文](README.zh-CN.md)

This is an unofficial Windows x64 integration project unaffiliated with Spotify or Lyricify. It lets Lyricify 4 read the current track, playback timeline, and common playback controls from the local Spotify client. Lyrics still come from Lyricify's servers, and sign-in still uses Spotify. This repository contains no Spotify or Lyricify application files.

The patch locations have been checked against Spotify Desktop 1.2.89.539 and Lyricify 4.4.0. Client updates may require changes. The project source is licensed under [MIT](LICENSE); see [third-party notices](THIRD_PARTY_NOTICES.md) for bundled dependencies.

## Download and use

1. Install the desktop version of Spotify from spotify.com and keep an original `Apps\xpui.spa`. If Spicetify previously modified Spotify, you can provide its backup of the original `xpui.spa` when the panel asks for it. The installer preserves the previous XPUI directory and checks the current bootstrap code before patching. Removal stops if Spotify has created a new `xpui.spa`, so it will not overwrite that file.
2. Download `LyricifyLauncher.exe` from the [latest release](https://github.com/A1mAssist/spotify-local-bridge/releases/latest) and run it. In the panel, select `Install Spotify patch`, then `Enable Lyricify shortcut`. Once installed, those menu items change to the corresponding removal and restore actions. After confirmation, installing or removing the Spotify patch closes Spotify and Lyricify; save any unsaved work in either app first.
3. From then on, start Lyricify through the modified Start Menu shortcut. It uses the launcher to load the Hook into Lyricify, after which the launcher exits without opening the management panel. Other desktop and taskbar shortcuts are not modified automatically.
4. Play a track and visit `http://127.0.0.1:48711/state`. Normally it returns JSON. HTTP 204 means no recent playback state is available. The receiver runs only while a Hook-enabled Lyricify process is running.

The TUI's `Now Playing` panel is a client of this local receiver; it does not read Spotify directly. Spotify's patched XPUI posts state to `/ingest`, while the .NET Hook inside Lyricify owns `127.0.0.1:48711`. Spotify can keep playing without Lyricify, but with no Hook there is no receiver, so `Now Playing` has no state to display. Lyricify must be started through the patched shortcut for the TUI and Lyricify playback data to work.

The EXE includes the .NET 8 runtime, the Hook and its dependencies, the Spotify XPUI script, and the install/uninstall scripts. No separate Node or Spicetify runtime is needed. On first run, it extracts these files to `%LOCALAPPDATA%\SpotifyLocalBridge\runtime\<build-id>\` for Lyricify and the installer to load. The selected Lyricify path and Hook log are stored under `%LOCALAPPDATA%\SpotifyLocalBridge\`. Close any older Node bridge using port 48711 before launching.

The original Lyricify shortcut backup, selected application path, logs, and extracted runtime files all live under `%LOCALAPPDATA%\SpotifyLocalBridge\`, independent of the launcher's location. New Spotify XPUI backups and patched directories retained after removal go in its `backups\` subdirectory. The uninstaller also supports older backups under Spotify's `Apps\.spotify-local-bridge-backups\`. A small installation manifest stays with the patched Spotify XPUI to identify the current installation. Moving the launcher does not lose these files, but you must select `Repair Lyricify shortcut` in the panel to point the Start Menu shortcut to the new EXE location.

### Management panel

The left side manages the Spotify patch, Lyricify shortcut, Lyricify application path, and Hook log. The right side shows client connections, current playback, and shortcut status. The panel reads state about every 250 ms and redraws only changed rows. It switches to a compact layout in a narrow terminal.

| Key or action | Effect |
| --- | --- |
| Up / Down, `Enter` | Select and run an action on the left |
| `Q` / `Esc` | Exit the panel without closing Spotify or Lyricify |
| `View hook log` | Read the latest 10,000 raw log entries in the same terminal |
| Log view `F` | Switch between `Events`, which hides repeated successful entries, and the complete `All` view |
| Log view Up / Down, `PgUp` / `PgDn`, `Home` / `End` | Scroll the log |
| Log view `R`, `Q` / `Esc` / `Enter` | Reload the log or return to the panel |

The log view shortens timestamps and wraps long messages. For actions that require confirmation, press `Y` in the panel; any other key cancels. You can enter the Lyricify application path or a missing XPUI archive path in the panel. Management actions first request a normal client close and force-stop any client still running after two seconds.

`Enable Lyricify shortcut` backs up `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Lyricify\Lyricify 4.lnk` to `%LOCALAPPDATA%\SpotifyLocalBridge\Lyricify 4.original.lnk`, then points the shortcut to the launcher's `--launch-only` mode. `Restore Lyricify shortcut` restores the original while the shortcut is still managed by this project. After moving the EXE, run it from its new location and select `Repair Lyricify shortcut` to update the Start Menu link. If Lyricify is already running from an entry point without the Hook, exit it completely from the tray before using the modified shortcut.

You can also pass a Lyricify EXE path to the launcher: it starts that application, then opens the panel. Developers can start the Hook separately with `.\lyricify-hook\run.ps1 -LyricifyExe 'C:\path\to\Lyricify for Spotify.exe'`; the script prints the log path. The log messages `PATCHED playback reads, modes, volume, and library controls` and `PATCHED MediaSession timeline enhancement bypass (including Plus)` indicate that the respective patches were installed.

### Start with Windows

Lyricify must start through the patched launcher shortcut for the Hook to load. The management panel does not need to stay open.

1. Put `LyricifyLauncher.exe` in its permanent location. In the panel, select `Enable Lyricify shortcut` (or `Repair Lyricify shortcut` if the EXE was moved).
2. Disable Lyricify's own startup setting or Windows Startup Apps entry so it cannot start directly without the Hook.
3. Press `Win + R`, enter `shell:startup`, and copy the patched **Lyricify 4** Start Menu shortcut into that folder. At sign-in, this copy starts Lyricify through `--launch-only` without opening the panel; the launcher also starts Spotify if needed.

If you move the EXE later, repair the Start Menu shortcut from the new location and replace the copy in `shell:startup`. Delete the Startup-folder copy to stop automatic launching; restoring the Start Menu shortcut alone does not remove that copy.

## Bridge coverage

The Spotify XPUI script reads the client's own `PlayerAPI`, `PlaybackAPI`, and `LibraryAPI`. It sends playback state to `127.0.0.1:48711` and receives local control commands. A .NET Startup Hook inside Lyricify provides the local receiver, replaces both overloads of `SpotifyAPI.Web.PlayerClient.GetCurrentPlayback` with in-memory state, and forwards pause, resume, previous/next, seek, shuffle/repeat, volume, and saved-track actions to the Spotify client. Coverage includes the current track, timeline, playback mode, volume, liked state, and saved-track checks needed for queue preloading. The TUI reads the same receiver and is not a standalone Spotify reader. Playback state collection uses neither a browser wrapper nor Windows Media Session.

The Hook-enabled process skips Lyricify's Media Session enhancement and its timer entry points. Saved settings for "Media Session enhancement," "timeline correction," and "timeline correction Plus" therefore do not control playback or the timeline in that process. Plus can actively pause and resume playback to refresh the Windows timeline, so all bridge state comes from Spotify XPUI. Lyricify launched without the Hook still follows its saved settings.

Volume is read and set through Spotify's `PlaybackAPI.getVolume()` / `setVolume()`. Spotify's native 0-1 value is converted to Lyricify's 0-100%. A valid reading enables Lyricify's volume slider and mute button; an unavailable reading disables them. Volume events refresh bridge state immediately. Control commands return asynchronously. Requests waiting more than five seconds expire, and expired queued requests are discarded. Spotify's setter may return before the volume actually changes, so use the readback at `/state.volume_percent` to determine the final value.

### ISRC

Spotify's [Get Playback State documentation](https://developer.spotify.com/documentation/web-api/reference/get-information-about-the-users-current-playback) exposes a track ISRC through `item.external_ids.isrc` when available. The script first checks the current track and its metadata. If no ISRC is present, it immediately converts the 22-character base62 track ID to a 32-character hexadecimal GID and uses Spotify's own `RequestBuilder` to request `https://spclient.wg.spotify.com/metadata/4/track/{gid}`. When an `external_id` record in the response has `type: "isrc"`, the Hook maps it to Lyricify's `FullTrack.ExternalIds`.

If Spotify is already requesting the same record, the script retries only that duplicate-request error for up to three seconds. Successful ISRCs are cached in memory by track. Empty results and HTTP 429 are retried after five minutes; transient request errors are retried after 15 seconds. The bridge no longer scans Spotify's disk cache or reads login credentials. Local files, podcasts, alternate releases, and failed metadata requests may still lack an ISRC.

The first playback state after a track change waits for the ISRC lookup so Lyricify can receive it with the initial lyric request. If the lookup fails or reaches its three-second limit, state is sent without an ISRC; a lyric request already sent is not retried automatically. Lyricify 4.4.0 copies the ISRC into its song-information popup only when the popup opens. If metadata arrives later, the Hook checks on the UI thread that the popup still shows the same track before updating that field. `tests/isrc` uses real WPF controls to check the popup update, rejection of an old track, and Dispatcher scheduling.

## Lyricify's Spotify endpoints

The [Lyricify Custom API Client guide](https://docs.lyricify.app/lyricify-4/custom-api-client/) allows a Spotify OAuth Client ID and Secret but has no API base URL setting. The `SpotifyAPI.Web.dll` bundled with Lyricify 4.4.0 uses `https://api.spotify.com/v1/`, and both `GetCurrentPlayback` overloads target `me/player`. Recovered Lyricify method bodies show direct calls to `SpotifyAPIClient.Player.GetCurrentPlayback` during startup and in playback helpers. A test call through its SDK returned local playback state; that test alone is not a complete logged-in end-to-end validation.

| Lyricify 4.4.0 call | Endpoint | When used | Local coverage |
| --- | --- | --- | --- |
| `UserProfile.Current` | `GET /v1/me` | Fetches user ID, email, name, country, subscription type, and image before the main window finishes loading | Not covered |
| `Player.GetCurrentPlayback` | `GET /v1/me/player` | Startup and song-change polling; lyrics depend on its track and device fields | Both SDK overloads hooked |
| `Player.GetQueue` | `GET /v1/me/player/queue` | Automatic queue preload with default settings on Premium accounts | Not covered |
| `Library.CheckTracks` | `GET /v1/me/tracks/contains` | Background liked-state check for nonlocal tracks | Forwarded to `LibraryAPI.contains` |
| `Tracks.GetSeveral` | `GET /v1/tracks?ids=...` | When a track is relinked | Not covered |
| `Player.GetAvailableDevices` | `GET /v1/me/player/devices` | When there is no playback state and the built-in WebView2 player is enabled | Not covered |
| `Player.GetRecentlyPlayed` | `GET /v1/me/player/recently-played` | Home page; other Home sections also call personalization, browse, artist, and follow endpoints | Not covered |
| `Library.SaveTracks` / `RemoveTracks` | `PUT/DELETE /v1/me/tracks` | Heart button | Forwarded to `LibraryAPI.add` / `remove` |
| Playback controls | `PUT/POST /v1/me/player/*` | Pause, resume, seek, skip, shuffle, repeat, volume, and queue | Listed controls except queue go to Spotify XPUI; queue remains remote |
| Spotify OAuth | `GET https://accounts.spotify.com/authorize`, `POST https://accounts.spotify.com/api/token` | First login and token renewal | Not covered |

The recovered authorization flow exchanges a Spotify refresh token, reads `/v1/me`, then passes the refresh token and custom Client credentials to `RegisterLyricify`. The Hook intercepts neither login nor the profile lookup, so Lyricify still requires Spotify authorization and may contact Spotify for other features. It cannot fully replace Lyricify's Spotify connection. For lyric display with an existing valid Lyricify account, the key startup dependencies are `/v1/me` and `/v1/me/player`. Eliminating **all** Spotify Web API requests would also require handling queue, saved-track, and conditional calls or disabling the corresponding features; first-time login would require a separate identity bootstrap. Lyric requests themselves go to Lyricify's `/lyrics/get` service with track metadata and Spotify user fields. This project does not replace that service.

The table is based on the bundled SDK, [SpotifyAPI-NET's PlayerClient](https://github.com/JohnnyCrazy/SpotifyAPI-NET/blob/master/SpotifyAPI.Web/Clients/PlayerClient.cs), and recovered Lyricify call sites. It is not a complete record of runtime request counts. Some auxiliary preload methods still have decompilation errors. The locally recovered assembly was used only for static analysis and is not distributed; the unpacker reported a resource error and possible code virtualization. A complete logged-in request trace has not been captured, and Spotify Connect playback on another device has not been tested. Lyricify updates may change SDK methods or data requirements. If the Hook cannot find the expected two overloads, it logs a patch error and stops that patch.

## Build and verify

Run `.\package-single.ps1` in Windows PowerShell to build the single-file release. To target a different Lyricify package, pass `-SpotifyApiDll 'C:\path\to\SpotifyAPI.Web.dll'`. The output is `dist\LyricifyLauncher.exe`, published as a GitHub Release asset rather than tracked in Git. Lyricify still needs the Hook DLL on disk while loading it, so the EXE extracts its embedded files at runtime.

The following checks are available. The packaging script compiles the latest Hook into a separate staging directory, so the commands explicitly use that DLL:

```powershell
.\package-single.ps1 -SpotifyApiDll 'C:\path\to\SpotifyAPI.Web.dll'
$hook = (Resolve-Path '.\launcher\obj\single-exe-payload\StartupHook.dll').Path
node .\test.mjs
.\lyricify-hook\check-server.ps1 -HookPath $hook -SpotifyApiDll 'C:\path\to\SpotifyAPI.Web.dll'
.\lyricify-hook\check-map.ps1 -HookPath $hook -SpotifyApiDll 'C:\path\to\SpotifyAPI.Web.dll'
dotnet run --project tests/launcher/LauncherCheck.csproj -c Release
dotnet run --project tests/isrc/IsrcCheck.csproj -c Release '-p:SpotifyApiDll=C:\path\to\SpotifyAPI.Web.dll' "-p:HookPath=$hook"
dotnet run --project tests/media-session/MediaSessionCheck.csproj -c Release '-p:SpotifyApiDll=C:\path\to\SpotifyAPI.Web.dll' "-p:HookPath=$hook"
.\install.ps1 -XpuiArchive 'C:\path\to\xpui.spa' -ValidateOnly
.\tests\storage\check.ps1
```

Tests cover volume at 0, 37, and 100%, invalid values, unavailable devices, RPC failures, and expired commands. The receiver checks volume bounds and maps `Device.SupportsVolume` / `VolumePercent`. Tests send an actual SDK `PlayerVolumeRequest` through the Hook prefix to check result propagation. A .NET 6 Harmony harness checks that Media Session initialization and polling are skipped, and that pause, resume, skip, and seek fall back to the hooked SDK. The harness uses the recovered shape of a public helper method; loading the protected Lyricify assembly requires separate deployment validation. Lyricify currently targets .NET 6, which is out of support and may trigger an SDK lifecycle warning during builds.

The XPUI script tests do not rely on Spicetify globals. They cover `PlayerAPI` updates, prototype getter metadata, the initial-state ISRC lookup, duplicate-request retries, track-switch isolation, timeout fallback, playback controls, shuffle/repeat, and `LibraryAPI` saved-track operations. The receiver passed Origin, parameter validation, command queue, timeline, and Hook mapping checks on an isolated port. Both installer bootstrap insertion points were validated against the original Spotify 1.2.89.539 archive.

On the development machine, after restarting both clients, the first sampled `/state` for `spotify:track:1lbNgoJ5iMrMluCyhI4OQP` contained ISRC `GBAHS2500558`. In one next-track test, the first changed `/state` appeared 1.48 seconds after the media-next key and already included ISRC `DGA082501213` for `spotify:track:2oyu6OED3JSxgogTuTZRab`. A Lyricify lyric-request log using this new initial-state path has not yet been captured.

## Known limitations

- The receiver listens only on loopback and checks Spotify XPUI's browser Origin. Another local process can forge HTTP headers, so it is intended for a trusted local machine. The Hook does not store Spotify tokens.
- The receiver estimates playback progress between updates. If Spotify exits unexpectedly, stale state can remain for up to 90 seconds.
- A Spotify update may replace XPUI. The installer checks known bootstrap locations and may need adaptation for a changed version.
- Lyricify's protected initializer crashed when the Hook patched it immediately, so patch installation is currently delayed by two seconds. A Lyricify update requires renewed live validation.
