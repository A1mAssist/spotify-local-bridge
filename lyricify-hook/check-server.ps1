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
    if ((Post-State $state).StatusCode -ne 204) { throw 'Valid state was rejected.' }
    $enqueue = $server.GetMethod('EnqueueCommand', $flags)
    if (-not $enqueue.Invoke($null, @('seek', [Nullable[int]]::new(12000)))) { throw 'Command was not queued.' }
    $foreignCommands = Invoke-WebRequest "$base/commands" -Headers @{ Origin = 'https://example.com' } -SkipHttpErrorCheck
    if ($foreignCommands.StatusCode -ne 403) { throw 'Foreign origin read commands.' }
    $commands = (Invoke-WebRequest "$base/commands" -Headers @{ Origin = $origin }).Content | ConvertFrom-Json
    if ($commands.Count -ne 1 -or $commands[0].name -ne 'seek' -or $commands[0].value -ne 12000) { throw 'Queued command was not returned.' }
    $current = (Invoke-WebRequest "$base/state").Content | ConvertFrom-Json
    if ($current.track_uri -ne $state.track_uri -or $current.isrc -ne $state.isrc -or
        $current.progress_ms -lt 5000 -or $current.updated_at_ms -le 0) { throw 'Stored state is incorrect.' }
    if ($SpotifyApiDll) {
        $patch = $hook.GetType('PlaybackPatch', $true)
        $read = $patch.GetMethod('ReadLocalPlayback', $flags)
        $playback = $read.Invoke($null, @()).GetAwaiter().GetResult()
        if ($playback.Item.Id -ne '0BxE4FqsDD1Ot4YuBXwAPp' -or
            $playback.ProgressMs -lt 5000 -or $playback.Item.ExternalIds['isrc'] -ne $state.isrc) {
            throw 'Hook did not map embedded bridge state.'
        }
    }
    $state.is_playing = $false
    $state.progress_ms = 9000
    if ((Post-State $state).StatusCode -ne 204) { throw 'Pause update was rejected.' }
    $paused = (Invoke-WebRequest "$base/state").Content | ConvertFrom-Json
    if ($paused.progress_ms -ne 9000 -or $paused.is_playing) { throw 'Paused timeline is incorrect.' }
    'Embedded bridge checks passed'
} finally {
    $stop.Invoke($null, @()) | Out-Null
}
