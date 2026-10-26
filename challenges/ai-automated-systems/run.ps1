# Starts Bridge Warden: governance core + operator UI on http://localhost:8000
#   .\run.ps1               demo profile
#   .\run.ps1 -Edge         low-power edge profile
# Opens Warden Control and the main app as their own Chrome app-mode windows
# (dedicated profile, no tabs or address bar). Falls back to the default browser.
# Ctrl+C stops the server.
param([switch]$Edge, [int]$Port = 8000)
$env:PROFILE = if ($Edge) { 'edge' } else { 'demo' }
$backend = Join-Path $PSScriptRoot 'backend'
Set-Location $backend
uv sync -q

$server = Start-Process uv -ArgumentList 'run', 'uvicorn', 'warden.main:app', '--host', '127.0.0.1', '--port', $Port `
    -WorkingDirectory $backend -NoNewWindow -PassThru

# Wait until the server answers, so the windows never load a dead page.
$up = $false
for ($i = 0; $i -lt 60 -and -not $up; $i++) {
    try { Invoke-WebRequest "http://127.0.0.1:$Port/api/summary" -UseBasicParsing -TimeoutSec 1 | Out-Null; $up = $true }
    catch { Start-Sleep -Milliseconds 500 }
}
if (-not $up) { Write-Warning "Server did not start on port $Port"; Stop-Process -Id $server.Id -ErrorAction SilentlyContinue; exit 1 }

$chrome = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
            "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
            "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
$appProfile = Join-Path $env:LOCALAPPDATA 'BridgeWarden\chrome-app'
$windows = @(@{ page = 'control.html'; pos = '0,0' }, @{ page = 'index.html'; pos = '120,60' })
foreach ($w in $windows) {
    $url = "http://127.0.0.1:$Port/$($w.page)"
    if ($chrome) {
        Start-Process $chrome "--user-data-dir=`"$appProfile`" --no-first-run --new-window --app=$url --window-size=1500,950 --window-position=$($w.pos)"
        Start-Sleep -Milliseconds 600
    } else {
        Start-Process $url
    }
}

Write-Host "Bridge Warden running on http://127.0.0.1:$Port  (Ctrl+C to stop)"
try { Wait-Process -Id $server.Id } finally { Stop-Process -Id $server.Id -ErrorAction SilentlyContinue }
