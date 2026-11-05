# Pester 5: the lookout -> model decider -> Warden chain (NavCell.cs), its fallbacks, and the red-team containment.
# Windows PowerShell 5.1. ASCII only.  Invoke-Pester src\tests
# The model is a fake (Fakes.Chat): these tests check the plumbing and the safety properties, not the model's judgment.

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    $script:RealChat = [BridgeWarden.Llm]::PROVIDERS['local_chat']
    $script:Inj = [BridgeWarden.App].GetField('Injections', [Reflection.BindingFlags]'NonPublic, Static').GetValue($null)

    # A fresh ship and Warden on one picture; the model answers with $Replies (the last one repeats).
    function New-Cell([string]$Cop, [string[]]$Replies) {
        $prompts = New-Object 'System.Collections.Generic.List[string]'
        [BridgeWarden.Llm]::PROVIDERS['local_chat'] = [BridgeWardenTests.Fakes]::Chat($Replies, $true, $prompts)
        [Environment]::SetEnvironmentVariable('LLM_LOCAL_MODEL', 'test-model')
        $s = New-BwSetup 1.0
        $s.Warden.policies['require_llm_provenance'] = $true      # the real rule: only verified model calls (or reflexes) pass
        $s.Warden.auto_act = $true
        $s | Add-Member -NotePropertyName On -NotePropertyValue (New-BwSet @($Cop))
        $s | Add-Member -NotePropertyName Prompts -NotePropertyValue $prompts
        return $s
    }
    function Invoke-Inject($S, [string]$Id) { [void]$S.World.ApplyEvent([BridgeWarden.Py]::DeepCopy($script:Inj[$Id])) }
    # Tick until $Until is true (the decider answers on its own thread), at most ~6 s.
    function Step-Until($S, [scriptblock]$Until) {
        for ($n = 0; $n -lt 120; $n++) {
            $S.World.Step(1.0, $S.On)
            $S.Warden.Step($S.On, 1.0)
            if (& $Until) { return $true }
            Start-Sleep -Milliseconds 50
        }
        return $false
    }
    function Get-Intents($S, [string]$Agent) { return , @($S.Warden.intents.Values | Where-Object { $_.agent_id -eq $Agent }) }
}

Describe 'Lookouts, deciders and Warden (NavCell.cs)' {
    BeforeEach {
        [BridgeWarden.Paths]::Root = $script:App
        [BridgeWarden.Py]::FakeTime = 0                            # real clock: the decider runs on its own thread
        [BridgeWarden.LlmAgent]::ResetMemory()
        $script:S = $null
    }
    AfterEach {
        if ($script:S) { $script:S.Ledger.Close() }
        [BridgeWarden.Llm]::PROVIDERS['local_chat'] = $script:RealChat
        [BridgeWarden.NavDecider]::OutageUntil = 0
    }

    It 'a lookout reports options and the order the model picks goes through Warden in the lookout''s name' {
        $script:S = New-Cell 'nav' @('1a | turn right to give way')
        Invoke-Inject $S 'close-crossing'
        (Step-Until $S { (Get-Intents $S 'col-01').Count -gt 0 }) | Should -BeTrue
        $col = $S.Warden.agents['col-01']
        $col.cue.options.Count | Should -BeGreaterThan 1
        $it = (Get-Intents $S 'col-01')[0]
        $it.action | Should -Be 'set_heading'
        $it.provenance['decided_by'] | Should -Be 'tao-01'
        $it.provenance['option'] | Should -Match 'Turn right'
        $it.pipeline['authenticate'] | Should -Be 'pass'            # verified model call: CALLS has its SHA-256
        $it.status | Should -Be 'executed'
        $S.Prompts[0] | Should -Match 'Glossary'                     # the decider's prompt carries the glossary skill
    }

    It 'when the model fails, an urgent alert gets the doctrine option by reflex, marked and checked by Warden' {
        $script:S = New-Cell 'nav' @('unused')
        [BridgeWarden.NavDecider]::OutageUntil = [BridgeWarden.Py]::Now() + 60
        [BridgeWarden.NavDecider]::ReflexDeadlineS = 0.5
        Invoke-Inject $S 'torpedo-close'
        (Step-Until $S { (Get-Intents $S 'asw-01').Count -gt 0 }) | Should -BeTrue
        $its = Get-Intents $S 'asw-01'
        $its | ForEach-Object { $_.provenance['reflex'] | Should -BeTrue }
        @($its | ForEach-Object { $_.action }) | Should -Contain 'set_heading'
        @($its | ForEach-Object { $_.action }) | Should -Contain 'activate_defense'
        $its[0].pipeline['authenticate'] | Should -Be 'pass'
    }

    It 'reflex orders are refused when the policy turns them off' {
        $script:S = New-Cell 'nav' @('unused')
        $S.Warden.policies['reflex_fallback'] = $false
        [BridgeWarden.NavDecider]::OutageUntil = [BridgeWarden.Py]::Now() + 60
        [BridgeWarden.NavDecider]::ReflexDeadlineS = 0.5
        Invoke-Inject $S 'torpedo-close'
        (Step-Until $S { $false }) | Out-Null
        (Get-Intents $S 'asw-01').Count | Should -Be 0
    }

    It 'a Warden block is fed back to the decider: the option is marked, the alert re-asked, the lesson kept' {
        $script:S = New-Cell 'nav' @('1a | turn right')
        Invoke-Inject $S 'close-crossing'
        $S.World.sys.Fail('STEER', 'test')                          # every rudder order is now refused
        (Step-Until $S { (Get-Intents $S 'col-01' | Where-Object { $_.status -eq 'rejected' }).Count -gt 0 }) | Should -BeTrue
        (Step-Until $S { $S.Prompts.Count -ge 2 }) | Should -BeTrue  # asked again after the block
        $S.Prompts[1] | Should -Match 'Warden blocks you have learned from'   # the lesson is in the next prompt
        $file = Join-Path ([BridgeWarden.LlmAgent]::LESSONS) 'nav_tao.decider.md'
        Get-Content $file -Raw | Should -Match 'STEERING_LOST|Steering'
    }

    It 'the same order is not sent twice while it is pending' {
        $script:S = New-Cell 'nav' @('1a | turn right')
        $S.Warden.auto_act = $false                                  # big turns wait for the watch
        Invoke-Inject $S 'torpedo-close'
        (Step-Until $S { (Get-Intents $S 'asw-01').Count -gt 0 }) | Should -BeTrue
        for ($n = 0; $n -lt 40; $n++) { $S.World.Step(1.0, $S.On); $S.Warden.Step($S.On, 1.0) }
        $heads = @(Get-Intents $S 'asw-01' | Where-Object { $_.action -eq 'set_heading' })
        $heads.Count | Should -Be 1
    }

    It 'a compromised commander is blocked and quarantined, and its lookouts keep answering by reflex' {
        # The hijacked model ignores the lookouts and orders the ship directly with actions it was never granted.
        $rogue = '{"actions": [{"action": "set_heading", "params": {"heading": 200}}, {"action": "set_speed", "params": {"speed_kn": 30}}, ' +
                 '{"action": "engage_target", "params": {"target": "TN4001", "rounds": 4}}]}'
        $script:S = New-Cell 'nav' @($rogue)
        [BridgeWarden.NavDecider]::ReflexDeadlineS = 0.5
        $tao = $S.Warden.agents['tao-01']
        $tao.rogue = $true; $tao.enabled = $true; $tao.min_gap_s = 0
        Invoke-Inject $S 'torpedo-close'
        (Step-Until $S { $tao.identity.status -eq 'quarantined' }) | Should -BeTrue
        $own = Get-Intents $S 'tao-01'
        $own.Count | Should -BeGreaterThan 0
        $own | ForEach-Object { $_.status | Should -Be 'rejected' }  # nothing the hijacked model asked for happened
        @($own | ForEach-Object { $_.policy_hits }) | Should -Contain 'TOOL_NOT_GRANTED'
        (Step-Until $S { (Get-Intents $S 'asw-01').Count -gt 0 }) | Should -BeTrue
        (Get-Intents $S 'asw-01')[0].provenance['reflex'] | Should -BeTrue
    }
}

Describe 'Patch cables (casualty power)' {
    It 'a team rigs a cable from a live panel and the equipment comes back on line' {
        $sys = New-Object BridgeWarden.ShipSystems(7)
        $sys.real_per_sim = 1.0
        $sys.Fail('LC-4', 'test')
        $sys.Ok('STEER') | Should -BeFalse
        $sys.Validate('rig_casualty_power', (ConvertTo-BwObject @{ team = 'RT9'; equipment = 'STEER'; source = '3SB' })) | Should -BeNullOrEmpty
        [void]$sys.Apply('rig_casualty_power', (ConvertTo-BwObject @{ team = 'RT9'; equipment = 'STEER'; source = '3SB' }))
        $sys.breakers['CP-STEER']['closed'] | Should -BeFalse          # not a path until the team has rigged it
        for ($i = 0; $i -lt 60; $i++) { $sys.Step(1.0, $null, [string[]]@('systems')) }
        $sys.breakers['CP-STEER']['closed'] | Should -BeTrue
        $sys.Ok('STEER') | Should -BeTrue
    }

    It 'a cable cannot restore damaged equipment or feed from a dead panel' {
        $sys = New-Object BridgeWarden.ShipSystems(7)
        $sys.Fail('STEER', 'test')
        $sys.Validate('rig_casualty_power', (ConvertTo-BwObject @{ team = 'RT9'; equipment = 'STEER'; source = '3SB' })) | Should -Match 'damaged'
        $sys2 = New-Object BridgeWarden.ShipSystems(7)
        $sys2.Fail('LC-4', 'test'); $sys2.Fail('LC-3', 'test')
        $sys2.Validate('rig_casualty_power', (ConvertTo-BwObject @{ team = 'RT9'; equipment = 'STEER'; source = 'LC-3' })) | Should -Match 'not live|bus'
    }
}
