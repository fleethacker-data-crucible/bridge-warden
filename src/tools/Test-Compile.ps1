# Compile-checks a set of C# files with the host's rules and compiler settings, in a separate process, so files still
# being ported never break anyone else. Windows PowerShell 5.1. ASCII only.
#   powershell -NoProfile -ExecutionPolicy Bypass -File host\tools\Test-Compile.ps1 Json.cs Py.cs Models.cs World.cs
#   powershell -NoProfile -ExecutionPolicy Bypass -File host\tools\Test-Compile.ps1 -All
# Names are files in host\src. Prints "OK" or every rule failure / compiler error with file and line; exit 0 / 1.
param([switch]$All, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Files)
$ErrorActionPreference = 'Stop'
$hostDir = Split-Path -Parent $PSScriptRoot
$src = Join-Path $hostDir 'src'
. (Join-Path $hostDir 'Build.ps1')

$paths = if ($All -or -not $Files) { @(Get-ChildItem $src -Recurse -Filter *.cs | ForEach-Object { $_.FullName }) }
         else { @($Files | ForEach-Object { if (Test-Path $_) { (Resolve-Path $_).Path } else { Join-Path $src $_ } }) }
$missing = @($paths | Where-Object { -not (Test-Path $_) })
if ($missing) { Write-Host "Missing: $($missing -join ', ')"; exit 1 }

$fail = Test-BwSourceRules -Files $paths
if ($fail.Count) { $fail | ForEach-Object { Write-Host "RULE  $_" }; exit 1 }

$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
$cp = New-BwCompilerParameters
$cp.GenerateInMemory = $false
$cp.OutputAssembly = Join-Path ([IO.Path]::GetTempPath()) ("bw-check-" + [guid]::NewGuid().ToString('N') + '.dll')
$r = $provider.CompileAssemblyFromFile($cp, [string[]]$paths)
Remove-Item -LiteralPath $cp.OutputAssembly -ErrorAction SilentlyContinue
$errs = @($r.Errors | Where-Object { -not $_.IsWarning })
if ($errs.Count) {
    foreach ($e in $errs) { Write-Host ("ERROR {0}({1},{2}): {3} {4}" -f [IO.Path]::GetFileName($e.FileName), $e.Line, $e.Column, $e.ErrorNumber, $e.ErrorText) }
    Write-Host "$($errs.Count) error(s)"
    exit 1
}
Write-Host "OK ($($paths.Count) files)"
exit 0
