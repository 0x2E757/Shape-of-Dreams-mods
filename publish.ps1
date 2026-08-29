# Stages both mods into dist\ in the shape the workshop should receive them.
#
# This exists because the game uploads a mod by handing Steam the mod's whole folder
# (SteamUGC.SetItemContent on ModItem.path). Publishing straight from the working copy would
# therefore send everything in it - including obj\, whose NuGet files carry absolute paths like
# C:\Users\<name>\.nuget\packages\, which would put the Windows account name in a public item.
#
# So the upload gets a copy containing only what the loader reads at runtime: about\ and the
# assembly. Sources and the art in assets\ are not needed there - the sprites are embedded in the
# dll - and they are on GitHub for anyone who wants them.
#
# Release rather than Debug, because there is no reason to ship an unoptimised build with full
# symbols. The staged metadata.json is rewritten to point at the release path.
param(
    [string]$OutDir = (Join-Path $PSScriptRoot "dist")
)

$ErrorActionPreference = "Stop"

& (Join-Path $PSScriptRoot "build.ps1") -Configuration Release
if ($LASTEXITCODE -ne 0) { throw "build failed" }

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }

foreach ($mod in @("AutoCast", "MoreGemSlots")) {
    $source = Join-Path $PSScriptRoot $mod
    $target = Join-Path $OutDir $mod
    $assembly = "bin/Release/netstandard2.1/$mod.dll"

    $dll = Join-Path $source $assembly
    if (-not (Test-Path $dll)) { throw "missing $dll" }

    New-Item -ItemType Directory -Path (Join-Path $target "bin\Release\netstandard2.1") -Force | Out-Null
    Copy-Item $dll (Join-Path $target "bin\Release\netstandard2.1")
    Copy-Item (Join-Path $source "about") $target -Recurse

    # The same layout the loader is known to accept, with only the configuration changed.
    $metadata = Get-Content (Join-Path $source "about\metadata.json") -Raw | ConvertFrom-Json
    $metadata.assemblies = @($assembly)
    $json = $metadata | ConvertTo-Json -Depth 5

    # No BOM: the loader reads these as plain UTF-8.
    [IO.File]::WriteAllText((Join-Path $target "about\metadata.json"), $json,
                            (New-Object System.Text.UTF8Encoding $false))

    $size = (Get-ChildItem $target -Recurse -File | Measure-Object -Property Length -Sum).Sum
    "{0,-14} {1,3} files, {2:N0} KB" -f $mod,
        (Get-ChildItem $target -Recurse -File).Count, ($size / 1KB)
}

""
"Staged in $OutDir. To publish:"
"  .\launch.ps1 -ModDir `"$OutDir`""
"  then in the game: Settings -> Mods -> the mod -> publish"
""
"A new workshop item is created private (the game calls SetItemVisibility with 2), so it stays"
"invisible until you make it public on its Steam page."
