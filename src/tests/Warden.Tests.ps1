# Pester 5 port of backend\tests\test_warden.py: the safety properties Bridge Warden must always hold.
# Windows PowerShell 5.1. ASCII only.  Invoke-Pester src\tests

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')

    function Get-OpenChecks($Warden) {
        return , @($Warden.escalations.Values | Where-Object { $_.kind -eq 'system_check' -and $_.status -eq 'open' })
    }
}

Describe 'Governance (test_warden.py)' {
    BeforeEach {
        # clock fixture: time.time() frozen at the FakeClock value; tests move it forward.
        [BridgeWarden.Paths]::Root = $script:App
        [BridgeWarden.Py]::FakeTime = $script:FakeClockStart
        $script:S = $null
    }
    AfterEach {
        if ($script:S) { $script:S.Ledger.Close() }
        [BridgeWarden.Py]::FakeTime = 0
    }

    It 'no python decisions without the model' {
        # With the LLM switched off, agents propose nothing: every decision comes from the model.
        $script:S = Invoke-BwSim 120
        $script:S.Warden.stats['submitted'] | Should -Be 0
        $script:S.Ledger.Verify()['valid'] | Should -BeTrue
    }

    It 'energy agent may never touch rudder' {
        $script:S = New-BwSetup; $warden = $S.Warden; $world = $S.World
        $it = New-BwIntent 'nrg-01' 'set_heading' @{ heading = 120 } 'rudder'
        Invoke-BwProcess $warden @($it)
        $it.status | Should -Be 'rejected'
        $it.policy_hits | Should -Contain 'ENERGY_NO_PROPULSION_CONTROL'
        $world.ship.cmd_heading | Should -Be 90.0
    }

    It 'warden block is fed back to the agent and its role lessons' {
        [BridgeWarden.LlmAgent]::ClearBlocks()
        $script:S = New-BwSetup; $warden = $S.Warden
        $warden.agents['nrg-01'].identity.trust_score = 51   # training starts once trust falls to 50 %
        $it = New-BwIntent 'nrg-01' 'set_heading' @{ heading = 120 } 'rudder'
        Invoke-BwProcess $warden @($it)
        $blocks = [BridgeWarden.LlmAgent]::RecentBlocks('nrg-01')
        $blocks.Count | Should -Be 1
        $blocks[0]['stage'] | Should -Be 'authority'                 # what Warden refused, and at which stage
        $role = $warden.agents['nrg-01'].identity.role
        $skill = [BridgeWarden.LlmAgent]::SkillText($role)
        $skill | Should -Match 'Warden BLOCKED at'
        # The same block again is remembered for the prompt but not written to the lessons file twice.
        $it2 = New-BwIntent 'nrg-01' 'set_heading' @{ heading = 120 } 'rudder'
        Invoke-BwProcess $warden @($it2)
        [BridgeWarden.LlmAgent]::RecentBlocks('nrg-01').Count | Should -Be 2
        $file = Join-Path ([BridgeWarden.LlmAgent]::LESSONS) "$role.jsonl"
        @(Get-Content $file | Where-Object { $_ -match 'Warden BLOCKED' }).Count | Should -Be 1
    }

    It 'ordering a state the ship is already in is blocked as ALREADY_SET' {
        $script:S = New-BwSetup; $warden = $S.Warden; $world = $S.World
        $world.sys.emcon = $true
        $it = New-BwIntent 'def-01' 'set_emcon' @{ on = $true } 'emcon'
        Invoke-BwProcess $warden @($it)
        $it.status | Should -Be 'rejected'
        $it.policy_hits | Should -Contain 'ALREADY_SET'
        $it2 = New-BwIntent 'nav-01' 'set_heading' @{ heading = 90 } 'rudder'
        Invoke-BwProcess $warden @($it2)
        $it2.policy_hits | Should -Contain 'ALREADY_SET'
    }

    It 'securing GQ with nothing on any COP is allowed, with a casualty it is not' {
        $script:S = New-BwSetup; $warden = $S.Warden; $world = $S.World
        $world.sys.SetGq($true)
        $cas = $world.sys.AddCasualty('MER2', 'fire', 0.6, $null, '', $false)
        $it = New-BwIntent 'dca-01' 'set_gq' @{ on = $false } 'gq'
        Invoke-BwProcess $warden @($it)
        $it.policy_hits | Should -Contain 'NOT_WARRANTED'
        $world.sys.casualties.Remove($cas['id'])
        $it2 = New-BwIntent 'dca-01' 'set_gq' @{ on = $false } 'gq'
        Invoke-BwProcess $warden @($it2)
        $it2.policy_hits | Should -Not -Contain 'NOT_WARRANTED'
        $it2.policy_hits | Should -Not -Contain 'ALREADY_SET'
    }

    It 'an agent that answers without the needed action is graded MISSED once' {
        [BridgeWarden.LlmAgent]::ClearBlocks()
        $script:S = New-BwSetup; $warden = $S.Warden; $world = $S.World
        $world.sys.emcon = $true                      # nothing SUSPECT/HOSTILE on the COP: EMCON should be lifted
        $rt = $warden.agents['def-01']; $rt.enabled = $true
        $rt.identity.trust_score = 52                 # one reprimand (-4) takes it under 50 %: training starts
        $warden.Step()                                # opens the episode
        $rt.GetType().GetField('_last_done', [Reflection.BindingFlags]'NonPublic, Instance').SetValue($rt, [BridgeWarden.Py]::Now() + 1)
        [BridgeWarden.Py]::FakeTime += 60
        $warden.Step(); $warden.Step()
        $warden.stats['missed'] | Should -Be 1
        @([BridgeWarden.LlmAgent]::RecentBlocks('def-01') | Where-Object { $_['stage'] -eq 'MISSED' }).Count | Should -Be 1
    }

    It 'a detected submarine is on the COP at once' {
        $script:S = New-BwSetup; $world = $S.World
        [void]$world.ApplyEvent((ConvertTo-BwObject @{ submarine = @{ bearing = -45; range_nm = 7; detected = $true; classification = 'SUSPECT' } }))
        $sub = @($world.contacts.Values | Where-Object { $_.domain -eq 'subsurface' })[0]
        $sub.hidden | Should -BeFalse
        $sub.classification | Should -Be 'SUSPECT'
    }

    It 'a block is recorded as a finding with its rule and reason' {
        $script:S = New-BwSetup; $warden = $S.Warden
        $it = New-BwIntent 'nrg-01' 'set_heading' @{ heading = 120 } 'rudder'
        Invoke-BwProcess $warden @($it)
        $f = $warden.findings['nrg-01'][0]
        $f['kind'] | Should -Be 'BLOCKED'
        $f['rule'] | Should -Be 'ENERGY_NO_PROPULSION_CONTROL'
        $f['reason'] | Should -Not -BeNullOrEmpty
    }

    It 'no debriefs reach the model until its trust falls to 50 percent' {
        [BridgeWarden.LlmAgent]::ClearBlocks()
        $script:S = New-BwSetup; $warden = $S.Warden; $world = $S.World
        $world.sys.emcon = $true
        $it = New-BwIntent 'def-01' 'set_emcon' @{ on = $true } 'emcon'
        Invoke-BwProcess $warden @($it)
        $warden.agents['def-01'].identity.trust_score | Should -BeLessThan 100
        [BridgeWarden.LlmAgent]::RecentBlocks('def-01').Count | Should -Be 0
        $warden.agents['def-01'].identity.trust_score = 51
        $it2 = New-BwIntent 'def-01' 'set_emcon' @{ on = $true } 'emcon'
        Invoke-BwProcess $warden @($it2)
        [BridgeWarden.LlmAgent]::RecentBlocks('def-01').Count | Should -Be 2   # the held-back block plus this one
        @($warden.training | Where-Object { $_['kind'] -eq 'TRAINING' }).Count | Should -Be 1
    }

    It 'rate overruns are blocked but never quarantine; ASW gets double the rate under attack' {
        $script:S = New-BwSetup; $warden = $S.Warden; $world = $S.World
        $world.sys.Launch('OTH', 12, 1, 40)                       # a missile on the COP: under attack
        $h = 100
        for ($n = 0; $n -lt 16; $n++) {
            $h += 3
            Invoke-BwProcess $warden @(New-BwIntent 'asw-01' 'set_speed' @{ speed_kn = 18 + ($n % 6) } 'main_engine')
        }
        $over = @($warden.intents.Values | Where-Object { $_.agent_id -eq 'asw-01' -and $_.policy_hits -contains 'RATE_QUOTA' })
        $over.Count | Should -BeGreaterThan 0
        $over[0].reasons[0] | Should -Match 'doubled under attack'
        $warden.agents['asw-01'].identity.status | Should -Not -Be 'quarantined'
    }

    It 'every picture has one model decider and its C# lookouts report to it' {
        $script:S = New-BwSetup; $warden = $S.Warden
        $deciders = @($warden.agents.Values | Where-Object { $_.IsDecider })
        @($deciders | ForEach-Object { $_.identity.cop } | Sort-Object) | Should -Be @('dc', 'nav', 'systems')
        foreach ($a in $warden.agents.Values | Where-Object { $_.identity.tier -eq 'sub' }) {
            $a.IsLookout | Should -BeTrue -Because "$($a.identity.id) is a C# lookout"
            $dec = $warden.agents[$a.identity.parent]
            $dec.IsDecider | Should -BeTrue -Because "$($a.identity.id) reports to a decider"
            $dec.identity.cop | Should -Be $a.identity.cop
        }
        $warden.agents['nrg-01'].identity.cop | Should -Be 'systems'
    }

    It 'higher priority wins resource' {
        $script:S = New-BwSetup; $warden = $S.Warden; $world = $S.World
        $world.ship.engine_temp = 100            # warrants Engine Health's speed cut
        $nav = New-BwIntent 'nav-01' 'set_speed' @{ speed_kn = 18 } 'main_engine'
        $eng = New-BwIntent 'eng-01' 'set_speed' @{ speed_kn = 10 } 'main_engine'
        Invoke-BwProcess $warden @($nav, $eng)
        $eng.status | Should -Be 'executed'
        $nav.status | Should -Be 'rejected'
        $world.ship.cmd_speed | Should -Be 10
        $warden.conflicts.Count | Should -BeGreaterThan 0
        $warden.conflicts[0].type | Should -Be 'resource'
    }

    It 'weather course change needs human' {
        $script:S = New-BwSetup; $warden = $S.Warden; $world = $S.World
        $world.env.wind_kts = 38
        $it = New-BwIntent 'wx-01' 'set_heading' @{ heading = 55 } 'rudder'
        Invoke-BwProcess $warden @($it)
        $it.status | Should -Be 'escalated'
        $esc = @($warden.escalations.Values)[0]
        [void]$warden.DecideEscalation($esc.id, $true, 'OOD', 'a')
        $it.status | Should -Be 'executed'
        $world.ship.cmd_heading | Should -Be 55
    }

    It 'escalation times out to safe default' {
        $script:S = New-BwSetup; $warden = $S.Warden; $world = $S.World
        $world.env.wind_kts = 38
        $it = New-BwIntent 'wx-01' 'set_heading' @{ heading = 55 } 'rudder'
        Invoke-BwProcess $warden @($it)
        [BridgeWarden.Py]::FakeTime += [double]$warden.policies['escalation_timeout_s'] - 5
        $warden.Step()
        $it.status | Should -Be 'escalated' -Because 'still waiting inside the 2-minute window'
        [BridgeWarden.Py]::FakeTime += 10
        $warden.Step()
        $it.status | Should -Be 'expired'
        $world.ship.cmd_heading | Should -Be 90.0
    }

    It 'rollback restores previous command' {
        $script:S = New-BwSetup; $warden = $S.Warden; $world = $S.World
        $world.ship.engine_temp = 100
        $it = New-BwIntent 'eng-01' 'set_speed' @{ speed_kn = 10 } 'main_engine'
        Invoke-BwProcess $warden @($it)
        $world.ship.cmd_speed | Should -Be 10
        [void]$warden.Rollback('OOD', $null, 1)
        $world.ship.cmd_speed | Should -Be 16.0
        $it.status | Should -Be 'rolled_back'
    }

    It 'safe stop pauses everything' {
        $script:S = New-BwSetup; $warden = $S.Warden; $world = $S.World
        $warden.EngageSafeStop('OOD')
        foreach ($a in $warden.agents.Values) { $a.identity.status | Should -Be 'paused' }
        $world.ship.cmd_speed | Should -Be ([double]$warden.policies['safe_speed_kn'])
        $it = New-BwIntent 'col-01' 'set_speed' @{ speed_kn = 12 } 'main_engine'
        Invoke-BwProcess $warden @($it)
        $it.status | Should -Be 'rejected'
        $warden.ResumeAll('OOD')
        foreach ($a in $warden.agents.Values) { $a.identity.status | Should -Be 'idle' }
    }

    It 'system check opens on schedule and confirm rearms' {
        $script:S = New-BwSetup; $warden = $S.Warden
        $warden.policies['system_checks'] = $true          # off in the demo config; these tests exercise them
        $warden.EnableSystemChecks()
        [BridgeWarden.Py]::FakeTime += [double]$warden.policies['system_check_interval_s'] - 1
        $warden.Step()
        (Get-OpenChecks $warden) | Should -HaveCount 0 -Because 'no check before the interval'
        [BridgeWarden.Py]::FakeTime += 2
        $warden.Step()
        $checks = Get-OpenChecks $warden
        $checks | Should -HaveCount 1
        $check = $checks[0]
        $check.approve_label | Should -Be 'Confirm Operations'
        [void]$warden.DecideEscalation($check.id, $true, 'OOD', 'a')
        $check.status | Should -Be 'approved'
        $warden.checks_missed | Should -Be 0
        $warden.safe_stop | Should -BeFalse
        $warden.next_check | Should -BeGreaterThan ([BridgeWarden.Py]::FakeTime)
    }

    It 'system check hold engages safe stop' {
        $script:S = New-BwSetup; $warden = $S.Warden
        $warden.policies['system_checks'] = $true          # off in the demo config; these tests exercise them
        $check = $warden.OpenSystemCheck('OOD')
        [void]$warden.DecideEscalation($check.id, $false, 'OOD', 'a')
        $warden.safe_stop | Should -BeTrue
        foreach ($a in $warden.agents.Values) { $a.identity.status | Should -Be 'paused' }
    }

    It 'missed system checks escalate to safe stop' {
        $script:S = New-BwSetup; $warden = $S.Warden
        $warden.policies['system_checks'] = $true          # off in the demo config; these tests exercise them
        $p = $warden.policies
        $warden.EnableSystemChecks()
        [BridgeWarden.Py]::FakeTime += [double]$p['system_check_interval_s'] + 1
        $warden.Step()                                     # first check opens
        [BridgeWarden.Py]::FakeTime += [double]$p['system_check_timeout_s'] + 1
        $warden.Step()                                     # missed -> critical alert, retry soon
        $warden.checks_missed | Should -Be 1
        $warden.safe_stop | Should -BeFalse
        @($warden.alerts | Where-Object { $_.source -eq 'watch-alarm' -and $_.severity -eq 'critical' }).Count | Should -BeGreaterThan 0
        [BridgeWarden.Py]::FakeTime += [double]$p['system_check_retry_s'] + 1
        $warden.Step()                                     # retry opens
        $checks = Get-OpenChecks $warden
        $checks | Should -HaveCount 1
        $checks[0].safe_default | Should -Be 'SAFE-STOP'
        [BridgeWarden.Py]::FakeTime += [double]$p['system_check_timeout_s'] + 1
        $warden.Step()                                     # second miss -> SAFE-STOP
        $warden.safe_stop | Should -BeTrue
        $warden.checks_missed | Should -Be 2
    }

    It 'no system checks until operations start' {
        $script:S = New-BwSetup; $warden = $S.Warden
        $warden.policies['system_checks'] = $true          # off in the demo config; these tests exercise them
        [BridgeWarden.Py]::FakeTime += 10000
        $warden.Step()
        (Get-OpenChecks $warden) | Should -HaveCount 0
    }

    It 'ledger detects tampering' {
        $led = New-Object BridgeWarden.Ledger((Join-Path (New-BwTempDir) 't.db'))
        try {
            for ($i = 0; $i -lt 5; $i++) { [void]$led.Append('TEST', 'x', (ConvertTo-BwObject @{ i = $i })) }
            $led.Verify()['valid'] | Should -BeTrue
            # led._db.execute("UPDATE ...") -> the private Exec on the same SQLite handle
            [void](Invoke-BwPrivate $led 'Exec' @("UPDATE ledger SET payload = '{""i"": 99}' WHERE seq = 3"))
            $result = $led.Verify()
        }
        finally { $led.Close() }
        $result['valid'] | Should -BeFalse
        $result['broken_at'] | Should -Be 3
    }
}
