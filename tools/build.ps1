# Builds every mod in this folder.
#
# Prefers a normally installed SDK on PATH, and falls back to a user-local one - this project was
# set up with the SDK unpacked into the user profile rather than installed, so that nothing on the
# machine outside that folder had to change.
param(
    [ValidateSet("Debug", "Release")][string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

# These scripts live in tools but act on the repository, which is its parent.
$repo = Split-Path $PSScriptRoot -Parent

# Finding dotnet.exe is not the same as finding an SDK: a machine with only the runtime installed
# has the shared host on PATH, and it answers "No .NET SDKs were found" to every build. So each
# candidate is asked what SDKs it has, and the first one that names any wins.
function Find-Sdk([string[]]$candidates) {
    foreach ($path in $candidates) {
        if (-not $path -or -not (Test-Path $path)) { continue }
        $sdks = & $path --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) { return $path }
    }
    return $null
}

$dotnet = Find-Sdk @(
    (Get-Command dotnet -ErrorAction SilentlyContinue).Source,
    (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe")
)

if (-not $dotnet) {
    throw "No .NET SDK on PATH or at `"$env:USERPROFILE\.dotnet\dotnet.exe`". Install one from https://dotnet.microsoft.com/download, or unpack it with dotnet-install.ps1 -InstallDir `"$env:USERPROFILE\.dotnet`"."
}

$failed = @()
foreach ($proj in Get-ChildItem -Path $repo -Filter *.csproj -Recurse) {
    Write-Host "=== $($proj.BaseName) ===" -ForegroundColor Cyan
    & $dotnet build $proj.FullName -c $Configuration -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { $failed += $proj.BaseName }
}

if ($failed.Count) {
    Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "All mods built ($Configuration)." -ForegroundColor Green
