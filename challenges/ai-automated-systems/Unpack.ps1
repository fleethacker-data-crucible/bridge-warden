# Unpacks Bridge_Warden.package (made by Package_App.ps1): base64 → .tar.gz → every file and folder, as packaged.
#   .\Unpack.ps1                                   Bridge_Warden.package next to this script → .\Bridge_Warden
#   .\Unpack.ps1 -Dest D:\apps\bw                  somewhere else
#   .\Unpack.ps1 -Python C:\Python312\python.exe   set the Python to run with
#   .\Unpack.ps1 -InstallDeps                      also pip-install the app's packages into that Python (needs network once)
# Writes config.json in the app folder: { "python": "<full path to python.exe>" }. run.ps1 runs with that Python;
# edit the path there to switch. A blank path means run.ps1 uses uv instead.
# An existing config.json is kept, so re-unpacking over an install never loses your path.
# Works in Windows PowerShell 5.1 and PowerShell 7. Needs tar.exe (built into Windows 10 1803+).
param(
    [string]$Package = (Join-Path $PSScriptRoot 'Bridge_Warden.package'),
    [string]$Dest = (Join-Path $PSScriptRoot 'Bridge_Warden'),
    [string]$Python = '',
    [switch]$InstallDeps
)
$ErrorActionPreference = 'Stop'

function Find-Python {
    # Python 3.12+ only. Skips the Microsoft Store "python.exe" stub, which isn't a real install.
    $ErrorActionPreference = 'Continue'   # probing: a missing or broken Python just writes to stderr
    $candidates = @()
    if (Get-Command py -ErrorAction SilentlyContinue) {
        $candidates += (& py -3 -c "import sys; print(sys.executable)" 2>$null)
    }
    $candidates += Get-Command python, python3 -All -ErrorAction SilentlyContinue |
        Where-Object { $_.Source -notlike '*\WindowsApps\*' } | ForEach-Object Source
    $candidates += Get-ChildItem "$env:APPDATA\uv\python\cpython-3.1[2-9]*\python.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | ForEach-Object FullName
    foreach ($c in $candidates | Where-Object { $_ } | Select-Object -Unique) {
        & $c -c "import sys; sys.exit(0 if sys.version_info >= (3, 12) else 1)" 2>$null
        if ($LASTEXITCODE -eq 0) { return $c }
    }
    return ''
}

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

# config.json: the Python run.ps1 uses. Precedence: -Python, then the existing file, then auto-detect.
$config = Join-Path $Dest 'config.json'
$current = if (Test-Path $config) { (Get-Content $config -Raw | ConvertFrom-Json).python } else { $null }
if ($Python) { $py = (Resolve-Path $Python).Path }
elseif ($null -ne $current) { $py = $current }
else { $py = Find-Python }
if ($Python -or $null -eq $current) {
    [IO.File]::WriteAllText($config, (@{ python = $py } | ConvertTo-Json))
}
if ($py) { Write-Host "config.json python: $py" }
else { Write-Warning "No Python 3.12+ found. Put the full path to python.exe in $config (or leave it blank to use uv)." }

if ($InstallDeps) {
    $ErrorActionPreference = 'Continue'   # pip writes progress to stderr; exit codes are checked instead
    if (-not $py) { throw "-InstallDeps needs a Python: pass -Python or set it in config.json" }
    # The app's packages, read from pyproject.toml so this list never goes stale.
    $toml = Get-Content (Join-Path $Dest 'backend\pyproject.toml') -Raw
    $deps = [regex]::Match($toml, '(?s)\ndependencies\s*=\s*\[(.*?)\]').Groups[1].Value |
        Select-String -Pattern '"([^"]+)"' -AllMatches | ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value }
    & $py -m pip --version 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { & $py -m ensurepip --upgrade }     # some Pythons (embeddable, bare venvs) ship without pip
    & $py -m pip install @deps
    if ($LASTEXITCODE -ne 0) { throw "pip install failed" }
}

Write-Host "Start it with: & '$(Join-Path $Dest 'run.ps1')'"
