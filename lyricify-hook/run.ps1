param([Parameter(Mandatory = $true)][string]$LyricifyExe)

$exe = (Resolve-Path -LiteralPath $LyricifyExe).Path
$hook = Join-Path $PSScriptRoot 'bin\Release\net6.0\StartupHook.dll'
if (-not (Test-Path -LiteralPath $hook)) { throw "Build the hook first: $hook" }
$processName = [System.IO.Path]::GetFileNameWithoutExtension($exe)
if (Get-Process -Name $processName -ErrorAction SilentlyContinue) {
    throw "Close Lyricify completely before launching it with the hook."
}
$log = Join-Path ([System.IO.Path]::GetTempPath()) 'spotify-local-bridge-lyricify-hook.log'

$process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -Environment @{
    DOTNET_STARTUP_HOOKS = $hook
    LYRICIFY_HOOK_LOG = $log
} -PassThru
Write-Output "Lyricify PID $($process.Id); hook log: $log"
