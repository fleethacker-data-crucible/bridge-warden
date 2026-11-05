# Starts Bridge Warden: governance core + operator UI on loopback (C# host, compiled by Windows PowerShell 5.1).
# Needs nothing installed: Windows PowerShell 5.1 and Chrome or Edge, both built into Windows.
#   .\run.ps1               demo profile
#   .\run.ps1 -Edge         low-power edge profile
#   .\run.ps1 -Port 8775    exact port (default: the bridge-warden block, 8774-8775)
# Opens Warden Control and the main app as their own Chrome/Edge app-mode windows. Ctrl+C stops the server.
# Same thing as src\Start-BridgeWarden.ps1, kept so existing shortcuts and Launch_Bridge_Warden.bat still work.
param([switch]$Edge, [int]$Port = 0, [switch]$NoBrowser)
$hostScript = Join-Path $PSScriptRoot 'Start-BridgeWarden.ps1'
if (-not (Test-Path $hostScript)) {
    Write-Host -ForegroundColor Red ("src\Start-BridgeWarden.ps1 is missing: this is an old (Python) copy of Bridge Warden.`n" +
        "Unpack a new Bridge_Warden.package into an EMPTY folder and run Launch_Bridge_Warden.bat there.")
    exit 1
}
# Unpacking over an old copy leaves the Python backend behind. It isn't used, but say so, since it confuses people.
if (Test-Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'backend\warden')) {
    Write-Host -ForegroundColor Yellow "Note: backend\warden (the old Python backend) is still here. It isn't used; delete it, or unpack into an empty folder."
}
$env:PROFILE = if ($Edge) { 'edge' } else { 'demo' }
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$hostArgs = @('-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-File', $hostScript)
if ($Port -gt 0) { $hostArgs += @('-Port', $Port) }
if ($NoBrowser) { $hostArgs += '-NoBrowser' }
& $ps @hostArgs
$code = $LASTEXITCODE
if ($code -ne 0) {
    $log = Get-ChildItem (Join-Path (Split-Path -Parent $PSScriptRoot) 'logs') -Filter 'BridgeWarden_*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($log) { Write-Host "Details: $($log.FullName)" }
}
exit $code
