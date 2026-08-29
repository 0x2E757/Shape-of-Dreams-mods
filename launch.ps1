# Launches Shape of Dreams pointed at this folder as its mod directory.
#
# -moddir replaces the game's own Mods folder entirely, which keeps these mods out of the
# Steam install under Program Files.
#
# -steamnorestart matters here: without it the game may relaunch itself through Steam, and
# the relaunch would not carry -moddir.
#
# Steam must already be running.

param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Shape of Dreams"
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $GameDir "Shape of Dreams.exe"
if (-not (Test-Path $exe)) { throw "Game not found at $exe" }

if (-not (Get-Process -Name steam -ErrorAction SilentlyContinue)) {
    Write-Warning "Steam does not appear to be running; the game may fail to initialise."
}

Write-Host "Mod directory: $PSScriptRoot" -ForegroundColor Cyan
foreach ($meta in Get-ChildItem -Path $PSScriptRoot -Filter metadata.json -Recurse) {
    $m = Get-Content $meta.FullName -Raw | ConvertFrom-Json
    $dll = Join-Path $meta.Directory.Parent.FullName ($m.assemblies[0] -replace '/', '\')
    $state = if (Test-Path $dll) { "built" } else { "NOT BUILT - run build.ps1" }
    Write-Host ("  {0,-16} {1}" -f $m.name, $state)
}

Start-Process -FilePath $exe -ArgumentList @("-moddir", $PSScriptRoot, "-steamnorestart")
Write-Host "Launched. Enable the mods in the in-game mod manager, then check the log at:" -ForegroundColor Green
Write-Host "  $env:USERPROFILE\AppData\LocalLow\Lizard Smoothie\Shape of Dreams\Player.log"
