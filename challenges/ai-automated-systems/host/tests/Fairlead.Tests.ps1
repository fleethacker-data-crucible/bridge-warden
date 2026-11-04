# Pester 5 tests for the Fairlead connection (host\src\Llm.cs Fairlead section, host\src\FairleadKey.cs).
# A fake Fairlead on CI-only port 18782 (edge-ops PORTS.md: 18000-18999 for CI) answers /__fleethacker/id as Fairlead
# does; the Credential Manager reader is swapped for a fake, so the real credential store is never read or written.
# Windows PowerShell 5.1. ASCII only.  Invoke-Pester host\tests\Fairlead.Tests.ps1

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    $script:Port = 18782
    $script:Vars = @('LLM_MODE', 'LLM_BASE_URL', 'LLM_LOCAL_MODEL', 'LLM_API_KEY', 'LLM_TIMEOUT_S')
    $script:RealReader = [BridgeWarden.FairleadKey]::Reader
    function Set-Env([string]$Name, [string]$Value) { [Environment]::SetEnvironmentVariable($Name, $Value) }
    function Start-Fake([bool]$Fairlead) {
        Set-Env 'LLM_BASE_URL' "http://localhost:$($script:Port)/v1"
        Set-Env 'LLM_MODE' 'local'
        Set-Env 'LLM_TIMEOUT_S' '10'
        $script:Fake = New-Object BridgeWardenTests.FakeSse($script:Port, [string[]]@('{"content": "ok"}'), $Fairlead)
        return $script:Fake
    }
    function Invoke-Explain {
        $in = ConvertTo-BwObject ([ordered]@{ agent = 'nav-01'; action = 'set_speed'; value = 'speed 16 kn'; stages = 'x'
                status = 'rejected'; reasons = 'r'; decided_by = 'warden' })
        return [BridgeWarden.Llm]::Run('explain_intent', $in)
    }
}

AfterAll { [BridgeWarden.FairleadKey]::Reader = $script:RealReader }

Describe 'Fairlead connection' {
    AfterEach {
        if ($script:Fake) { $script:Fake.Dispose(); $script:Fake = $null }
        foreach ($v in $script:Vars) { [Environment]::SetEnvironmentVariable($v, $null) }
        [BridgeWarden.FairleadKey]::Reader = $script:RealReader
        [BridgeWarden.Llm]::ForgetRouters()
        Start-Sleep -Milliseconds 200       # let the listener release the port before the next test
    }

    It 'detects Fairlead and sends its key, its label and the local route' {
        $f = Start-Fake $true
        [BridgeWarden.FairleadKey]::Reader = [BridgeWardenTests.Fakes]::Key('fl-test-key')
        [BridgeWarden.Llm]::IsFairlead("http://localhost:$($script:Port)/v1") | Should -Be $true
        $r = Invoke-Explain
        $r.source | Should -Be 'local'
        $r.text | Should -Be 'ok'
        $f.auth[0] | Should -Be 'Bearer fl-test-key'
        $f.labels[0] | Should -Be 'BridgeWarden'
        ($f.seen[0] | ConvertFrom-Json).model | Should -Be 'local'
    }

    It 'never uses LLM_API_KEY for Fairlead and says how to install a key when none is saved' {
        $f = Start-Fake $true
        Set-Env 'LLM_API_KEY' 'fl-from-env'
        [BridgeWarden.FairleadKey]::Reader = [BridgeWardenTests.Fakes]::Key($null)
        $r = Invoke-Explain
        $r.source | Should -Not -Be 'local'
        $r.error | Should -Match 'No Fairlead key for BridgeWarden'
        $r.error | Should -Match 'Fairlead/BridgeWarden'
        $f.seen.Count | Should -Be 0
    }

    It 'keeps an explicit model name instead of the route' {
        $f = Start-Fake $true
        Set-Env 'LLM_LOCAL_MODEL' 'smart'
        [BridgeWarden.FairleadKey]::Reader = [BridgeWardenTests.Fakes]::Key('fl-test-key')
        [void](Invoke-Explain)
        ($f.seen[0] | ConvertFrom-Json).model | Should -Be 'smart'
    }

    It 'treats any other server as before: no label, no key on loopback, phoenixlai by default' {
        $f = Start-Fake $false
        [BridgeWarden.FairleadKey]::Reader = [BridgeWardenTests.Fakes]::Key('fl-should-not-be-sent')
        $r = Invoke-Explain
        $r.source | Should -Be 'local'
        $f.auth[0] | Should -Be ''
        $f.labels[0] | Should -Be ''
        ($f.seen[0] | ConvertFrom-Json).model | Should -Be 'phoenixlai'
    }
}

Describe 'Finding Fairlead (LLM_BASE_URL=fairlead)' {
    BeforeEach {
        $script:Dir = Join-Path ([IO.Path]::GetTempPath()) ('bw-fl-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $script:Dir | Out-Null
        [Environment]::SetEnvironmentVariable('FAIRLEAD_DATA_DIR', $script:Dir)
        [BridgeWarden.Llm]::ForgetRouters()
    }
    AfterEach {
        if ($script:Fake) { $script:Fake.Dispose(); $script:Fake = $null }
        foreach ($v in $script:Vars + 'FAIRLEAD_DATA_DIR') { [Environment]::SetEnvironmentVariable($v, $null) }
        [BridgeWarden.Llm]::ForgetRouters()
        Remove-Item -LiteralPath $script:Dir -Recurse -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 200
    }

    It 'uses the gateway Fairlead recorded in its CLI file (any port)' {
        $script:Fake = New-Object BridgeWardenTests.FakeSse($script:Port, [string[]]@('{"content": "ok"}'), $true)
        # Only url, gateway and pid are read; a token in the file is ignored.
        Set-Content -LiteralPath (Join-Path $script:Dir 'fairlead-cli.json') -Encoding ASCII -Value (
            '{"pid":' + $PID + ',"url":"http://localhost:' + $script:Port + '/","gateway":"http://localhost:' + $script:Port + '/v1","token":"dpapi:not-read"}')
        [Environment]::SetEnvironmentVariable('LLM_BASE_URL', 'fairlead')
        $s = [BridgeWarden.Llm]::Settings()
        $s['base_url'] | Should -Be "http://localhost:$($script:Port)/v1"
        $s['endpoint'] | Should -Be 'fairlead'
        [BridgeWarden.Llm]::ResolveEndpoint('Fairlead') | Should -Be "http://localhost:$($script:Port)/v1"
    }

    It 'reads an older Fairlead''s runtime file from FAIRLEAD_HOME\logs' {
        $script:Fake = New-Object BridgeWardenTests.FakeSse($script:Port, [string[]]@('{"content": "ok"}'), $true)
        $home2 = Join-Path $script:Dir 'app'
        New-Item -ItemType Directory -Path (Join-Path $home2 'logs') | Out-Null
        Set-Content -LiteralPath (Join-Path $home2 ('logs\fairlead-server-' + $PID + '.json')) -Encoding ASCII -Value (
            '{"pid":' + $PID + ',"url":"http://localhost:' + $script:Port + '/","gateway":"http://localhost:' + $script:Port + '/v1"}')
        [Environment]::SetEnvironmentVariable('FAIRLEAD_HOME', $home2)
        try { [BridgeWarden.Llm]::FairleadFromFile() | Should -Be "http://localhost:$($script:Port)/v1" }
        finally { [Environment]::SetEnvironmentVariable('FAIRLEAD_HOME', $null) }
    }

    It 'ignores a CLI file left over from a Fairlead that has exited' {
        Set-Content -LiteralPath (Join-Path $script:Dir 'fairlead-cli.json') -Encoding ASCII -Value (
            '{"pid":999999,"gateway":"http://localhost:' + $script:Port + '/v1"}')
        [BridgeWarden.Llm]::FairleadFromFile() | Should -BeNullOrEmpty
    }

    It 'ignores a CLI file that points at something that is not Fairlead' {
        $script:Fake = New-Object BridgeWardenTests.FakeSse($script:Port, [string[]]@('{"content": "ok"}'), $false)
        Set-Content -LiteralPath (Join-Path $script:Dir 'fairlead-cli.json') -Encoding ASCII -Value (
            '{"pid":' + $PID + ',"gateway":"http://localhost:' + $script:Port + '/v1"}')
        [BridgeWarden.Llm]::FairleadFromFile() | Should -BeNullOrEmpty
    }

    It 'falls back to the registry address when no Fairlead is found' {
        [Environment]::SetEnvironmentVariable('LLM_BASE_URL', 'fairlead')
        if (-not [BridgeWarden.Llm]::IsFairlead('http://127.0.0.1:8776/v1')) {
            [BridgeWarden.Llm]::Settings()['base_url'] | Should -Be 'http://127.0.0.1:8776/v1'
        }
    }
}

Describe 'Port block' {
    It 'never reaches Fairlead''s 8776' {
        $ports = Import-PowerShellDataFile (Join-Path $script:HostDir 'FleetPorts.psd1')
        $b = $ports['bridge-warden']
        ($b.Start + $b.Size - 1) | Should -BeLessThan 8776
    }
}

Describe 'Source rules' {
    It 'allows advapi32.dll only in FairleadKey.cs and winsqlite3.dll only in Ledger.cs' {
        . (Join-Path $script:HostDir 'Build.ps1')
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('bw-rules-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tmp | Out-Null
        try {
            $bad = Join-Path $tmp 'Ledger.cs'
            Set-Content -LiteralPath $bad -Value '[DllImport("advapi32.dll")] static extern bool X();' -Encoding ASCII
            @(Test-BwSourceRules -Files @($bad)) -join ' ' | Should -Match 'native DLL not on the allowlist \(advapi32.dll\)'
            $other = Join-Path $tmp 'App.cs'
            Set-Content -LiteralPath $other -Value '[DllImport("advapi32.dll")] static extern bool X();' -Encoding ASCII
            @(Test-BwSourceRules -Files @($other)) -join ' ' | Should -Match 'native calls'
            $ok = Join-Path $script:HostDir 'src\FairleadKey.cs'
            @(Test-BwSourceRules -Files @($ok)).Count | Should -Be 0
        }
        finally { Remove-Item -LiteralPath $tmp -Recurse -Force }
    }
}
