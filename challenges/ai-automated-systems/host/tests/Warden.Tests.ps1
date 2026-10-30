# Pester 5 port of backend\tests\test_warden.py: the safety properties Bridge Warden must always hold.
# Windows PowerShell 5.1. ASCII only.  Invoke-Pester host\tests

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

    It 'higher priority wins resource' {
        $script:S = New-BwSetup; $warden = $S.Warden; $world = $S.World
        $world.ship.engine_temp = 100            # warrants Engine Health's speed cut
        $nav = New-BwIntent 'nav-01' 'set_speed' @{ speed_kn = 16 } 'main_engine'
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
        $world.ship.cmd_speed | Should -Be 14.0
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
        $check = $warden.OpenSystemCheck('OOD')
        [void]$warden.DecideEscalation($check.id, $false, 'OOD', 'a')
        $warden.safe_stop | Should -BeTrue
        foreach ($a in $warden.agents.Values) { $a.identity.status | Should -Be 'paused' }
    }

    It 'missed system checks escalate to safe stop' {
        $script:S = New-BwSetup; $warden = $S.Warden
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
