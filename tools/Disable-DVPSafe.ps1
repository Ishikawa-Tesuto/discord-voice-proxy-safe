$ErrorActionPreference = 'Stop'
$base = Join-Path $env:LOCALAPPDATA 'Discord'
foreach ($app in (Get-ChildItem $base -Directory -Filter 'app-*')) {
    $folder = $app.FullName
    foreach ($file in @('DWrite.dll', 'force-proxy-safe.dll')) {
        $target = Join-Path $folder $file
        if (Test-Path $target) { Remove-Item -LiteralPath $target -Force }
    }
    $old = Join-Path $folder 'DWrite.dll.before-dvp-safe.bak'
    if (Test-Path $old) {
        Write-Host "Legacy DWrite backup exists at $old; NOT restoring the crashing proxy automatically."
    }
}
Write-Host 'DVP Safe loader removed. Your proxy.txt configuration was left untouched.'
