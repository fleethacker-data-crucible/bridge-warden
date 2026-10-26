# Source rules and compile settings for the C# host. Windows PowerShell 5.1. ASCII only.
# Dot-sourced by Start-BridgeWarden.ps1 (every start) and tools\Test-Compile.ps1 (one file set, while porting).
# Pattern: phoenix-lai src\run.ps1 (Test-Locality): framework assemblies only, nothing loaded at run time, and every
# capability that touches the outside is allowed in exactly one named file.

# Framework assemblies the compiler may reference. Nothing else, ever.
$script:BwReferences = @('System.dll', 'System.Core.dll')

$script:BwBanned = [ordered]@{
    'System.Net'    = 'network namespace'; 'HttpClient' = 'network client'; 'WebClient' = 'network client'
    'WebRequest'    = 'network client';   'TcpClient'  = 'network socket'; 'UdpClient' = 'network socket'
    'Dns.'          = 'name lookup';      'Process.Start' = 'launching processes'
    'Assembly.Load' = 'loading assemblies'; 'LoadLibrary' = 'loading native libraries'; 'DllImport' = 'native calls'
    'https://'      = 'URL in source'
}

# The only exceptions, one file each:
#   WebServer.cs  HttpListener on 127.0.0.1 / localhost (the UI and the FleetHacker endpoints)
#   LlmClient.cs  outbound HTTP to the configured OpenAI-compatible /v1 server (local phoenix-lai by default)
#   Ledger.cs     the SQLite built into Windows (winsqlite3.dll), for the audit ledger
$script:BwAllowed = @{
    'WebServer.cs' = @('System.Net')
    'LlmClient.cs' = @('System.Net', 'WebRequest', 'https://')
    'Ledger.cs'    = @('DllImport')
}
# Native DLLs Ledger.cs may name (anything else in a DllImport fails the rules).
$script:BwDllAllowlist = @('winsqlite3.dll')

function Test-BwSourceRules {
    param([string[]]$Files, [string]$AppRoot)
    $fail = New-Object System.Collections.Generic.List[string]
    if (-not $Files -or $Files.Count -eq 0) { $fail.Add('No C# sources to compile'); return $fail }
    foreach ($path in $Files) {
        $name = [IO.Path]::GetFileName($path)
        $allowed = if ($script:BwAllowed.ContainsKey($name)) { $script:BwAllowed[$name] } else { @() }
        $lines = @(Get-Content -LiteralPath $path)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            if ($line -match '[^\x00-\x7F]') { $fail.Add("${name}:$($i + 1): non-ASCII character") }
            if ($line -match '^\s*//') { continue }     # comments may name what is banned
            $code = ($line -replace '//.*$', '')          # trailing comments too
            foreach ($p in $script:BwBanned.Keys) {
                if ($allowed -contains $p) { continue }
                if ($code.IndexOf($p, [StringComparison]::Ordinal) -ge 0) { $fail.Add("${name}:$($i + 1): $($script:BwBanned[$p]) ($p)") }
            }
            if ($code -match 'DllImport\s*\(\s*"([^"]+)"' -and $script:BwDllAllowlist -notcontains $Matches[1]) {
                $fail.Add("${name}:$($i + 1): native DLL not on the allowlist ($($Matches[1]))")
            }
        }
    }
    if ($AppRoot) {
        $bins = @(Get-ChildItem $AppRoot -Recurse -File -Include *.dll, *.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(\.venv|temp|\.git)\\' })
        foreach ($b in $bins) { $fail.Add("Binary in app folder: $($b.FullName)") }
    }
    return $fail
}

function New-BwCompilerParameters {
    $cp = New-Object System.CodeDom.Compiler.CompilerParameters
    $cp.GenerateInMemory = $true
    $cp.CompilerOptions = '/optimize+'
    $script:BwReferences | ForEach-Object { [void]$cp.ReferencedAssemblies.Add($_) }
    return $cp
}
