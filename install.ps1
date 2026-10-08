param(
    [string]$SpotifyPath = (Join-Path $env:APPDATA 'Spotify'),
    [string]$XpuiArchive,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
if (-not $ValidateOnly -and (Get-Process Spotify -ErrorAction SilentlyContinue)) {
    throw 'Close Spotify before installing the local bridge.'
}

$apps = Join-Path ([IO.Path]::GetFullPath($SpotifyPath)) 'Apps'
if (-not (Test-Path -LiteralPath $apps -PathType Container)) { throw "Spotify Apps directory not found: $apps" }
$live = Join-Path $apps 'xpui'
if (-not $XpuiArchive) { $XpuiArchive = Join-Path $apps 'xpui.spa' }
$archive = (Resolve-Path -LiteralPath $XpuiArchive).Path
if (([IO.Path]::GetExtension($archive)) -ne '.spa') { throw "Expected an xpui.spa archive: $archive" }
if (-not $ValidateOnly -and (Test-Path -LiteralPath (Join-Path $live 'spotify-local-bridge-install.json'))) {
    throw 'The standalone bridge is already installed. Uninstall it before installing again.'
}

$backupRoot = Join-Path $apps '.spotify-local-bridge-backups'
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'
$stage = Join-Path $apps "xpui.local-bridge-stage-$stamp"
$previous = if (Test-Path -LiteralPath $live -PathType Container) {
    Join-Path $backupRoot "xpui-before-$stamp"
} else { $null }
$previousArchive = if ($archive -eq (Join-Path $apps 'xpui.spa')) {
    Join-Path $backupRoot "xpui-before-$stamp.spa"
} else { $null }

try {
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $stage)
    $indexPath = Join-Path $stage 'index.html'
    $bundlePath = Join-Path $stage 'xpui.js'
    if (-not (Test-Path -LiteralPath $indexPath) -or -not (Test-Path -LiteralPath $bundlePath)) {
        throw 'The archive does not contain Spotify XPUI index.html and xpui.js.'
    }

    $utf8 = [Text.UTF8Encoding]::new($false)
    $index = [IO.File]::ReadAllText($indexPath, $utf8)
    $bundle = [IO.File]::ReadAllText($bundlePath, $utf8)
    if ($index.Contains('spicetifyWrapper.js') -or $bundle.Contains('Spicetify._platform')) {
        throw 'The supplied archive is already modified by Spicetify; provide an original xpui.spa.'
    }

    $scriptTag = '<script defer="defer" src="/xpui.js"></script>'
    $anchors = @(
        @{ From = 'i.registerFactory(nq.J,e=>new n$(new Z.XO(e.resolve(ff.VI)),e.resolve(uo.z))),{version:n,container:e,operatingSystem:'; To = 'i.registerFactory(nq.J,e=>new n$(new Z.XO(e.resolve(ff.VI)),e.resolve(uo.z))),window.__spotifyLocalBridgeStart?.(i),{version:n,container:e,operatingSystem:' },
        @{ From = 'u.registerFactory(ed.J,()=>new ep.Q),{version:n,container:e,operatingSystem:'; To = 'u.registerFactory(ed.J,()=>new ep.Q),window.__spotifyLocalBridgeStart?.(u),{version:n,container:e,operatingSystem:' }
    )
    if ($index.Split([string[]]@($scriptTag), [StringSplitOptions]::None).Length -ne 2) {
        throw 'Spotify XPUI script tag changed; no files were installed.'
    }
    foreach ($anchor in $anchors) {
        if ($bundle.Split([string[]]@($anchor.From), [StringSplitOptions]::None).Length -ne 2) {
            throw 'Spotify XPUI bootstrap changed; no files were installed.'
        }
        $bundle = $bundle.Replace($anchor.From, $anchor.To)
    }

    $index = $index.Replace($scriptTag, '<script defer="defer" src="/spotify-local-bridge.js"></script>' + $scriptTag)
    [IO.File]::WriteAllText($indexPath, $index, $utf8)
    [IO.File]::WriteAllText($bundlePath, $bundle, $utf8)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'spotify-local-bridge.js') -Destination (Join-Path $stage 'spotify-local-bridge.js')
    $manifest = @{ PreviousDirectory = $previous; PreviousArchive = $previousArchive }
    [IO.File]::WriteAllText((Join-Path $stage 'spotify-local-bridge-install.json'), ($manifest | ConvertTo-Json), $utf8)

    if ($ValidateOnly) {
        Write-Output 'Original Spotify XPUI patch validated; no Spotify files changed.'
        Remove-Item -LiteralPath $stage -Recurse -Force
        return
    }

    if ($previous) { Move-Item -LiteralPath $live -Destination $previous }
    if ($previousArchive) { Move-Item -LiteralPath $archive -Destination $previousArchive }
    Move-Item -LiteralPath $stage -Destination $live
    Write-Output "Standalone Spotify bridge installed: $live"
    if ($previous) { Write-Output "Previous XPUI preserved: $previous" }
} catch {
    if (-not (Test-Path -LiteralPath $live) -and $previous -and (Test-Path -LiteralPath $previous)) {
        Move-Item -LiteralPath $previous -Destination $live
    }
    if ($previousArchive -and -not (Test-Path -LiteralPath $archive) -and (Test-Path -LiteralPath $previousArchive)) {
        Move-Item -LiteralPath $previousArchive -Destination $archive
    }
    if (Test-Path -LiteralPath $stage) {
        $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
        if (-not $resolvedStage.StartsWith($apps + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove an unexpected stage path: $resolvedStage"
        }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
    throw
}
