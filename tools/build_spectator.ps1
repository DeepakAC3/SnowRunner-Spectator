param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath
if (!$installation) { throw 'Visual Studio Build Tools with MSVC and MSBuild are required.' }
$msbuild = Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe'
$cmake = Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$nativeRoot = Join-Path $projectRoot 'native\SpectatorUI'
$nativeBuild = Join-Path $nativeRoot 'bin'
$bundle = Join-Path $projectRoot 'bin\Spectator'
New-Item -ItemType Directory -Path $bundle -Force | Out-Null
& $cmake -S $nativeRoot -B $nativeBuild -G 'Visual Studio 17 2022' -A x64
if ($LASTEXITCODE) { throw 'Native UI CMake configuration failed.' }
& $cmake --build $nativeBuild --config $Configuration
if ($LASTEXITCODE) { throw 'Native UI build failed.' }
& $msbuild (Join-Path $projectRoot 'SnowFlyer2.sln') /t:Build "/p:Configuration=$Configuration" /p:Platform=x64 "/p:OutputPath=$bundle" /verbosity:minimal
if ($LASTEXITCODE) { throw 'Spectator helper build failed.' }
Copy-Item -LiteralPath (Join-Path $nativeBuild "$Configuration\SpectatorUI.dll") -Destination $bundle
Copy-Item -LiteralPath (Join-Path $nativeRoot 'THIRD_PARTY_NOTICES.md') -Destination $bundle
Write-Output "Built spectator helper and in-game UI in $bundle"
