# Starts Bridge Warden: governance core + operator UI on loopback (C# host, compiled by Windows PowerShell 5.1).
#   .\run.ps1               demo profile
#   .\run.ps1 -Edge         low-power edge profile
#   .\run.ps1 -Port 8775    exact port (default: the bridge-warden block, 8774-8776)
# Opens Warden Control and the main app as their own Chrome/Edge app-mode windows. Ctrl+C stops the server.
# Same thing as host\Start-BridgeWarden.ps1, kept so existing shortcuts and run.bat still work.
param([switch]$Edge, [int]$Port = 0, [switch]$NoBrowser)
$env:PROFILE = if ($Edge) { 'edge' } else { 'demo' }
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$hostArgs = @('-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'host\Start-BridgeWarden.ps1'))
if ($Port -gt 0) { $hostArgs += @('-Port', $Port) }
if ($NoBrowser) { $hostArgs += '-NoBrowser' }
& $ps @hostArgs
exit $LASTEXITCODE
