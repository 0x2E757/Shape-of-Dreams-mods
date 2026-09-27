# Sends one command to the DevTools command server in a running Debug build and prints the answer.
#
#     .\tools\devcmd.ps1 state
#     .\tools\devcmd.ps1 memory Q St_Q_IncendiaryRounds
#     .\tools\devcmd.ps1 gem Q 0 Gem_R_Frost
#     .\tools\devcmd.ps1 verdict Q
#
# The server listens on 127.0.0.1 only and exists only in a Debug build of DevTools; see
# docs/devtools.md. "help" lists the commands.
[CmdletBinding(PositionalBinding = $false)]
param(
    # Every word of the command; -Port is the only thing taken out of it.
    [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)][string[]]$Command,
    [int]$Port = 47653
)

$ErrorActionPreference = "Stop"

$client = New-Object System.Net.Sockets.TcpClient
try {
    $client.Connect([System.Net.IPAddress]::Loopback, $Port)
} catch {
    throw "Nothing is listening on 127.0.0.1:$Port. Is the game running with a Debug build of DevTools enabled?"
}

try {
    $stream = $client.GetStream()
    $stream.ReadTimeout = 15000
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $bytes = $utf8.GetBytes(($Command -join " ") + "`n")
    $stream.Write($bytes, 0, $bytes.Length)

    $reader = New-Object System.IO.StreamReader($stream, $utf8)
    $reader.ReadToEnd()
} finally {
    $client.Close()
}
