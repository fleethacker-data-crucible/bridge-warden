# Bridge Warden C# host. Windows PowerShell 5.1 only. ASCII only (PS 5.1 reads BOM-less .ps1 as ANSI).
# Compiles host\src\*.cs with Add-Type (inbox .NET Framework compiler, C# 5, framework assemblies only) and serves
# the operator UI on loopback. Pattern: edge-ops templates\host\Start-ToolName.ps1 and phoenix-lai src\run.ps1.
#   powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File host\Start-BridgeWarden.ps1 [-Port n] [-NoBrowser] [-CheckOnly]
#   -Port n      use exactly port n (no walking); default: the bridge-warden block in FleetPorts.psd1 (8774-8775; 8776 is Fairlead)
#   -NoBrowser   don't open the windows (tests, CI)
#   -CheckOnly   source rules + compile, then exit 0 (CI)
param([int]$Port = 0, [switch]$NoBrowser, [switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path -Parent $here

# Logs: <root>\logs\BridgeWarden_yyyy_MM_dd_HHmmss.log, one file per run.
$logFile = Join-Path $root ('logs\BridgeWarden_{0:yyyy_MM_dd_HHmmss}.log' -f (Get-Date))
New-Item -ItemType Directory -Force (Split-Path $logFile) | Out-Null
function Add-Log([string]$level, [string]$text) {
    try { [IO.File]::AppendAllText($logFile, "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  [$level] $text`r`n") } catch { }
    Write-Host "[$level] $text"
}
# Started hidden from a shortcut, a fatal error must still be seen: log + message box (tests set BW_NO_MESSAGEBOX=1).
function Show-Fatal([string]$text) {
    Add-Log 'FATAL' $text
    if ($env:BW_NO_MESSAGEBOX -eq '1') { return }
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [void][System.Windows.Forms.MessageBox]::Show("Bridge Warden could not start.`n`n$text`n`nLog: $logFile", 'Bridge Warden')
    } catch { }
}
trap { Show-Fatal "$($_.Exception.Message) (line $($_.InvocationInfo.ScriptLineNumber))"; exit 1 }

if ($PSVersionTable.PSVersion.Major -ne 5) { throw "Run with Windows PowerShell 5.1 (powershell.exe), not $($PSVersionTable.PSVersion)." }

# Add-Type scratch and .NET temp files stay inside the app folder.
$env:TEMP = Join-Path $root 'temp\sys'; $env:TMP = $env:TEMP
New-Item -ItemType Directory -Force $env:TEMP | Out-Null

$version = ([string](Get-Content -LiteralPath (Join-Path $root 'VERSION') -TotalCount 1 -ErrorAction SilentlyContinue)).Trim()
if (-not $version) { $version = '0.0' }
$src = Join-Path $here 'src'
$webRoot = Join-Path $root 'frontend'

# ---------------------------------------------------------------- source rules (checked before every compile)
# Rules (Build.ps1): framework assemblies only, nothing loaded at run time, ASCII only, and network / native access
# in exactly one named file each.
. (Join-Path $here 'Build.ps1')
$sources = @(Get-ChildItem $src -Recurse -Filter *.cs | ForEach-Object { $_.FullName })
$failures = Test-BwSourceRules -Files $sources -AppRoot $root
if ($failures.Count) { throw ("Source rules failed:`n  " + ($failures -join "`n  ")) }

# ---------------------------------------------------------------- compile
$sw = [Diagnostics.Stopwatch]::StartNew()
Add-Type -Path $sources -CompilerParameters (New-BwCompilerParameters)
Add-Log 'sys' "Compiled host\src in $($sw.ElapsedMilliseconds) ms (Bridge Warden $version)"
if ($CheckOnly) { Add-Log 'sys' 'Self-check passed.'; exit 0 }

# ---------------------------------------------------------------- port: FleetHacker block + self-detection
Import-Module (Join-Path $here 'FleetPort.psm1') -Force
$fleetArgs = @{ Tool = 'bridge-warden'; Version = $version; NoListener = $true; RegistryPath = (Join-Path $here 'FleetPorts.psd1') }
if ($Port -gt 0) { $fleetArgs.Start = $Port; $fleetArgs.Size = 1 }
$fleet = Resolve-FleetPort @fleetArgs
if ($fleet.Note) { Add-Log 'port' $fleet.Note }

function Open-Windows([int]$p) {
    if ($NoBrowser) { return }
    $chrome = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
                "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
                "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe",
                "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
                "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    $profileDir = Join-Path $env:LOCALAPPDATA 'BridgeWarden\browser-app'
    foreach ($w in @(@{ page = 'control.html'; pos = '0,0' }, @{ page = 'index.html'; pos = '120,60' })) {
        $url = "http://127.0.0.1:$p/$($w.page)"
        if ($chrome) {
            Start-Process $chrome "--user-data-dir=`"$profileDir`" --no-first-run --new-window --app=$url --window-size=1500,950 --window-position=$($w.pos)"
            Start-Sleep -Milliseconds 600
        } else { Start-Process $url }
    }
}

if ($fleet.Action -eq 'Reuse') {
    Add-Log 'port' "Bridge Warden is already running on port $($fleet.Port); opening it."
    Open-Windows $fleet.Port
    exit 0
}

# ---------------------------------------------------------------- serve
# App = the routes, world, Warden and tick loop (port of warden\main.py). Config, scenarios and skills come from
# backend\ for now. Settings and keys: backend\.env (see backend\.env.example).
[BridgeWarden.Paths]::Root = $root
$app = New-Object BridgeWarden.App
$app.Start()
$server = New-Object BridgeWarden.WebServer($webRoot, $app, $version, [int]$fleet.Port)
$server.LogFile = $logFile
Add-Log 'sys' "Bridge Warden $version serving http://127.0.0.1:$($server.Port)/ (Ctrl+C or /__fleethacker/shutdown stops it)"
$pidFile = Join-Path $here '.pid'
[IO.File]::WriteAllText($pidFile, [string]$PID)
try {
    Open-Windows $server.Port
    while (-not $server.ShutdownRequested) { Start-Sleep -Milliseconds 250 }
    Start-Sleep -Milliseconds 300     # let the shutdown reply reach the launcher that asked
} finally {
    $server.Dispose()
    $app.Stop()
    Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
    Add-Log 'sys' 'Stopped.'
}
