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
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Shape of Dreams",

    # Which folder the game treats as its mod directory. Defaults to the working copy; point it at
    # dist\ to run and publish the staged copies instead - see publish.ps1.
    [string]$ModDir = $PSScriptRoot,

    # Start through Steam rather than by running the exe.
    #
    # Needed to publish. Launching the exe directly leaves SteamAPI_Init() failing, and with no
    # Steam session there is no workshop - the upload button is simply not there. Steam passes the
    # arguments on itself, so -moddir survives without -steamnorestart.
    [switch]$ViaSteam
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $GameDir "Shape of Dreams.exe"
if (-not (Test-Path $exe)) { throw "Game not found at $exe" }

if (-not (Get-Process -Name steam -ErrorAction SilentlyContinue)) {
    Write-Warning "Steam does not appear to be running; the game may fail to initialise."
}

Write-Host "Mod directory: $ModDir" -ForegroundColor Cyan
foreach ($meta in Get-ChildItem -Path $ModDir -Filter metadata.json -Recurse) {
    $m = Get-Content $meta.FullName -Raw | ConvertFrom-Json
    $dll = Join-Path $meta.Directory.Parent.FullName ($m.assemblies[0] -replace '/', '\')
    $state = if (Test-Path $dll) { "built" } else { "NOT BUILT - run build.ps1" }
    Write-Host ("  {0,-16} {1}" -f $m.name, $state)
}

if ($ViaSteam) {
    # Steam's own path, since a game in a secondary library folder is nowhere near steam.exe.
    $steam = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -Name SteamExe -ErrorAction SilentlyContinue).SteamExe
    if (-not $steam) { $steam = "C:\Program Files (x86)\Steam\steam.exe" }
    if (-not (Test-Path $steam)) { throw "Steam not found at $steam" }

    # -applaunch passes the rest through as the game's arguments, and being started by Steam is
    # what gives the game its session - so no -steamnorestart here, and none needed.
    Start-Process -FilePath $steam -ArgumentList @("-applaunch", "2444750", "-moddir", $ModDir)
    Write-Host "Launched through Steam, so the workshop is available." -ForegroundColor Green
}
else {
    Start-Process -FilePath $exe -ArgumentList @("-moddir", $ModDir, "-steamnorestart")
    Write-Host "Launched directly - no Steam session, so no workshop. Use -ViaSteam to publish." -ForegroundColor Yellow
}

Write-Host "Enable the mods in the in-game mod manager, then check the log at:" -ForegroundColor Green
Write-Host "  $env:USERPROFILE\AppData\LocalLow\Lizard Smoothie\Shape of Dreams\Player.log"
