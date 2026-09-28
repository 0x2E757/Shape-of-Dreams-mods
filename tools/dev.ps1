# Calls the DevTools agent API in a running Debug build and prints the JSON answer.
#
#     .\tools\dev.ps1                          # GET /  - every route and its parameters
#     .\tools\dev.ps1 state                    # GET /state
#     .\tools\dev.ps1 entities radius=30       # arguments make it a POST with a JSON body
#     .\tools\dev.ps1 hero/cast slot=Q x=12.5 z=-3
#     .\tools\dev.ps1 flow/start_solo hero=Hero_Lacerta -Timeout 180
#     .\tools\dev.ps1 reflect/call 'path=$hero.Control.CmdStop'   # single quotes: $ is PowerShell's
#
# Arguments are key=value. A value that reads as a number, true/false/null, or starts with [ or {
# is sent as that JSON; anything else as a string. -Get sends them as a query string instead.
#
# The server listens on 127.0.0.1 only and exists only in a Debug build of DevTools; see
# docs/devtools.md.
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Position = 0)][string]$Route = "",
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)][string[]]$Arguments = @(),
    [int]$Port = 47653,
    # Seconds to wait for the game. Also passed to the server, which gives up a moment before.
    [int]$Timeout = 30,
    [switch]$Get
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

function Convert-Value([string]$text) {
    $t = $text.Trim()
    if ($t -match '^-?\d+(\.\d+)?([eE][-+]?\d+)?$' -or $t -in @('true', 'false', 'null') -or $t.StartsWith('[') -or $t.StartsWith('{')) {
        try { return ($t | ConvertFrom-Json) } catch { }
    }
    return $text
}

$body = [ordered]@{}
foreach ($arg in $Arguments) {
    $eq = $arg.IndexOf('=')
    if ($eq -le 0) { throw "Arguments are key=value; got '$arg'" }
    $body[$arg.Substring(0, $eq)] = Convert-Value $arg.Substring($eq + 1)
}
if ($Timeout -ne 30) { $body['timeout'] = $Timeout }

$path = '/' + $Route.TrimStart('/')
$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromSeconds($Timeout + 5)

try {
    if ($body.Count -eq 0 -or $Get) {
        $query = ($body.Keys | ForEach-Object { [Uri]::EscapeDataString($_) + '=' + [Uri]::EscapeDataString([string]$body[$_]) }) -join '&'
        $uri = "http://127.0.0.1:$Port$path" + $(if ($query) { "?$query" } else { "" })
        $response = $client.GetAsync($uri).GetAwaiter().GetResult()
    } else {
        # Depth 20: nested arrays and objects would otherwise be flattened into strings.
        $json = $body | ConvertTo-Json -Depth 20 -Compress
        $content = New-Object System.Net.Http.StringContent($json, [System.Text.Encoding]::UTF8, "application/json")
        $response = $client.PostAsync("http://127.0.0.1:$Port$path", $content).GetAwaiter().GetResult()
    }
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
} catch {
    throw "Nothing answered on 127.0.0.1:$Port. Is the game running with a Debug build of DevTools enabled? ($($_.Exception.InnerException.Message))"
} finally {
    $client.Dispose()
}

Write-Output $text
if (-not $response.IsSuccessStatusCode) { exit 1 }
