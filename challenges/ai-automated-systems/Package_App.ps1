# Packages Bridge Warden into one text file: every app file → .tar.gz → base64 → Bridge_Warden.package
#   .\Package_App.ps1                       writes Bridge_Warden.package next to this script
#   .\Package_App.ps1 -Out D:\x.package     somewhere else
# Never packaged: keys (.env), the local ledger (backend/data), the Python environment and caches, old packages,
# config.json (each machine sets its own Python path) and an unpacked Bridge_Warden folder.
# To unpack: .\Unpack.ps1 (see that script). By hand:
#   [IO.File]::WriteAllBytes("bw.tar.gz", [Convert]::FromBase64String((Get-Content Bridge_Warden.package -Raw)))
#   tar -xzf bw.tar.gz
# Works in Windows PowerShell 5.1 and PowerShell 7. Needs tar.exe (built into Windows 10 1803+).
param([string]$Out = (Join-Path $PSScriptRoot 'Bridge_Warden.package'))
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
# Bridge_Warden is Unpack.ps1's default output folder; config.json is each machine's own Python path.
$skipDirs = @('.venv', 'data', '__pycache__', '.pytest_cache', '.git', 'node_modules', 'Bridge_Warden')
$skipFile = { param($f) $f.Name -eq '.env' -or ($f.Name -like '.env.*' -and $f.Name -ne '.env.example') -or
                        $f.Extension -in '.package', '.gz', '.pyc' -or $f.FullName -eq (Join-Path $root 'config.json') }

$files = Get-ChildItem -Path $root -Recurse -File -Force | Where-Object {
    $rel = $_.FullName.Substring($root.Length + 1)
    -not ($rel.Split('\') | Where-Object { $_ -in $skipDirs }) -and -not (& $skipFile $_)
} | ForEach-Object { $_.FullName.Substring($root.Length + 1).Replace('\', '/') }
if (-not $files) { throw "No files found under $root" }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("bw-package-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $list = Join-Path $tmp 'files.txt'
    $gz = Join-Path $tmp 'Bridge_Warden.tar.gz'
    [IO.File]::WriteAllLines($list, [string[]]$files)        # UTF-8 without BOM, so tar reads the first name right
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path $tar)) { $tar = 'tar' }
    & $tar -czf $gz -C $root -T $list
    if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }

    $bytes = [IO.File]::ReadAllBytes($gz)
    [IO.File]::WriteAllText($Out, [Convert]::ToBase64String($bytes, 'InsertLineBreaks'))
    Write-Host ("Packaged {0} files: {1:N0} KB gzip, {2:N0} KB package -> {3}" -f
        $files.Count, ($bytes.Length / 1KB), ((Get-Item $Out).Length / 1KB), $Out)
} finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
