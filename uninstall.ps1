param([string]$SpotifyPath = (Join-Path $env:APPDATA 'Spotify'))

$ErrorActionPreference = 'Stop'
if (Get-Process Spotify -ErrorAction SilentlyContinue) {
    throw 'Close Spotify before uninstalling the local bridge.'
}

$apps = Join-Path ([IO.Path]::GetFullPath($SpotifyPath)) 'Apps'
$live = Join-Path $apps 'xpui'
$manifestPath = Join-Path $live 'spotify-local-bridge-install.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'Standalone bridge installation not found.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$backupRoot = Join-Path (Join-Path $env:LOCALAPPDATA 'SpotifyLocalBridge') 'backups'
$legacyBackupRoot = Join-Path $apps '.spotify-local-bridge-backups'
foreach ($path in @($manifest.PreviousDirectory, $manifest.PreviousArchive)) {
    $resolved = if ($path) { [IO.Path]::GetFullPath($path) } else { $null }
    if ($resolved -and -not (
        $resolved.StartsWith($backupRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $resolved.StartsWith($legacyBackupRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) {
        throw "Unexpected backup path in manifest: $path"
    }
}
if ($manifest.PreviousDirectory -and -not (Test-Path -LiteralPath $manifest.PreviousDirectory)) {
    throw 'Previous XPUI backup is missing; leaving the installation intact.'
}
if ($manifest.PreviousArchive -and -not (Test-Path -LiteralPath $manifest.PreviousArchive)) {
    throw 'Previous xpui.spa backup is missing; leaving the installation intact.'
}
if ($manifest.PreviousArchive -and (Test-Path -LiteralPath (Join-Path $apps 'xpui.spa'))) {
    throw 'Spotify created a new xpui.spa; leaving both it and the installation intact.'
}

$retired = Join-Path $backupRoot ("xpui-standalone-retired-" + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
Move-Item -LiteralPath $live -Destination $retired
if ($manifest.PreviousDirectory) { Move-Item -LiteralPath $manifest.PreviousDirectory -Destination $live }
if ($manifest.PreviousArchive) { Move-Item -LiteralPath $manifest.PreviousArchive -Destination (Join-Path $apps 'xpui.spa') }
Write-Output 'Previous Spotify XPUI restored.'
Write-Output "Standalone XPUI retained at: $retired"
