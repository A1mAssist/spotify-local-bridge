param(
    [string]$SpotifyApiDll = (Join-Path $env:LOCALAPPDATA 'Lyricify 4\SpotifyAPI.Web.dll')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $SpotifyApiDll -PathType Leaf)) {
    throw "SpotifyAPI.Web.dll not found: $SpotifyApiDll"
}

$hook = Join-Path $PSScriptRoot 'lyricify-hook\StartupHook.csproj'
$payload = Join-Path $PSScriptRoot 'launcher\obj\single-exe-payload'
& dotnet build $hook -c Release "-p:SpotifyApiDll=$SpotifyApiDll" "-p:OutputPath=$payload" `
    '-p:AppendTargetFrameworkToOutputPath=false'
if ($LASTEXITCODE -ne 0) { throw 'StartupHook build failed.' }

$launcher = Join-Path $PSScriptRoot 'launcher\LyricifyLauncher.csproj'
$publish = Join-Path $PSScriptRoot 'launcher\bin\Release\net8.0-windows\win-x64\publish-framework-dependent'
& dotnet publish $launcher -c Release -r win-x64 --self-contained false `
    '-p:PublishSingleFile=true' `
    '-p:DebugType=None' '-p:DebugSymbols=false' "-p:PayloadDirectory=$payload" -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Framework-dependent EXE publish failed.' }

$output = Join-Path $publish 'LyricifyLauncher.exe'
if (-not (Test-Path -LiteralPath $output -PathType Leaf)) { throw 'Published EXE not found.' }
$otherFiles = @(Get-ChildItem -LiteralPath $publish -File | Where-Object Name -ne 'LyricifyLauncher.exe')
if ($otherFiles.Count -ne 0) { throw "Publish produced extra files: $($otherFiles.Name -join ', ')" }

$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$deliverable = Join-Path $dist 'LyricifyLauncher.exe'
Copy-Item -LiteralPath $output -Destination $deliverable -Force
Write-Output "Framework-dependent EXE: $deliverable"
Write-Output "SHA256: $((Get-FileHash -LiteralPath $deliverable -Algorithm SHA256).Hash)"
