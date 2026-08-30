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
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) "dist")
)

$ErrorActionPreference = "Stop"

# These scripts live in tools but act on the repository, which is its parent.
$repo = Split-Path $PSScriptRoot -Parent

# Named rather than discovered, because the list of mods and the list of things to publish are not
# the same list: DevTools is a testing tool for the other two and has no business on the workshop.
# build.ps1 and launch.ps1 do discover, which is right for them - it is meant to be built and run,
# just not shipped.
$publish = @("AutoCast", "MoreGemSlots")

& (Join-Path $PSScriptRoot "build.ps1") -Configuration Release
if ($LASTEXITCODE -ne 0) { throw "build failed" }

# On a first upload the game asks Steam for a new item id and writes it to
# about\publishedfileid.txt inside the folder it published; on later uploads it reads that file
# back and updates the existing item instead of creating another one. That file therefore appears
# in dist\ rather than here, and wiping dist\ would lose it - the next upload would then publish a
# duplicate. So it is carried back into the working copy first, where it belongs and where git
# keeps it.
foreach ($mod in $publish) {
    $published = Join-Path $OutDir "$mod\about\publishedfileid.txt"
    $keep = Join-Path $repo "mods\$mod\about\publishedfileid.txt"
    if ((Test-Path $published) -and -not (Test-Path $keep)) {
        Copy-Item $published $keep
        "$mod - kept workshop id $(Get-Content $published -Raw)"
    }
}

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }

foreach ($mod in $publish) {
    $source = Join-Path $repo "mods\$mod"
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
