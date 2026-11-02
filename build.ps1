# Builds everything and packages the installer:
#   build\               compiled mods and installer
#   dist\SunLoader-Setup.zip   what players download
#
# Needs: the .NET SDK (6 or newer) and the game with BepInEx run once (for BepInEx\interop).
#
#   .\build.ps1                                   # game in the default Steam folder
#   .\build.ps1 -GameDir "D:\Games\Just Shapes & Beats"
#   .\build.ps1 -Deploy                           # also copy the mods into your game while building
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Just Shapes & Beats",
    [switch]$Deploy
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = Join-Path $root 'build'
$dist = Join-Path $root 'dist'

if (-not (Test-Path (Join-Path $GameDir 'BepInEx\interop\Assembly-CSharp.dll'))) {
    throw "No BepInEx\interop in '$GameDir'. Install BepInEx (or run the Sun Loader installer), start the game once, then build again. Use -GameDir if the game is somewhere else."
}

function Build($project, $name) {
    Write-Host "Building $name..." -ForegroundColor Cyan
    dotnet build (Join-Path $root $project) -c Release -nologo -v q -o (Join-Path $build $name) "-p:GameDir=$GameDir" "-p:Deploy=$($Deploy.IsPresent.ToString().ToLower())"
    if ($LASTEXITCODE -ne 0) { throw "$name failed to build" }
}

Build 'mod\SunLoader\SunLoader.csproj' 'SunLoader'
Build 'mod\NoIntro\NoIntro.csproj' 'NoIntro'
Build 'mod\ExampleMod\ExampleMod.csproj' 'ExampleMod'

# The installer ships the mods as plain files next to it (see mod\Installer\Installer.cs).
$payload = Join-Path $root 'mod\Installer\payload'
New-Item -ItemType Directory -Force $payload | Out-Null
Copy-Item (Join-Path $build 'SunLoader\SunLoader.dll'), (Join-Path $build 'NoIntro\NoIntro.dll'), (Join-Path $build 'ExampleMod\ExampleMod.dll') $payload -Force
# The version the installer compares with GitHub's newest release.
$version = [regex]::Match((Get-Content -Raw (Join-Path $root 'mod\SunLoader\Plugin.cs')), 'const string Version = "([^"]+)"').Groups[1].Value
Set-Content (Join-Path $payload 'version.txt') $version -NoNewline -Encoding ascii
Build 'mod\Installer\SunLoaderInstaller.csproj' 'Installer'

# The official BepInEx package the installer unpacks. Downloaded here (not stored in git) and
# checked against the same SHA-256 the installer checks.
$bepinexUrl = 'https://builds.bepinex.dev/projects/bepinex_be/791/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.791%2Bb782da4.zip'
$bepinexSha = 'BB1C510909B69522733791F3A0817A487379A52DA6468A492F1C9C19B3EC6F9A'
$bepinex = Join-Path $build 'cache\BepInEx.zip'
if (-not (Test-Path $bepinex) -or (Get-FileHash $bepinex -Algorithm SHA256).Hash -ne $bepinexSha) {
    Write-Host 'Downloading BepInEx...' -ForegroundColor Cyan
    New-Item -ItemType Directory -Force (Split-Path $bepinex) | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest $bepinexUrl -OutFile $bepinex -UseBasicParsing
    if ((Get-FileHash $bepinex -Algorithm SHA256).Hash -ne $bepinexSha) { throw 'BepInEx download checksum mismatch' }
}

Write-Host 'Packaging...' -ForegroundColor Cyan
$stage = Join-Path $build 'setup\SunLoader-Setup'
if (Test-Path (Split-Path $stage)) { Remove-Item (Split-Path $stage) -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage 'files') | Out-Null
Copy-Item (Join-Path $build 'Installer\SunLoaderInstaller.exe'), (Join-Path $build 'Installer\SunLoaderInstaller.exe.config') $stage
Copy-Item (Join-Path $root 'mod\Installer\SETUP-README.txt') (Join-Path $stage 'README.txt')
Copy-Item (Join-Path $payload '*.dll'), (Join-Path $payload 'version.txt') (Join-Path $stage 'files')
Copy-Item $bepinex (Join-Path $stage 'files\BepInEx.zip')
Copy-Item (Join-Path $root 'mod\Installer\BepInEx-LICENSE.txt') (Join-Path $stage 'files\BepInEx-LICENSE.txt')

New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist 'SunLoader-Setup.zip'
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory((Split-Path $stage), $zip)
# The mod DLLs on their own, to attach to the GitHub release: the installer's update check
# downloads them from there.
Copy-Item (Join-Path $payload '*.dll') $dist -Force
Write-Host "Done: $zip (attach it and dist\*.dll to the release, tagged v$version)" -ForegroundColor Green
