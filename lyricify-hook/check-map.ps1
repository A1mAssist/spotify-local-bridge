param(
    [Parameter(Mandatory = $true)]
    [string]$SpotifyApiDll
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$sdkPath = (Resolve-Path -LiteralPath $SpotifyApiDll).Path
$hookPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net6.0/StartupHook.dll')).Path
[System.Runtime.Loader.AssemblyLoadContext]::Default.LoadFromAssemblyPath($sdkPath) | Out-Null
$hook = [System.Runtime.Loader.AssemblyLoadContext]::Default.LoadFromAssemblyPath($hookPath)
$stateType = $hook.GetType('BridgeState', $true)
$state = [Activator]::CreateInstance($stateType)
$stateType.GetProperty('TrackUri').SetValue($state, 'spotify:track:0BxE4FqsDD1Ot4YuBXwAPp')
$stateType.GetProperty('Title').SetValue($state, '505')
$stateType.GetProperty('Artists').SetValue($state, [string[]]@('Arctic Monkeys'))
$stateType.GetProperty('Album').SetValue($state, 'Favourite Worst Nightmare')
$stateType.GetProperty('DurationMs').SetValue($state, 253000)
$stateType.GetProperty('Isrc').SetValue($state, 'USUM71703861')

$method = $hook.GetType('PlaybackPatch').GetMethod('Map', [System.Reflection.BindingFlags]'NonPublic,Static')
$playback = $method.Invoke($null, @($state))
$device = $playback.Device
$track = $playback.Item
if ($null -eq $device -or $device.Name -ne 'Spotify' -or
    $null -eq $track -or $track.Id -ne '0BxE4FqsDD1Ot4YuBXwAPp' -or
    $track.Name -ne '505' -or $track.Artists[0].Name -ne 'Arctic Monkeys' -or
    $track.Album.Name -ne 'Favourite Worst Nightmare' -or $track.DurationMs -ne 253000 -or
    $track.ExternalIds['isrc'] -ne 'USUM71703861') {
    throw 'Playback mapping does not satisfy Lyricify lyric-loading fields.'
}

Write-Output 'Playback map check passed'
