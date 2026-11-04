# Pester 5 tests for the C# host over HTTP (the full API is ported). Windows PowerShell 5.1. ASCII only.
#   Invoke-Pester host\tests
# Starts the real host on CI-only port 18774 (edge-ops PORTS.md reserves 18000-18999 for CI) with -NoBrowser.

BeforeAll {
    $script:App = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $script:Host1 = Join-Path $App 'host\Start-BridgeWarden.ps1'
    $script:Port = 18774
    $script:Base = "http://127.0.0.1:$Port"
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $env:BW_NO_MESSAGEBOX = '1'
    $script:Proc = Start-Process $ps -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$Host1`"",
        '-Port', $Port, '-NoBrowser' -PassThru -WindowStyle Hidden

    # Raw request with any Host/Origin header (Invoke-WebRequest won't send a foreign Host).
    function script:Get-Status([string]$Path, [hashtable]$Headers = @{}, [string]$Method = 'GET') {
        $req = [Net.HttpWebRequest]::Create("$Base$Path")
        $req.Method = $Method
        $req.Timeout = 3000
        foreach ($k in $Headers.Keys) { if ($k -eq 'Host') { $req.Host = $Headers[$k] } else { $req.Headers[$k] = $Headers[$k] } }
        if ($Method -eq 'POST') { $req.ContentLength = 0 }
        try { $r = $req.GetResponse() } catch [Net.WebException] { $r = $_.Exception.Response; if (-not $r) { throw } }
        $code = [int]$r.StatusCode
        $body = (New-Object IO.StreamReader($r.GetResponseStream())).ReadToEnd()
        $r.Close()
        [pscustomobject]@{ Code = $code; Body = $body }
    }

    $deadline = (Get-Date).AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 300
        try { $up = (Get-Status '/__fleethacker/id').Code -eq 200 } catch { $up = $false }
    } until ($up -or (Get-Date) -gt $deadline -or $Proc.HasExited)
    if (-not $up) { throw "Host did not start on port $Port" }
}

AfterAll {
    if ($Proc -and -not $Proc.HasExited) { Stop-Process -Id $Proc.Id -Force -ErrorAction SilentlyContinue }
}

Describe 'Start-BridgeWarden.ps1 -CheckOnly' {
    It 'passes the source rules and compiles' {
        $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        & $ps -NoProfile -ExecutionPolicy Bypass -File $Host1 -CheckOnly | Out-Null
        $LASTEXITCODE | Should -Be 0
    }
}

Describe 'FleetHacker self-detection' {
    It 'answers /__fleethacker/id with the tool, version and pid' {
        $id = (Get-Status '/__fleethacker/id').Body | ConvertFrom-Json
        $id.tool | Should -Be 'bridge-warden'
        $id.version | Should -Be ((Get-Content (Join-Path $App 'VERSION') -TotalCount 1).Trim())
        $id.pid | Should -Be $Proc.Id
    }
    It 'answers on localhost as well as 127.0.0.1' {
        (Get-Status '/__fleethacker/id' @{ Host = "localhost:$Port" }).Code | Should -Be 200
    }
    It 'refuses shutdown without the X-FleetHacker-Shutdown header' {
        (Get-Status '/__fleethacker/shutdown' @{} 'POST').Code | Should -Be 405
    }
}

Describe 'Request guard' {
    It 'rejects a foreign Host header' {
        (Get-Status '/api/state' @{ Host = "evil.example:$Port" }).Code | Should -Be 403
    }
    It 'rejects a foreign Origin header' {
        (Get-Status '/api/state' @{ Origin = 'http://evil.example' }).Code | Should -Be 403
    }
    It 'accepts its own origin' {
        (Get-Status '/api/state' @{ Origin = $Base }).Code | Should -Be 200
    }
    It 'never serves files outside frontend' {
        (Get-Status '/%2e%2e/host/Start-BridgeWarden.ps1').Code | Should -Not -Be 200
        (Get-Status '/..%5chost%5cStart-BridgeWarden.ps1').Code | Should -Not -Be 200
    }
}

Describe 'API routes' {
    It 'serves the UI pages' {
        $r = Get-Status '/'
        $r.Code | Should -Be 200
        $r.Body | Should -Match '<title>Bridge Warden'
        (Get-Status '/control.html').Body | Should -Match '<title>Warden Control'
    }
    It 'serves /api/state as the live snapshot JSON' {
        $s = (Get-Status '/api/state').Body | ConvertFrom-Json
        $s.summary.agents_total | Should -Be 13
    }
    It 'starts the scenario on POST /api/scenario/start' {
        $r = Get-Status '/api/scenario/start' @{} 'POST'
        $r.Code | Should -Be 200
        ($r.Body | ConvertFrom-Json).ok | Should -Be $true
    }
}

Describe 'Shutdown' {
    It 'stops the host on POST /__fleethacker/shutdown with the header' {
        (Get-Status '/__fleethacker/shutdown' @{ 'X-FleetHacker-Shutdown' = '1' } 'POST').Code | Should -Be 202
        $Proc.WaitForExit(10000) | Should -BeTrue
    }
}
