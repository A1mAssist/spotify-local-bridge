$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('spotify-bridge-storage-check-' + [Guid]::NewGuid().ToString('N'))
$originalLocalAppData = $env:LOCALAPPDATA
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Assert($condition, $message) {
    if (-not $condition) { throw $message }
}

# The scripts check for Spotify by process name; the fixture has no running client.
function Get-Process {
    param($Name)
    if ($Name -ne 'Spotify') { Microsoft.PowerShell.Management\Get-Process $Name }
}

try {
    $env:LOCALAPPDATA = Join-Path $root 'LocalAppData'
    $spotify = Join-Path $root 'Spotify'
    $apps = Join-Path $spotify 'Apps'
    $source = Join-Path $root 'archive-source'
    $live = Join-Path $apps 'xpui'
    New-Item -ItemType Directory -Path $env:LOCALAPPDATA, $source, $live -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $source 'index.html'), '<script defer="defer" src="/xpui.js"></script>')
    [IO.File]::WriteAllText((Join-Path $source 'xpui.js'), @'
i.registerFactory(nq.J,e=>new n$(new Z.XO(e.resolve(ff.VI)),e.resolve(uo.z))),{version:n,container:e,operatingSystem:
u.registerFactory(ed.J,()=>new ep.Q),{version:n,container:e,operatingSystem:
'@)
    [IO.File]::WriteAllText((Join-Path $live 'original.txt'), 'original XPUI')
    $archive = Join-Path $apps 'xpui.spa'
    [IO.Compression.ZipFile]::CreateFromDirectory($source, $archive)

    & (Join-Path $repo 'install.ps1') -SpotifyPath $spotify -XpuiArchive $archive
    $manifest = Get-Content -LiteralPath (Join-Path $live 'spotify-local-bridge-install.json') -Raw | ConvertFrom-Json
    $backupRoot = Join-Path $env:LOCALAPPDATA 'SpotifyLocalBridge\backups'
    Assert ($manifest.PreviousDirectory.StartsWith($backupRoot + [IO.Path]::DirectorySeparatorChar)) 'XPUI backup escaped the data directory.'
    Assert ($manifest.PreviousArchive.StartsWith($backupRoot + [IO.Path]::DirectorySeparatorChar)) 'Archive backup escaped the data directory.'
    Assert (Test-Path -LiteralPath (Join-Path $manifest.PreviousDirectory 'original.txt')) 'Original XPUI backup is missing.'
    & (Join-Path $repo 'uninstall.ps1') -SpotifyPath $spotify
    Assert (Test-Path -LiteralPath (Join-Path $live 'original.txt')) 'Original XPUI was not restored.'
    Assert (Test-Path -LiteralPath $archive) 'Original xpui.spa was not restored.'
    Assert (@(Get-ChildItem -LiteralPath $backupRoot -Directory -Filter 'xpui-standalone-retired-*').Count -eq 1) 'Retired XPUI was not kept in the data directory.'

    $legacySpotify = Join-Path $root 'LegacySpotify'
    $legacyApps = Join-Path $legacySpotify 'Apps'
    $legacyLive = Join-Path $legacyApps 'xpui'
    $legacyBackup = Join-Path $legacyApps '.spotify-local-bridge-backups\xpui-before-old'
    New-Item -ItemType Directory -Path $legacyLive, $legacyBackup -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $legacyBackup 'original.txt'), 'legacy XPUI')
    [IO.File]::WriteAllText((Join-Path $legacyLive 'spotify-local-bridge-install.json'),
        (@{ PreviousDirectory = $legacyBackup; PreviousArchive = $null } | ConvertTo-Json))
    & (Join-Path $repo 'uninstall.ps1') -SpotifyPath $legacySpotify
    Assert (Test-Path -LiteralPath (Join-Path $legacyLive 'original.txt')) 'Legacy backup was not restored.'

    $unsafeSpotify = Join-Path $root 'UnsafeSpotify'
    $unsafeLive = Join-Path $unsafeSpotify 'Apps\xpui'
    New-Item -ItemType Directory -Path $unsafeLive -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $unsafeLive 'spotify-local-bridge-install.json'),
        (@{ PreviousDirectory = (Join-Path $root 'unrelated'); PreviousArchive = $null } | ConvertTo-Json))
    try {
        & (Join-Path $repo 'uninstall.ps1') -SpotifyPath $unsafeSpotify
        throw 'Untrusted manifest path was accepted.'
    } catch {
        Assert ($_.Exception.Message -like 'Unexpected backup path*') 'Unexpected error for an untrusted manifest path.'
    }
    Assert (Test-Path -LiteralPath $unsafeLive) 'Untrusted manifest changed the installation.'
    Write-Output 'Storage checks passed'
} finally {
    $env:LOCALAPPDATA = $originalLocalAppData
    $resolvedRoot = [IO.Path]::GetFullPath($root)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ((Split-Path -Leaf $resolvedRoot) -notlike 'spotify-bridge-storage-check-*' -or
        -not $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove unexpected test directory: $resolvedRoot"
    }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
