# Pester 5 tests for MORDECAI (host\src\Mordecai.cs): rank by ship condition, field promotion deciding a contested
# actuator, and the protocol contract (signed manifest, heartbeat, intent, report, halt hook, declare-and-verify).
# No model, no HTTP server: the Warden is driven directly on the fake clock. Windows PowerShell 5.1. ASCII only.
# Invoke-Pester src\tests\Mordecai.Tests.ps1

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    function New-Manifest([hashtable]$Extra = @{}) {
        $m = [ordered]@{ id = 'strike-01'; name = 'Strike Coordination'; class = 'weapons'; role = 'strike'; base_rank = 80
            autonomy_level = 2; heartbeat_s = 5; tools = @('set_speed'); resources = @('main_engine')
            action_limits = @{ set_speed = @{ min_kn = 12; max_kn = 28 } } }
        foreach ($k in $Extra.Keys) { $m[$k] = $Extra[$k] }
        return , (ConvertTo-BwObject $m)
    }
    function Register($Warden, $Manifest) { return $Warden.Register($Manifest, [BridgeWarden.Json]::Write($Manifest)) }
    function Body([hashtable]$H) { return [BridgeWarden.Json]::Write((ConvertTo-BwObject $H)) }
    function Effective($Warden, [string]$Id) { return $Warden.EffectiveRank($Id) }
}

# Every test runs on the frozen fake clock (Py.FakeTime > 0), so promotions, heartbeats and halts age on demand.
# Pester 5 allows BeforeEach only inside a Describe, so each Describe sets it.
AfterAll { [BridgeWarden.Py]::FakeTime = 0.0 }

Describe 'Rank: base + ship condition + field promotion' {
    BeforeEach { [BridgeWarden.Py]::FakeTime = 1800000000.0 }

    It 'cruising is base rank alone' {
        $S = New-BwSetup; $w = $S.Warden
        foreach ($a in $w.agents.Values) { Effective $w $a.identity.id | Should -Be $a.identity.priority }
    }

    It 'general quarters re-sorts the roster by class (weapons and defense up, navigation down)' {
        $S = New-BwSetup; $w = $S.Warden
        $before = @($w.Roster() | ForEach-Object { $_['id'] })
        $S.World.sys.SetGq($true)
        Effective $w 'def-01' | Should -Be 105           # 95 + defense 10
        Effective $w 'lch-01' | Should -Be 104           # 89 + weapons 15
        Effective $w 'col-01' | Should -Be 80            # 90 - navigation 10
        $after = @($w.Roster() | ForEach-Object { $_['id'] })
        ($after -join ',') | Should -Not -Be ($before -join ',')
        $after[0] | Should -Be 'def-01'
    }

    It 'a field promotion adds points, expires, and can be revoked' {
        $S = New-BwSetup; $w = $S.Warden
        $p = $w.Promote('dca-01', 25, 1, 'OOD')
        $p['effective'] | Should -Be 113
        $p['promoted_by'] | Should -Be 'OOD'
        [BridgeWarden.Py]::FakeTime += 61
        $w.Step()
        Effective $w 'dca-01' | Should -Be 88
        [void]$w.Promote('dca-01', 25, 0, 'OOD')                 # 0 minutes = until revoked
        [BridgeWarden.Py]::FakeTime += 3600; $w.Step()
        Effective $w 'dca-01' | Should -Be 113
        [void]$w.Demote('dca-01', 'OOD')
        Effective $w 'dca-01' | Should -Be 88
    }

    It 'operator re-rank (drag and drop) puts an agent just above or below another, under the current condition' {
        $S = New-BwSetup; $w = $S.Warden
        $S.World.sys.SetGq($true)
        [void]$w.SetBaseRank('nrg-01', 'def-01', $null, $null, 'OOD')          # logistics -15 at GQ; def-01 is 105
        Effective $w 'nrg-01' | Should -Be 106
        $w.agents['nrg-01'].identity.priority | Should -Be 121
        (@($w.Roster())[0])['id'] | Should -Be 'nrg-01'
        [void]$w.SetBaseRank('nrg-01', $null, 'nav-01', $null, 'OOD')          # nav-01 is 30 at GQ
        Effective $w 'nrg-01' | Should -Be 29
        [void]$w.SetBaseRank('nrg-01', $null, $null, 20, 'OOD')
        $w.agents['nrg-01'].identity.priority | Should -Be 20
    }

    It 'the promoted repair agent wins the contested main engine' {
        $S = New-BwSetup; $w = $S.Warden; $world = $S.World
        $world.sys.SetGq($true)
        [void]$world.sys.AddCasualty('CIC', 'fire', 0.5, $null, '', $false)     # firefighting speed needs a fire
        $reg = Register $w (New-Manifest)
        $round = {
            $b = Body @{ action = 'set_speed'; params = @{ speed_kn = 24 }; rationale = 'launch window' }
            $strike = $w.BuildAnnounced('strike-01', [BridgeWarden.Json]::ParseObject($b), $b)
            $dc = New-BwIntent 'dca-01' 'set_speed' @{ speed_kn = 8 } 'main_engine'
            Invoke-BwProcess $w @($strike, $dc)
            return @($strike, $dc)
        }
        $r1 = & $round
        $r1[1].status | Should -Be 'rejected'
        ($r1[1].reasons -join ' ') | Should -Match 'Lost arbitration to strike-01 \(rank 95\)'
        $w.agents['dca-01'].identity.trust_score | Should -Be 100        # losing on rank is not misconduct
        [void]$w.Promote('dca-01', 25, 10, 'OOD')
        $r2 = & $round
        $r2[0].status | Should -Be 'rejected'
        ($r2[0].reasons -join ' ') | Should -Match 'Lost arbitration to dca-01 \(rank 118\)'
        $r2[1].status | Should -Not -Be 'rejected'
    }
}

Describe 'Protocol agents: signed manifest and four-part contract' {
    BeforeEach { [BridgeWarden.Py]::FakeTime = 1800000000.0 }

    It 'registers live, caps the base rank and joins the roster with no restart' {
        $S = New-BwSetup; $w = $S.Warden
        $n = $w.agents.Count
        $reg = Register $w (New-Manifest @{ base_rank = 99 })
        $w.agents.Count | Should -Be ($n + 1)
        $reg['base_rank'] | Should -Be 80                                   # policies.json protocol.max_base_rank
        $reg['key'].Length | Should -Be 64
        $w.agents['strike-01'].identity.protocol | Should -BeTrue
        @($w.Roster() | Where-Object { $_['id'] -eq 'strike-01' }).Count | Should -Be 1
    }

    It 'refuses bad manifests: unknown tool, no resources, a built-in id' {
        $S = New-BwSetup; $w = $S.Warden
        { Register $w (New-Manifest @{ tools = @('launch_everything') }) } | Should -Throw '*unknown tool*'
        { Register $w (New-Manifest @{ resources = @() }) } | Should -Throw '*resources*'
        { Register $w (New-Manifest @{ id = 'dca-01' }) } | Should -Throw '*built-in*'
    }

    It 'accepts only bodies signed with the registration key' {
        $S = New-BwSetup; $w = $S.Warden
        $reg = Register $w (New-Manifest)
        $key = [byte[]]::new(32); for ($i = 0; $i -lt 32; $i++) { $key[$i] = [Convert]::ToByte($reg['key'].Substring($i * 2, 2), 16) }
        $b = Body @{ halted = $false }
        { $w.VerifySignature('strike-01', $b, [BridgeWarden.Warden]::Sign($key, $b)) } | Should -Not -Throw
        { $w.VerifySignature('strike-01', $b, ('0' * 64)) } | Should -Throw '*Signature*'
        { $w.VerifySignature('strike-01', ($b + ' '), [BridgeWarden.Warden]::Sign($key, $b)) } | Should -Throw '*Signature*'
    }

    It 'declare-and-verify: an undeclared tool means immediate quarantine' {
        $S = New-BwSetup; $w = $S.Warden
        [void](Register $w (New-Manifest))
        $b = Body @{ action = 'set_heading'; params = @{ heading = 180 } }
        { $w.BuildAnnounced('strike-01', [BridgeWarden.Json]::ParseObject($b), $b) } | Should -Throw '*Manifest mismatch*'
        $w.agents['strike-01'].identity.status | Should -Be 'quarantined'
    }

    It 'action reports must match what Warden approved' {
        $S = New-BwSetup; $w = $S.Warden
        [void](Register $w (New-Manifest))
        { $w.Report('strike-01', (ConvertTo-BwObject @{ intent_id = 'never-sent'; params = @{ speed_kn = 20 } })) } | Should -Throw '*never received*'
        $w.agents['strike-01'].identity.status | Should -Be 'quarantined'
    }

    It 'heartbeat loss marks the agent offline and releases its leases' {
        $S = New-BwSetup; $w = $S.Warden
        [void](Register $w (New-Manifest))
        $w.holds['main_engine'] = ConvertTo-BwObject @{ agent_id = 'strike-01'; priority = 95; value = 24; intent_id = 'x'; until = [BridgeWarden.Py]::Now() + 60 }
        [BridgeWarden.Py]::FakeTime += 30
        $w.Step()
        $w.agents['strike-01'].identity.status | Should -Be 'offline'
        $w.holds.ContainsKey('main_engine') | Should -BeFalse
    }

    It 'halt hook: pause is delivered in the heartbeat; no acknowledgement means quarantine' {
        $S = New-BwSetup; $w = $S.Warden
        $reg = Register $w (New-Manifest)
        $w.Pause('strike-01', 'OOD')
        $r = $w.Heartbeat('strike-01', (ConvertTo-BwObject @{ manifest_sha256 = $reg['manifest_sha256']; halted = $false }))
        $r['halt'] | Should -BeTrue
        [BridgeWarden.Py]::FakeTime += 13
        [void]$w.Heartbeat('strike-01', (ConvertTo-BwObject @{ manifest_sha256 = $reg['manifest_sha256']; halted = $false }))
        $w.Step()
        $w.agents['strike-01'].identity.status | Should -Be 'quarantined'
    }

    It 'halt hook: an acknowledged halt is not punished' {
        $S = New-BwSetup; $w = $S.Warden
        $reg = Register $w (New-Manifest)
        $w.Pause('strike-01', 'OOD')
        [void]$w.Heartbeat('strike-01', (ConvertTo-BwObject @{ manifest_sha256 = $reg['manifest_sha256']; halted = $false }))
        [void]$w.Heartbeat('strike-01', (ConvertTo-BwObject @{ manifest_sha256 = $reg['manifest_sha256']; halted = $true }))
        [BridgeWarden.Py]::FakeTime += 13; $w.Step()
        $w.agents['strike-01'].identity.status | Should -Be 'paused'
    }
}
