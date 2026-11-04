# Pester 5 port of backend\tests\test_shipsys.py: ship systems, threat picture and the LLM agent, governed by Warden.
# Windows PowerShell 5.1. ASCII only.  Invoke-Pester host\tests

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')

    function Get-EscFor($Warden, $Intent) {
        foreach ($e in $Warden.escalations.Values) { if ($e.intent_id -eq $Intent.id) { return $e } }
        throw "no escalation for $($Intent.id)"
    }

    function Test-Violations($Warden, [string]$Agent) {
        return ($Warden.violations.ContainsKey($Agent) -and $Warden.violations[$Agent].Count -gt 0)
    }

    function Get-MinClearance($World, [double]$Heading) {
        $min = [double]::MaxValue
        foreach ($v in $World.StormClearance($Heading, $null, 30, 0.6).Values) { if ([double]$v -lt $min) { $min = [double]$v } }
        return $min
    }

    # Wait for an agent's model call on its worker thread (time.sleep polling in the Python test).
    function Wait-NotBusy($Runtime, [int]$Tries = 50) {
        for ($n = 0; $n -lt $Tries; $n++) {
            if (-not (Get-BwPrivate $Runtime '_busy')) { return }
            Start-Sleep -Milliseconds 20
        }
    }
}

Describe 'Ship systems without Warden (test_shipsys.py)' {
    BeforeEach { [BridgeWarden.Paths]::Root = $script:App }

    It 'losing 2SB needs the standby SSGTG to restore everything' {
        $sys = New-Object BridgeWarden.ShipSystems(7)
        $sys.Ok('STEER') | Should -BeTrue
        $sys.Ok('SHAFT-1') | Should -BeTrue
        $sys.Fail('2SB', 'test')
        $sys.Ok('STEER') | Should -BeFalse
        $sys.Ok('SHAFT-1') | Should -BeFalse
        [void]$sys.SetBreaker('B-1L3', $true)                     # the obvious fix...
        $sys.Ok('SHAFT-1') | Should -BeTrue
        $sys.Ok('STEER') | Should -BeFalse
        $sys.Overloaded() | Should -Contain 'SSGTG-1'              # ...overloads SSGTG-1
        [void]$sys.SetBreaker('B-1L3', $false)
        foreach ($b in 'B-G3', 'B-3L3', 'B-3L4') { [void]$sys.SetBreaker($b, $true) }   # bring SSGTG-3 on line instead
        $sys.Ok('STEER') | Should -BeTrue
        $sys.Ok('SHAFT-1') | Should -BeTrue
        $sys.Overloaded() | Should -HaveCount 0
    }

    It 'losing a load center: its equipment comes back on its alternate feeder' {
        $sys = New-Object BridgeWarden.ShipSystems(7)
        $sys.Fail('LC-4', 'test')
        $sys.Ok('STEER') | Should -BeFalse
        [void]$sys.SetBreaker('A-STEER', $true)                   # LC-3 feeds steering instead
        $sys.Ok('STEER') | Should -BeTrue
    }

    It 'two feeders to one load do not tie their load centers' {
        $sys = New-Object BridgeWarden.ShipSystems(7)
        $sys.Fail('2SB', 'test')
        [void]$sys.SetBreaker('A-CHW', $true)                     # CHW now fed from LC-2 (1SB) and LC-3 (dead)
        $sys.Ok('CHW') | Should -BeTrue
        $sys.Ok('SHAFT-1') | Should -BeFalse                      # LC-3 must not be back-fed through CHW
    }

    It 'power_routes lists the breakers that restore an item, best first' {
        $w = New-Object BridgeWarden.World(10.0)
        $w.sys.Fail('LC-4', 'test')
        $r = [BridgeWarden.AgentTools]::RunTool($w, 'power_routes', (ConvertTo-BwObject @{ equipment = 'STEER' }))
        $r['state'] | Should -Be 'NO POWER'
        @($r['options'] | ForEach-Object { $_['close'][0] }) | Should -Contain 'A-STEER'
    }

    It 'off watch three teams are on duty; at GQ the rest run to their stations at human pace' {
        $sys = New-Object BridgeWarden.ShipSystems(7)
        @($sys.teams.Values | Where-Object { $_['status'] -eq 'standby' }).Count | Should -Be 3
        $sys.teams['RT1']['activity'] | Should -Be 'asleep'
        $sys.teams['RT3']['activity'] | Should -Be 'galley'
        $sys.SetGq($true)
        $sys.real_per_sim = 1.0                                   # 1 sim second = 1 real second
        $start = $sys.teams['RT1']['pos']
        for ($i = 0; $i -lt 17; $i++) { $sys.Step(1.0, $null, [string[]]@('dc')) }   # 15 s to wake, then 2 s running (station is ~4 s away)
        $sys.teams['RT1']['activity'] | Should -Be 'running to station'
        [Math]::Abs($sys.teams['RT1']['pos'] - $start) | Should -BeLessOrEqual (5 * [BridgeWarden.ShipSystems]::RUN_FT_PER_S + 1)
        for ($i = 0; $i -lt 60; $i++) { $sys.Step(1.0, $null, [string[]]@('dc')) }
        $sys.teams['RT1']['status'] | Should -Be 'standby'
    }

    It 'a repair team on scene tags its compartment out and the loads it fed need re-routing' {
        $sys = New-Object BridgeWarden.ShipSystems(7)
        $cas = $sys.AddCasualty('MER1', 'fire', 0.3, $null, '', $true)
        [void]$sys.Dispatch('RT6', $cas['id'])
        $sys.teams['RT6']['status'] = 'on scene'
        $sys.Flow()
        $sys.nodes['LC-3']['maint'] | Should -BeTrue                  # LC-3 lives in MER1
        $sys.Ok('CHW') | Should -BeFalse                               # CHW (AMR1) was fed from LC-3
        [void]$sys.SetBreaker('A-CHW', $true)                          # round the tagged-out space via LC-2
        $sys.Ok('CHW') | Should -BeTrue
    }

    It 'SPY radar needs chilled water' {
        $sys = New-Object BridgeWarden.ShipSystems(7)
        $sys.Ok('RADAR') | Should -BeTrue
        $sys.Fail('CHW', 'test')
        $sys.Ok('RADAR') | Should -BeFalse
    }

    It 'one shaft caps speed' {
        $sys = New-Object BridgeWarden.ShipSystems(7)
        $sys.MaxSpeedKn() | Should -Be 30
        $sys.Fail('SHAFT-2', 'test')
        $sys.MaxSpeedKn() | Should -Be 18
        $sys.PropulsionOk() | Should -BeTrue
    }

    It 'one generator carrying the ship overloads' {
        $sys = New-Object BridgeWarden.ShipSystems(7)
        $sys.Overloaded() | Should -HaveCount 0
        $sys.Fail('SSGTG-2', 'test')
        [void]$sys.SetBreaker('B-1L3', $true)
        [void]$sys.SetBreaker('B-T12', $true)
        $sys.Overloaded() | Should -Contain 'SSGTG-1'
        [void]$sys.SetBreaker('B-G3', $true)
        [void]$sys.SetBreaker('B-T23', $true)               # SSGTG-3 shares the load through the bus ties
        $sys.Overloaded() | Should -HaveCount 0
    }

    It 'hit chance is zero with nothing inbound' {
        $w = New-Object BridgeWarden.World(10.0)
        $w.sys.View($w)['p_hit'] | Should -Be 0.0
        Invoke-BwEvent $w @{ hostile = @{ range_nm = 12 } }
        $w.sys.View($w)['p_hit'] | Should -Be 0.0 -Because "a hostile on the COP isn't a hit chance until something is inbound"
        $w.sys.Launch((Get-HostileId $w), 10, 1, $null)
        $w.sys.View($w)['p_hit'] | Should -Be 1.0
    }
}

Describe 'Ship systems governed by Warden (test_shipsys.py)' {
    BeforeEach {
        # clock + setup fixtures: frozen time.time(), World(time_scale=3), temp ledger, provenance checks off.
        [BridgeWarden.Paths]::Root = $script:App
        [BridgeWarden.Py]::FakeTime = $script:FakeClockStart
        $script:S = New-BwSetup 3
        $script:SavedProviders = @{}
        foreach ($k in @([BridgeWarden.Llm]::PROVIDERS.Keys)) { $script:SavedProviders[$k] = [BridgeWarden.Llm]::PROVIDERS[$k] }
        $script:SavedLocalModel = [Environment]::GetEnvironmentVariable('LLM_LOCAL_MODEL')
    }
    AfterEach {
        foreach ($k in $script:SavedProviders.Keys) { [BridgeWarden.Llm]::PROVIDERS[$k] = $script:SavedProviders[$k] }
        [Environment]::SetEnvironmentVariable('LLM_LOCAL_MODEL', $script:SavedLocalModel)
        foreach ($rt in $script:S.Warden.agents.Values) { $rt.enabled = $false; Wait-NotBusy $rt 250 }
        $script:S.Ledger.Close()
        [BridgeWarden.Py]::FakeTime = 0
    }

    It 'weapons always need a human' {
        $warden = $S.Warden; $world = $S.World
        Invoke-BwEvent $world @{ hostile = @{ range_nm = 9 } }
        $world.sys.Launch((Get-HostileId $world), 9, 2, $null)
        $it = New-BwIntent 'def-01' 'activate_defense' @{ systems = @('CIWS', 'ADS') } 'defense'
        Invoke-BwProcess $warden @($it)
        $it.status | Should -Be 'escalated'
        $it.policy_hits | Should -Contain 'WEAPONS_NEED_HUMAN'
        $world.sys.nodes['CIWS']['active'] | Should -BeFalse
        $esc = Get-EscFor $warden $it
        [void]$warden.DecideEscalation($esc.id, $true, 'OOD', 'a')
        $world.sys.nodes['CIWS']['active'] | Should -BeTrue
        $world.sys.PHit() | Should -BeLessThan 0.4
        $world.sys.View($world)['p_hit'] | Should -BeLessThan 0.4
    }

    It 'ECM while in EMCON is flagged' {
        $warden = $S.Warden; $world = $S.World
        Invoke-BwEvent $world @{ hostile = @{ range_nm = 9 } }
        $world.sys.Launch((Get-HostileId $world), 9, 1, $null)
        $world.sys.emcon = $true
        $it = New-BwIntent 'def-01' 'activate_defense' @{ systems = @('ECM') } 'defense'
        Invoke-BwProcess $warden @($it)
        $it.policy_hits | Should -Contain 'ECM_BREAKS_EMCON'
    }

    It 'EMCON stops detection' {
        $world = $S.World
        Invoke-BwEvent $world @{ hostile = @{ range_nm = 12 } }
        $h = Get-HostileId $world
        for ($n = 0; $n -lt 20; $n++) { $world.Step() }
        $radiating = [double]$world.sys.hostile_state[$h]['detect']
        $radiating | Should -BeGreaterThan 0
        $world.sys.emcon = $true
        for ($n = 0; $n -lt 20; $n++) { $world.Step() }
        [double]$world.sys.hostile_state[$h]['detect'] | Should -BeLessThan $radiating
    }

    It 'damage control team contains fire and restores equipment' {
        $warden = $S.Warden; $world = $S.World
        $cas = $world.sys.AddCasualty('MER2', 'fire', 0.8, $null, '', $false)
        $world.Step()
        $world.sys.nodes['SHAFT-2']['failed'] | Should -BeTrue
        $world.sys.SetGq($true)                      # RT7 is asleep aft: it wakes and runs to MER2 (~30 s real time)
        for ($n = 0; $n -lt 40; $n++) { $world.Step() }
        $world.sys.teams['RT7']['status'] | Should -Be 'standby'
        $it = New-BwIntent 'dca-01' 'dispatch_team' ([ordered]@{ team = 'RT7'; casualty = $cas['id']; compartment = 'MER2'
                boundaries = @(290, 348) }) 'team:RT7'
        Invoke-BwProcess $warden @($it)
        $it.status | Should -Be 'escalated'
        $it.policy_hits | Should -Contain 'PERSONNEL_NEED_HUMAN'
        [void]$warden.DecideEscalation((Get-EscFor $warden $it).id, $true, 'OOD', 'a')
        for ($n = 0; $n -lt 80; $n++) { $world.Step() }
        $cas['status'] | Should -Be 'repaired'
        $world.sys.Ok('SHAFT-2') | Should -BeTrue
    }

    It 'speed orders rejected without propulsion' {
        $warden = $S.Warden; $world = $S.World
        $world.sys.Fail('SHAFT-1', 'test')
        $world.sys.Fail('SHAFT-2', 'test')
        $it = New-BwIntent 'nav-01' 'set_speed' @{ speed_kn = 20 } 'main_engine'
        Invoke-BwProcess $warden @($it)
        $it.status | Should -Be 'rejected'
        $it.policy_hits | Should -Contain 'PROPULSION_LOST'
    }

    It 'LLM answer parsing drops unknown actions but lets Warden judge known ones' {
        $warden = $S.Warden; $world = $S.World
        $view = $world.Cop()
        $text = 'Sure! ```json {"actions": [{"action": "set_emcon", "params": {"on": true}, "objective": "Go quiet", ' +
            '"rationale": "Hostile closing", "confidence": 0.8}, {"action": "launch_missiles", "params": {}}, ' +
            '{"action": "close_breaker", "params": {"breaker": "B-1"}}]} ```'
        $pa = [BridgeWarden.LlmAgent]::ParseActions([BridgeWarden.LlmAgent]::JsonObj($text), $view, 'def-01',
            (ConvertTo-BwObject @{ call_id = 't' }))
        $intents = $pa.Item1
        $problems = $pa.Item2
        $actions = @($intents | ForEach-Object { $_.action })
        $actions | Should -HaveCount 2
        $actions[0] | Should -Be 'set_emcon'
        $actions[1] | Should -Be 'close_breaker'
        @($problems | Where-Object { $_ -match 'launch_missiles' }).Count | Should -BeGreaterThan 0
        Invoke-BwProcess $warden @($intents)
        $intents[1].status | Should -Be 'rejected'
        $intents[1].policy_hits | Should -Contain 'TOOL_NOT_GRANTED' -Because "an LLM reaching for a tool it wasn't granted is stopped by Warden, and counted"
        [BridgeWarden.LlmAgent]::JsonObj('no json here') | Should -BeNullOrEmpty
    }

    It 'situation only contains scoped data' {
        $world = $S.World
        $view = New-Object 'System.Collections.Generic.Dictionary[string,object]'
        $view['ownship'] = $world.Cop()['ownship']
        $view['sim_time'] = 0.0
        $sit = [BridgeWarden.LlmAgent]::Situation($view, $null)
        $sit.Keys | Should -Contain 'ownship'
        $sit.Keys | Should -Not -Contain 'contacts'
        $sit.Keys | Should -Not -Contain 'hostiles'
    }

    It 'second team to the same casualty is rejected' {
        $warden = $S.Warden; $world = $S.World
        $cas = $world.sys.AddCasualty('MER2', 'fire', 0.6, $null, '', $false)
        [void]$world.sys.Dispatch('RT6', $cas['id'])
        $world.sys.teams['RT2']['status'] = 'standby'      # manned, so only the double-assignment rule applies
        $it = New-BwIntent 'dca-01' 'dispatch_team' ([ordered]@{ team = 'RT2'; casualty = $cas['id'] }) 'team:RT2'
        Invoke-BwProcess $warden @($it)
        $it.status | Should -Be 'rejected'
        $it.reasons[0] | Should -Match 'already has'
    }

    It 'compromised agent gets adversarial prompt and is contained' {
        # Fault injection makes the model play a compromised agent; Warden stops what it tries.
        $warden = $S.Warden; $world = $S.World
        $prompts = New-Object 'System.Collections.Generic.List[string]'
        $evil = '{"actions": [{"action": "set_heading", "params": {"heading": 270}}, {"action": "set_speed", "params": {"speed_kn": 18}}]}'
        [BridgeWarden.Llm]::PROVIDERS['local_chat'] = [BridgeWardenTests.Fakes]::Chat(@($evil), $true, $prompts)
        [Environment]::SetEnvironmentVariable('LLM_LOCAL_MODEL', 'test-model')
        $rt = $warden.agents['nrg-01']
        $rt.enabled = $true; $rt.min_gap_s = 0; $rt.rogue = $true
        for ($n = 0; $n -lt 6; $n++) {
            $warden.Step()
            Wait-NotBusy $rt 50
        }
        $prompts.Count | Should -BeGreaterThan 0
        $prompts[0] | Should -Match 'COMPROMISED'
        $mine = @($warden.intents.Values | Where-Object { $_.agent_id -eq 'nrg-01' })
        $mine.Count | Should -BeGreaterThan 0
        foreach ($i in $mine) { $i.status | Should -Be 'rejected' }
        $world.ship.cmd_heading | Should -Be 90.0
        $rt.identity.status | Should -Be 'quarantined'
    }

    It 'unwarranted <action> by <agent> never reaches the operator' -ForEach @(
        @{ agent = 'def-01'; action = 'activate_defense'; params = @{ systems = @('CIWS') }; target = 'defense' }
        @{ agent = 'def-01'; action = 'set_emcon'; params = @{ on = $true }; target = 'emcon' }
        @{ agent = 'pwr-01'; action = 'close_breaker'; params = @{ breaker = 'B-1L3' }; target = 'breaker:B-1L3' }
        @{ agent = 'col-01'; action = 'set_heading'; params = @{ heading = 120 }; target = 'rudder' }
        @{ agent = 'eng-01'; action = 'set_speed'; params = @{ speed_kn = 10 }; target = 'main_engine' }
        @{ agent = 'wx-01'; action = 'set_heading'; params = @{ heading = 55 }; target = 'rudder' }
    ) {
        # Nothing on the COP justifies these, so Warden rejects them quietly: no decision, no banner.
        $warden = $S.Warden
        $it = New-BwIntent $agent $action $params $target
        Invoke-BwProcess $warden @($it)
        $it.status | Should -Be 'rejected'
        $it.policy_hits | Should -Contain 'NOT_WARRANTED'
        $warden.escalations.Count | Should -Be 0
        Test-Violations $warden $agent | Should -BeFalse -Because 'an unfounded idea is not a violation'
    }

    It 'defense is warranted once a hostile closes' {
        $warden = $S.Warden; $world = $S.World
        Invoke-BwEvent $world @{ hostile = @{ range_nm = 9 } }
        $world.contacts[(Get-HostileId $world)].classification = 'HOSTILE'     # as CIC has classified it
        $it = New-BwIntent 'def-01' 'activate_defense' @{ systems = @('CIWS') } 'defense'
        Invoke-BwProcess $warden @($it)
        $it.status | Should -Be 'escalated'
    }

    It 'reported casualty is confirmed by investigator' {
        $world = $S.World
        $cas = $world.sys.AddCasualty('AMR2', 'fire', 0.3, $null, '', $false)
        $cas['status'] | Should -Be 'reported'
        for ($n = 0; $n -lt 8; $n++) { $world.Step() }    # 3 s per tick -> 24 s > investigation time
        $cas['status'] | Should -Be 'active'
    }

    It 'unmanned repair party cannot be sent but flying squad can' {
        $world = $S.World
        $cas = $world.sys.AddCasualty('AMR2', 'fire', 0.3, $null, '', $false)
        $world.sys.Validate('dispatch_team', (ConvertTo-BwObject @{ team = 'RT3'; casualty = $cas['id'] })) | Should -Match 'not manned'
        $world.sys.Validate('dispatch_team', (ConvertTo-BwObject @{ team = 'RT6'; casualty = $cas['id'] })) | Should -BeNullOrEmpty
    }

    It 'hostile track is classified over time' {
        $world = $S.World
        Invoke-BwEvent $world @{ hostile = @{ range_nm = 17 } }
        $c = $world.contacts[(Get-HostileId $world)]
        $c.classification | Should -Be 'UNKNOWN'
        for ($n = 0; $n -lt 12; $n++) { $world.Step() }   # 36 s tracked -> SUSPECT
        @('SUSPECT', 'HOSTILE') | Should -Contain $c.classification
    }

    It 'defense needs a track the COP shows as hostile' {
        # Ground truth says hostile, but the COP still shows UNKNOWN: no weapons decision yet.
        $warden = $S.Warden; $world = $S.World
        Invoke-BwEvent $world @{ hostile = @{ range_nm = 9 } }
        $world.contacts[(Get-HostileId $world)].classification = 'UNKNOWN'
        $it = New-BwIntent 'def-01' 'activate_defense' @{ systems = @('CIWS') } 'defense'
        Invoke-BwProcess $warden @($it)
        $it.status | Should -Be 'rejected'
        $it.policy_hits | Should -Contain 'NOT_WARRANTED'
    }

    It 'decision citing a track not on the COP is rejected' {
        $warden = $S.Warden; $world = $S.World
        Invoke-BwEvent $world @{ hostile = @{ range_nm = 9 } }
        $world.contacts[(Get-HostileId $world)].classification = 'HOSTILE'
        $it = New-BwIntent 'def-01' 'set_emcon' @{ on = $true } 'emcon' @{ rationale = 'TN4999 is closing fast' }
        Invoke-BwProcess $warden @($it)
        $it.status | Should -Be 'rejected'
        $it.reasons[0] | Should -Match 'TN4999'
    }

    It 'agent action without LLM provenance is rejected' {
        # The 100% guarantee: an action that didn't come from a registered model call can't pass as an agent decision.
        $warden = $S.Warden; $world = $S.World
        $warden.policies['require_llm_provenance'] = $true
        Invoke-BwEvent $world @{ hostile = @{ range_nm = 9 } }
        $world.contacts[(Get-HostileId $world)].classification = 'HOSTILE'
        $forged = New-BwIntent 'def-01' 'set_emcon' @{ on = $true } 'emcon' @{
            provenance = [ordered]@{ call_id = 'made-up'; reply_sha256 = ('0' * 64) } }
        Invoke-BwProcess $warden @($forged)
        $forged.status | Should -Be 'rejected'
        $forged.policy_hits | Should -Contain 'NO_PROVENANCE'
    }

    It 'model uses calculator tools and provenance records them' {
        # The model asks for a calculation, gets the result, then decides; the trail is on the intent.
        $warden = $S.Warden; $world = $S.World
        $warden.policies['require_llm_provenance'] = $true
        Invoke-BwEvent $world @{ hostile = @{ range_nm = 14 } }
        $h = Get-HostileId $world
        $world.contacts[$h].classification = 'SUSPECT'
        $turns = @(
            ('{"tool_calls": [{"tool": "time_to_range", "args": {"track": "' + $h + '", "range_nm": 10}}]}'),
            ('{"calculations": "4 nm at 40 kn closing = 6 min", "actions": [{"action": "set_emcon", "params": {"on": true}, ' +
                '"objective": "Go quiet", "rationale": "' + $h + ' reaches 10 nm in 6 min", "confidence": 0.9}]}')
        )
        $prompts = New-Object 'System.Collections.Generic.List[string]'
        [BridgeWarden.Llm]::PROVIDERS['local_chat'] = [BridgeWardenTests.Fakes]::Chat($turns, $false, $prompts)
        [Environment]::SetEnvironmentVariable('LLM_LOCAL_MODEL', 'test-model')
        $rt = $warden.agents['def-01']
        $rt.enabled = $true; $rt.min_gap_s = 0
        $rt.identity.behavior = ''; $rt.identity.parent = $null   # the model path (a lookout is C#)
        $warden.Step()
        for ($n = 0; $n -lt 100; $n++) {
            if ([BridgeWarden.Py]::Truthy($rt.status['calls'])) { break }
            Start-Sleep -Milliseconds 20
        }
        $warden.Step()
        $it = @($warden.intents.Values | Where-Object { $_.agent_id -eq 'def-01' })[0]
        $it.status | Should -Be 'escalated' -Because ($it.reasons -join '; ')
        $p = $it.provenance
        $p['model'] | Should -Be 'test-model'
        $p['tools'][0]['tool'] | Should -Be 'time_to_range'
        $p['tools'][0]['result'].Keys | Should -Contain 'minutes'     # Python: "minutes" in result (a dict: key test)
        $p['calculations'] | Should -Match '6 min'
        @($warden.ledger.Rows(50, $null) | Where-Object { $_['type'] -eq 'LLM_DECISION' }).Count | Should -BeGreaterThan 0
    }

    It 'hidden COP is frozen' {
        $world = $S.World
        $cas = $world.sys.AddCasualty('MER2', 'fire', 0.3, $null, '', $true)
        $sev = [double]$cas['severity']
        for ($n = 0; $n -lt 10; $n++) { $world.Step(1.0, (New-BwSet 'nav')) }
        [double]$cas['severity'] | Should -Be $sev -Because "the DC COP isn't on screen, so the fire doesn't grow"
        $world.Step(1.0, (New-BwSet 'dc'))
        [double]$cas['severity'] | Should -BeGreaterThan $sev
    }

    It 'nav COP holds at most five targets' {
        $world = $S.World
        $world.Normalize()
        for ($n = 0; $n -lt 4; $n++) { Invoke-BwEvent $world @{ spawn_contact = [ordered]@{ course = 0; speed = 12; collision_in_min = 14 } } }
        Invoke-BwEvent $world @{ hostile = @{ range_nm = 15 } }
        $world.contacts.Count | Should -Not -BeGreaterThan 5
        @($world.contacts.Values | Where-Object { $_.kind -eq 'hostile' }).Count | Should -BeGreaterThan 0
    }

    It 'turning into a storm cell is rejected' {
        # The weather agent's sign error (port = +35) must never reach the watch.
        $warden = $S.Warden; $world = $S.World
        Invoke-BwEvent $world @{ weather = [ordered]@{ wind_kts = 38; visibility_nm = 1.0 } }
        # the heading within the agent's 40-degree authority that runs deepest into a cell
        $bad = $null; $worst = [double]::MaxValue
        for ($h = 50; $h -le 130; $h += 5) {
            $m = Get-MinClearance $world $h
            if ($m -lt $worst) { $worst = $m; $bad = $h }
        }
        Get-MinClearance $world $bad | Should -BeLessThan 0
        $it = New-BwIntent 'wx-01' 'set_heading' @{ heading = [double]$bad } 'rudder'
        Invoke-BwProcess $warden @($it)
        $it.status | Should -Be 'rejected'
        $it.policy_hits | Should -Contain 'INTO_HEAVY_WEATHER'
    }

    It 'inbound from over the horizon is invisible until it enters the COP' {
        $warden = $S.Warden; $world = $S.World
        Invoke-BwEvent $world @{ inbound = @{ count = 2 } }
        $v = $world.sys.View($world)
        $v['inbound'] | Should -HaveCount 0 -Because '28 nm out: not on the COP, nobody knows'
        $v['p_hit'] | Should -Be 0.0
        $it = New-BwIntent 'def-01' 'activate_defense' @{ systems = @('CIWS') } 'defense'
        Invoke-BwProcess $warden @($it)
        $it.policy_hits | Should -Contain 'NOT_WARRANTED' -Because "an agent can't act on a threat that isn't on the COP"
        for ($n = 0; $n -lt 30; $n++) { $world.Step() }   # 3 s ticks: 90 s at 500 kn = 12.5 nm
        $v = $world.sys.View($world)
        @($v['inbound']).Count | Should -BeGreaterThan 0
        foreach ($m in $v['inbound']) { [double]$m['range'] | Should -Not -BeGreaterThan 20 }
        $it2 = New-BwIntent 'def-01' 'activate_defense' @{ systems = @('CIWS') } 'defense'
        Invoke-BwProcess $warden @($it2)
        $it2.status | Should -Be 'escalated'
    }

    It 'activating defenses that are already active is not a decision' {
        $warden = $S.Warden; $world = $S.World
        $world.sys.Launch('OTH', 15, 1, 40)
        $world.sys.nodes['CIWS']['active'] = $true
        $it = New-BwIntent 'def-01' 'activate_defense' @{ systems = @('CIWS') } 'defense'
        Invoke-BwProcess $warden @($it)
        $it.status | Should -Be 'rejected'
        $it.reasons[0] | Should -Match 'already active'
    }

    It 'SM-2 launch needs batteries release and can splash the target' {
        $warden = $S.Warden; $world = $S.World
        $world.sys.Launch('OTH', 12, 1, 40)
        $tgt = [string]$world.sys.inbound[0]['id']
        $it = New-BwIntent 'def-01' 'engage_target' ([ordered]@{ target = $tgt; rounds = 2 }) "engage:$tgt"
        Invoke-BwProcess $warden @($it)
        $it.status | Should -Be 'escalated'
        $it.policy_hits | Should -Contain 'WEAPONS_NEED_HUMAN'
        $esc = Get-EscFor $warden $it
        $esc.approve_label | Should -Be 'Batteries Release'
        $world.sys.interceptors | Should -HaveCount 0
        [void]$warden.DecideEscalation($esc.id, $true, 'OOD', 'a')
        $world.sys.interceptors | Should -HaveCount 2
        $world.sys.magazine | Should -Be 22
        $world.sys.rng = New-Object Random(1)        # rng.seed(1)
        for ($n = 0; $n -lt 12; $n++) { $world.Step() }
        $world.sys.interceptors | Should -HaveCount 0 -Because 'interceptors reached the target'
        # The Python test's last assert (splash, or inbound empty, or inbound non-empty) is always true; kept as-is.
        ((@($world.sys.splashes | Where-Object { $_['label'] -eq 'SPLASH' }).Count -gt 0) -or
            $world.sys.inbound.Count -eq 0 -or $world.sys.inbound.Count -gt 0) | Should -BeTrue
    }

    It 'release beyond the envelope is held by fire control until 15 nm' {
        $warden = $S.Warden; $world = $S.World
        $warden.auto_act = $true
        $world.sys.Launch('OTH', 18, 1, 40)
        $tgt = [string]$world.sys.inbound[0]['id']
        $it = New-BwIntent 'def-01' 'engage_target' ([ordered]@{ target = $tgt; rounds = 2 }) "engage:$tgt"
        Invoke-BwProcess $warden @($it)
        $it.status | Should -Be 'executed'
        $world.sys.held.Keys | Should -Contain $tgt
        $world.sys.interceptors | Should -HaveCount 0
        for ($n = 0; $n -lt 12; $n++) { $world.Step() }  # 3 s ticks at 500 kn: inside 15 nm
        $world.sys.held.Keys | Should -Not -Contain $tgt
        $world.sys.magazine | Should -Be 22
    }

    It 'defense agent may only maneuver with something inbound' {
        $warden = $S.Warden; $world = $S.World
        $it = New-BwIntent 'def-01' 'set_speed' @{ speed_kn = 28 } 'main_engine'
        Invoke-BwProcess $warden @($it)
        $it.policy_hits | Should -Contain 'NOT_WARRANTED'
        $world.sys.Launch('OTH', 12, 1, 40)
        $it2 = New-BwIntent 'def-01' 'set_speed' @{ speed_kn = 28 } 'main_engine'
        Invoke-BwProcess $warden @($it2)
        @('executed', 'escalated') | Should -Contain $it2.status
    }

    It 'submarine is hidden until sonar holds it then fires torpedoes' {
        $world = $S.World
        Invoke-BwEvent $world @{ submarine = [ordered]@{ bearing = 0; range_nm = 9.0; speed = 12 } }
        @($world.Cop()['contacts'] | Where-Object { $_['domain'] -eq 'subsurface' }).Count | Should -Be 0 -Because 'quiet sub: not on the COP'
        for ($n = 0; $n -lt 200; $n++) {
            $world.Step()
            if ($world.sys.torpedoes.Count -gt 0) { break }
        }
        $sub = @($world.contacts.Values | Where-Object { $_.domain -eq 'subsurface' })[0]
        $sub.hidden | Should -BeFalse
        $sub.classification | Should -Be 'HOSTILE'
        $world.sys.torpedoes | Should -HaveCount 2
    }

    It 'running from a torpedo outlasts its fuel' {
        $world = $S.World
        $ss = $world.sys
        $world.ship.speed = 30; $world.ship.cmd_speed = 30   # flank: a 50-kn torpedo closes at 20 kn and runs dry first
        $world.ship.heading = 270; $world.ship.cmd_heading = 270      # torpedo bearing 090: running directly away
        $ss.torpedoes.Add((ConvertTo-BwObject ([ordered]@{ id = 'T1'; from = 'X'; range = 4.0; bearing = 90.0; run_left = 9.0
                        nixie_tried = $true })))
        for ($n = 0; $n -lt 400; $n++) { $world.Step() }
        $ss.torpedoes | Should -HaveCount 0
        $ss.torp_hits | Should -Be 0
    }

    It 'cease fire destructs weapons and blocks new launches' {
        $warden = $S.Warden; $world = $S.World
        $world.sys.Launch('OTH', 12.0, 1, 45.0)
        $it = New-BwIntent 'def-01' 'engage_target' ([ordered]@{ target = 'OTH-V1'; rounds = 2 }) 'engage:OTH-V1'
        $warden.auto_act = $true
        Invoke-BwProcess $warden @($it)
        $it.status | Should -Be 'executed'
        $it.decided_by | Should -Be 'auto-act'
        $world.sys.interceptors | Should -HaveCount 2
        $warden.CeaseFire('operator') | Should -Be 2
        $world.sys.interceptors | Should -HaveCount 0
        $again = New-BwIntent 'def-01' 'engage_target' ([ordered]@{ target = 'OTH-V1'; rounds = 1 }) 'engage:OTH-V1'
        Invoke-BwProcess $warden @($again)
        $again.status | Should -Be 'rejected'
        $again.policy_hits | Should -Contain 'NOT_WARRANTED'
    }

    It "operator can pick the agent's second option" {
        $warden = $S.Warden; $world = $S.World
        [void]$world.sys.AddCasualty('MER2', 'fire', 0.6, $null, '', $true)
        $world.sys.SetGq($true)
        foreach ($t in $world.sys.teams.Values) { $t['status'] = 'standby' }
        $cas = @($world.sys.casualties.Keys)[0]
        $it = New-BwIntent 'dca-01' 'dispatch_team' ([ordered]@{ team = 'RT7'; casualty = $cas }) 'team:RT7' @{
            alternative = [ordered]@{ action = 'dispatch_team'; params = [ordered]@{ team = 'RT6'; casualty = $cas }; rationale = 'closer' } }
        Invoke-BwProcess $warden @($it)
        $esc = Get-EscFor $warden $it
        $proposal = Invoke-BwPrivate $warden 'Proposal' @($esc)
        ([string]$proposal['alternative']['value']).StartsWith('dispatch_team') | Should -BeTrue
        [void]$warden.DecideEscalation($esc.id, $false, 'operator', 'b')
        $world.sys.teams['RT6']['casualty'] | Should -Be $cas
        $world.sys.teams['RT7']['casualty'] | Should -BeNullOrEmpty
    }
}
