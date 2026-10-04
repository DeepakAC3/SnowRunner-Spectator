$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$bundle = Join-Path $projectRoot 'bin\Spectator'
$helper = Join-Path $bundle 'SnowFlyer2.exe'
if (!(Test-Path -LiteralPath $helper) -or
    !(Test-Path -LiteralPath (Join-Path $bundle 'SpectatorUI.dll'))) {
    throw 'Run tools\build_spectator.ps1 first.'
}
$running = Get-Process SnowFlyer2 -ErrorAction SilentlyContinue
if ($running) {
    throw 'A spectator helper is already running. Close it before launching this build.'
}
Start-Process -FilePath $helper -ArgumentList '--headless', '--ingame-ui' -WorkingDirectory $bundle -WindowStyle Hidden
Write-Output 'Spectator launched. In SnowRunner, press Delete to open or close the in-game menu.'
