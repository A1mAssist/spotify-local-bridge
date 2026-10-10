param(
    [string]$SpotifyApiDll,
    [string]$HookPath = (Join-Path $PSScriptRoot 'bin/Release/net6.0/StartupHook.dll')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$hookPath = (Resolve-Path -LiteralPath $HookPath).Path
if ($SpotifyApiDll) {
    $sdkPath = (Resolve-Path -LiteralPath $SpotifyApiDll).Path
    [System.Runtime.Loader.AssemblyLoadContext]::Default.LoadFromAssemblyPath($sdkPath) | Out-Null
}
$hook = [System.Runtime.Loader.AssemblyLoadContext]::Default.LoadFromAssemblyPath($hookPath)
$server = $hook.GetType('LocalBridgeServer', $true)
$flags = [System.Reflection.BindingFlags]'NonPublic,Static'
$start = $server.GetMethod('Start', $flags)
$stop = $server.GetMethod('Stop', $flags)

$probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$probe.Start()
$port = ([System.Net.IPEndPoint]$probe.LocalEndpoint).Port
$probe.Stop()

$start.Invoke($null, @($port)) | Out-Null
$base = "http://127.0.0.1:$port"
$origin = 'https://xpui.app.spotify.com'
try {
    if ((Invoke-WebRequest "$base/state" -SkipHttpErrorCheck).StatusCode -ne 204) { throw 'Expected empty state.' }
    if ((Invoke-WebRequest "$base/ingest" -Method Options -Headers @{ Origin = $origin } -SkipHttpErrorCheck).StatusCode -ne 204) { throw 'Spotify preflight failed.' }

    $state = @{
        track_uri = 'spotify:track:0BxE4FqsDD1Ot4YuBXwAPp'
        title = '505'
        artists = @('Arctic Monkeys')
        album = 'Favourite Worst Nightmare'
        track_number = 7
        album_release_date = '2007-04-23'
        album_total_tracks = 13
        duration_ms = 253000
        progress_ms = 5000
        is_playing = $true
        context_uri = $null
        shuffle = $false
        repeat = 0
    }
    function Post-State($value, $requestOrigin = $origin) {
        Invoke-WebRequest "$base/ingest" -Method Post -Headers @{ Origin = $requestOrigin } -ContentType 'application/json' -Body ($value | ConvertTo-Json -Compress) -SkipHttpErrorCheck
    }
    if ((Post-State $state 'https://example.com').StatusCode -ne 403) { throw 'Foreign origin was accepted.' }
    $invalid = $state.Clone()
    $invalid.progress_ms = -1
    if ((Post-State $invalid).StatusCode -ne 400) { throw 'Invalid progress was accepted.' }
    $invalid = $state.Clone()
    $invalid.isrc = 'invalid'
    if ((Post-State $invalid).StatusCode -ne 400) { throw 'Invalid ISRC was accepted.' }
    $invalid = $state.Clone()
    $invalid.title = 'x' * 17000
    if ((Post-State $invalid).StatusCode -ne 413) { throw 'Oversized state was accepted.' }
    $state.isrc = 'USUM71703861'
    foreach ($percent in @(-1, 101)) {
        $invalid = $state.Clone()
        $invalid.volume_percent = $percent
        if ((Post-State $invalid).StatusCode -ne 400) { throw "Invalid volume $percent was accepted." }
    }
    foreach ($speed in @(-0.1, 4.1)) {
        $invalid = $state.Clone()
        $invalid.playback_speed = $speed
        if ((Post-State $invalid).StatusCode -ne 400) { throw 'Invalid playback speed was accepted.' }
    }
    $snapshotType = $hook.GetType('BridgeSnapshot', $true)
    $timedState = [Activator]::CreateInstance($hook.GetType('BridgeState', $true))
    $timedState.ProgressMs = 5000
    $timedState.DurationMs = 253000
    foreach ($speed in @(0.0, 0.5, 1.0, 2.0)) {
        $timedState.IsPlaying = $true
        $timedState.PlaybackSpeed = $speed
        $snapshot = [Activator]::CreateInstance($snapshotType, @($timedState, ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() - 2000)))
        $expected = 5000 + 2000 * $speed
        if ([Math]::Abs($snapshot.ProgressMs - $expected) -gt 100) { throw 'Receiver extrapolation did not use Spotify playback speed.' }
        $timedState.IsPlaying = $false
        if ($snapshot.ProgressMs -ne 5000) { throw 'Stopped/buffering timeline advanced.' }
    }
    foreach ($percent in @(0, 100)) {
        $state.volume_percent = $percent
        if ((Post-State $state).StatusCode -ne 204) { throw "Valid volume $percent was rejected." }
        if (((Invoke-WebRequest "$base/state").Content | ConvertFrom-Json).volume_percent -ne $percent) {
            throw 'Stored volume is incorrect.'
        }
    }
    $state.volume_percent = 37
    if ((Post-State $state).StatusCode -ne 204) { throw 'Valid state was rejected.' }
    $enqueue = $server.GetMethod('EnqueueCommand', $flags)
    if (-not $enqueue.Invoke($null, @('seek', [Nullable[int]]::new(12000)))) { throw 'Command was not queued.' }
    if (-not $enqueue.Invoke($null, @('shuffle', [Nullable[int]]::new(1)))) { throw 'Shuffle command was not queued.' }
    if (-not $enqueue.Invoke($null, @('repeat', [Nullable[int]]::new(2)))) { throw 'Repeat command was not queued.' }
    $foreignCommands = Invoke-WebRequest "$base/commands" -Headers @{ Origin = 'https://example.com' } -SkipHttpErrorCheck
    if ($foreignCommands.StatusCode -ne 403) { throw 'Foreign origin read commands.' }
    $commandsJson = (Invoke-WebRequest "$base/commands" -Headers @{ Origin = $origin }).Content
    if ($commandsJson -notmatch '"name"\s*:\s*"seek"' -or $commandsJson -notmatch '"value"\s*:\s*12000' -or
        $commandsJson -notmatch '"name"\s*:\s*"shuffle"' -or $commandsJson -notmatch '"name"\s*:\s*"repeat"') { throw 'Command JSON did not use XPUI field names.' }
    $commands = $commandsJson | ConvertFrom-Json
    if ($commands.Count -ne 3 -or $commands[0].name -ne 'seek' -or $commands[0].value -ne 12000 -or
        $commands[1].name -ne 'shuffle' -or $commands[1].value -ne 1 -or
        $commands[2].name -ne 'repeat' -or $commands[2].value -ne 2) { throw 'Queued command was not returned.' }
    $current = (Invoke-WebRequest "$base/state").Content | ConvertFrom-Json
    if ($current.track_uri -ne $state.track_uri -or $current.isrc -ne $state.isrc -or
        $current.track_number -ne 7 -or $current.album_release_date -ne '2007-04-23' -or
        $current.album_total_tracks -ne 13 -or
        $current.progress_ms -lt 5000 -or $current.updated_at_ms -le 0) { throw 'Stored state is incorrect.' }
    if ($server.GetMethod('NextRepeat', $flags).Invoke($null, @()) -ne 1) { throw 'Repeat cycle did not advance from off to context.' }
    if ($SpotifyApiDll) {
        $patch = $hook.GetType('PlaybackPatch', $true)
        $read = $patch.GetMethod('ReadLocalPlayback', $flags)
        $playback = $read.Invoke($null, @()).GetAwaiter().GetResult()
        if ($playback.Item.Id -ne '0BxE4FqsDD1Ot4YuBXwAPp' -or
            $playback.ProgressMs -lt 5000 -or $playback.Item.ExternalIds['isrc'] -ne $state.isrc) {
            throw 'Hook did not map embedded bridge state.'
        }
        $readPosition = $patch.GetMethod('ReadPosition', $flags)
        $seekRequest = [SpotifyAPI.Web.PlayerSeekToRequest]::new([long]12000)
        $readArgs = [object[]]::new(1)
        $readArgs[0] = [object[]]@($seekRequest)
        if ($readPosition.Invoke($null, $readArgs) -ne 12000) { throw 'Hook did not read PlayerSeekToRequest.PositonMs.' }
        $readShuffle = $patch.GetMethod('ReadShuffle', $flags)
        $shuffleRequest = [SpotifyAPI.Web.PlayerShuffleRequest]::new($true)
        $shuffleArgs = [object[]]::new(1)
        $shuffleArgs[0] = [object[]]@($shuffleRequest)
        if ($readShuffle.Invoke($null, $shuffleArgs) -ne 1) { throw 'Hook did not read PlayerShuffleRequest.State.' }
        $control = $patch.GetMethod('ControlPrefix', $flags)
        $volumeMethod = [SpotifyAPI.Web.PlayerClient].GetMethod('SetVolume')
        foreach ($percent in @(37, 0, 100)) {
            $volumeArgs = [object[]]@($volumeMethod, [object[]]@([SpotifyAPI.Web.PlayerVolumeRequest]::new($percent)), $null)
            if ($control.Invoke($null, $volumeArgs)) { throw 'Volume request was not intercepted.' }
            $volumeTask = $volumeArgs[2]
            $volumeCommands = (Invoke-WebRequest "$base/commands" -Headers @{ Origin = $origin }).Content | ConvertFrom-Json
            if ($volumeCommands.Count -ne 1 -or $volumeCommands[0].name -ne 'volume' -or $volumeCommands[0].value -ne $percent) {
                throw 'SDK volume request was not queued correctly.'
            }
            $result = @{ id = $volumeCommands[0].id; name = 'volume'; success = ($percent -ne 100) }
            Invoke-WebRequest "$base/control-result" -Method Post -Headers @{ Origin = $origin } -ContentType 'application/json' -Body ($result | ConvertTo-Json -Compress) | Out-Null
            if ($volumeTask.GetAwaiter().GetResult() -ne ($percent -ne 100)) { throw 'Hook did not propagate volume result.' }
        }
        foreach ($percent in @(-1, 101)) {
            $volumeArgs = [object[]]@($volumeMethod, [object[]]@([SpotifyAPI.Web.PlayerVolumeRequest]::new($percent)), $null)
            $control.Invoke($null, $volumeArgs) | Out-Null
            if ($volumeArgs[2].GetAwaiter().GetResult()) { throw 'Invalid SDK volume was accepted.' }
        }
        $volumeArgs = [object[]]@($volumeMethod, [object[]]@([SpotifyAPI.Web.PlayerVolumeRequest]::new(37)), $null)
        $control.Invoke($null, $volumeArgs) | Out-Null
        if ($volumeArgs[2].GetAwaiter().GetResult()) { throw 'Unanswered volume request succeeded.' }
        if ($server.GetMethod('TakeCommands', $flags).Invoke($null, @()).Length -ne 0) {
            throw 'Expired volume request would execute after reconnection.'
        }
    }
    $state.isrc = $null
    if ((Post-State $state).StatusCode -ne 204) { throw 'State without ISRC was rejected.' }
    $noIsrc = (Invoke-WebRequest "$base/state").Content | ConvertFrom-Json
    if ($null -ne $noIsrc.isrc) { throw 'Missing ISRC was filled outside Spotify XPUI.' }
    $state.isrc = 'USUM71703861'
    $state.is_playing = $false
    $state.progress_ms = 9000
    if ((Post-State $state).StatusCode -ne 204) { throw 'Pause update was rejected.' }
    $paused = (Invoke-WebRequest "$base/state").Content | ConvertFrom-Json
    if ($paused.progress_ms -ne 9000 -or $paused.is_playing) { throw 'Paused timeline is incorrect.' }
    'Embedded bridge checks passed'
} finally {
    $stop.Invoke($null, @()) | Out-Null
}
