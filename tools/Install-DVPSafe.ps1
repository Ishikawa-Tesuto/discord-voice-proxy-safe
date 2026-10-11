# Experimental manual installer (run only after building the patched sources).
param(
    [Parameter(Mandatory=$true)][string]$DWriteDll,
    [Parameter(Mandatory=$true)][string]$ProxyDll,
    [Parameter(Mandatory=$true)][string]$ProxyAddress,
    [Parameter(Mandatory=$true)][int]$ProxyPort
)
$ErrorActionPreference = 'Stop'
if ($ProxyPort -lt 1 -or $ProxyPort -gt 65535) { throw 'Invalid SOCKS5 port' }
$base = Join-Path $env:LOCALAPPDATA 'Discord'
$apps = @(Get-ChildItem $base -Directory -Filter 'app-*' | Where-Object { Test-Path (Join-Path $_.FullName 'Discord.exe') })
if (!$apps) { throw "Discord app-* directory not found under $base" }
foreach ($app in $apps) {
    $folder = $app.FullName
    foreach ($legacy in @('DWrite.dll', 'force-proxy.dll')) {
        $path = Join-Path $folder $legacy
        if (Test-Path $path) {
            $backup = "$path.before-dvp-safe.bak"
            if (!(Test-Path $backup)) { Copy-Item $path $backup }
        }
    }
    $legacyNetwork = Join-Path $folder 'force-proxy.dll'
    if (Test-Path $legacyNetwork) { Remove-Item -LiteralPath $legacyNetwork -Force }
    Copy-Item -LiteralPath $DWriteDll -Destination (Join-Path $folder 'DWrite.dll') -Force
    Copy-Item -LiteralPath $ProxyDll -Destination (Join-Path $folder 'force-proxy-safe.dll') -Force
    # Do not copy credentials or secrets to logs.
    @("SOCKS5_PROXY_ADDRESS=$ProxyAddress", "SOCKS5_PROXY_PORT=$ProxyPort") |
        Set-Content -Path (Join-Path $folder 'proxy.txt') -Encoding ascii
    Write-Host "Installed into $folder"
}
Write-Host "Restart Discord. Diagnostic log: $env:LOCALAPPDATA\DVP-Safe\loader.log"
