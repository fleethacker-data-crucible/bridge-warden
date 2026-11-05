# Unpacks Bridge_Warden.package (made by Package_App.ps1): base64 -> .tar.gz -> every file and folder, as packaged.
#   .\Unpack.ps1                     Bridge_Warden.package next to Unpack.bat -> .\Bridge_Warden
#   .\Unpack.ps1 -Dest D:\apps\bw    somewhere else
# Nothing to install: Windows PowerShell 5.1 compiles the C# host on start (src\Start-BridgeWarden.ps1).
# Works in Windows PowerShell 5.1 and PowerShell 7. Needs tar.exe (built into Windows 10 1803+).
param(
    [string]$Package = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Bridge_Warden.package'),
    [string]$Dest = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Bridge_Warden')
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Package)) { throw "Package not found: $Package" }
New-Item -ItemType Directory -Force -Path $Dest | Out-Null
$Dest = (Resolve-Path $Dest).Path

$gz = Join-Path ([IO.Path]::GetTempPath()) ("bw-unpack-" + [guid]::NewGuid().ToString('N') + '.tar.gz')
try {
    # Base64 decoding ignores the line breaks Package_App.ps1 inserts.
    [IO.File]::WriteAllBytes($gz, [Convert]::FromBase64String((Get-Content $Package -Raw)))
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path $tar)) { $tar = 'tar' }
    & $tar -xzf $gz -C $Dest
    if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
} finally {
    Remove-Item -Force $gz -ErrorAction SilentlyContinue
}
$count = (Get-ChildItem $Dest -Recurse -File).Count
Write-Host "Unpacked to $Dest ($count files)"

# Keys never travel in the package: start from the example settings (no keys in it).
$envFile = Join-Path $Dest 'backend\.env'
if (-not (Test-Path $envFile) -and (Test-Path "$envFile.example")) { Copy-Item "$envFile.example" $envFile }

Write-Host "Start it with: $(Join-Path $Dest 'Launch_Bridge_Warden.bat')"
