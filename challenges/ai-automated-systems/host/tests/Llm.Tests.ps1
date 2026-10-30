# Pester 5 port of backend\tests\test_llm.py: required LLM wrapper tests (docs/llm-wrapper.md, section 6).
# Windows PowerShell 5.1. ASCII only.  Invoke-Pester host\tests
# Streaming tests use a fake OpenAI-compatible server on CI-only port 18781 (edge-ops PORTS.md: 18000-18999 for CI).

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    $script:SsePort = 18781
    $script:LlmVars = @('LLM_MODE', 'LLM_PROVIDER', 'LLM_MODEL', 'LLM_LOCAL_MODEL', 'LLM_TIMEOUT_S', 'LLM_RECORD',
        'LLM_REASONING_EFFORT', 'LLM_INCLUDE_THOUGHTS', 'LLM_BASE_URL')
    $deg = [string][char]0xB0
    $script:SAMPLE = @{
        summarize_conflict   = [ordered]@{ kind = 'resource'; resource = 'main_engine'; winner_agent = 'col-01'
            winner_objective = 'Keep CPA'; winner_value = 'speed 8 kn'; winner_priority = 90
            loser_agent = 'nav-01'; loser_objective = 'Keep ETA'; loser_value = 'speed 16 kn'; loser_priority = 40 }
        summarize_escalation = [ordered]@{ agent = 'wx-01'; value = "heading 055$deg"; objective = 'Avoid heavy weather'
            why = "turn above 30$deg"; rationale = 'Wind 38 kts on the beam.' }
        explain_intent       = [ordered]@{ agent = 'nav-01'; action = 'set_speed'; value = 'speed 16 kn'; stages = 'constraints fail'
            status = 'rejected'; reasons = 'Predicted CPA 0.4 nm'; decided_by = 'warden' }
        agent_decide         = [ordered]@{ role = 'Navigation'; state = '{"ownship": {"speed": 16}}'
            tools = 'set_speed: {"speed_kn": number}' }
    }

    function Get-Sample([string]$Task) { return , (ConvertTo-BwObject $script:SAMPLE[$Task]) }

    function Invoke-Llm([string]$Task, $OnChunk = $null) {
        return [BridgeWarden.Llm]::Run($Task, (Get-Sample $Task), $null, $OnChunk)
    }

    function Set-Env([string]$Name, [string]$Value) { [Environment]::SetEnvironmentVariable($Name, $Value) }

    # Remove a variable the way monkeypatch.delenv does, including a blank value Llm keeps for an empty .env entry.
    function Clear-Env([string]$Name) {
        [Environment]::SetEnvironmentVariable($Name, $null)
        $f = [BridgeWarden.Llm].GetField('_blank', [Reflection.BindingFlags]'NonPublic, Static')
        $blank = $f.GetValue($null)
        [System.Threading.Monitor]::Enter($blank)
        try { [void]$blank.Remove($Name) } finally { [System.Threading.Monitor]::Exit($blank) }
    }

    function Set-Provider([string]$Name, $Fn) { [BridgeWarden.Llm]::PROVIDERS[$Name] = $Fn }

    function Set-SeedCache([string]$Task, [string]$Text) {
        $t = [BridgeWarden.LlmTasks]::TASKS[$Task]
        $sample = $script:SAMPLE[$Task]
        $ins = New-Object 'System.Collections.Generic.Dictionary[string,object]'
        foreach ($k in $t.fields) { $ins[$k] = if ($sample.Contains($k)) { Get-BwBase (ConvertTo-BwObject $sample[$k]) } else { "" } }
        $key = [BridgeWarden.Llm]::Key($Task, $ins)
        $cache = ConvertTo-BwObject ([ordered]@{ $key = [ordered]@{ task = $Task; text = $Text } })
        $path = [BridgeWarden.Llm]::CACHE_PATH
        New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
        [IO.File]::WriteAllText($path, [BridgeWarden.Json]::Write($cache), (New-Object Text.UTF8Encoding($false)))
        [BridgeWarden.Llm]::ReloadCache()
    }

    # fake_provider(): a local server whose /chat/completions streams these deltas (JSON objects) as SSE.
    function Start-FakeProvider([string[]]$Deltas) {
        Set-Env 'LLM_BASE_URL' "http://localhost:$($script:SsePort)/v1"
        $script:Sse = New-Object BridgeWardenTests.FakeSse($script:SsePort, $Deltas)
        return $script:Sse
    }
}

Describe 'LLM wrapper (test_llm.py)' {
    BeforeEach {
        # isolated(): fresh cache file, no logger, default settings, real providers restored after each test.
        # CACHE_PATH is Paths.Fixtures\llm_cache.json, so the cache moves with a temp Paths.Root.
        $script:Tmp = New-BwTempDir
        [BridgeWarden.Paths]::Root = $script:Tmp
        [BridgeWarden.Llm]::ReloadCache()
        foreach ($v in $script:LlmVars) { Clear-Env $v }
        $script:SavedProviders = @{}
        foreach ($k in @([BridgeWarden.Llm]::PROVIDERS.Keys)) { $script:SavedProviders[$k] = [BridgeWarden.Llm]::PROVIDERS[$k] }
        [BridgeWarden.Llm]::SetLogger($null)
        $script:Sse = $null
    }
    AfterEach {
        if ($script:Sse) { $script:Sse.Dispose(); $script:Sse = $null }
        foreach ($k in $script:SavedProviders.Keys) { [BridgeWarden.Llm]::PROVIDERS[$k] = $script:SavedProviders[$k] }
        foreach ($v in $script:LlmVars) { Clear-Env $v }
        [BridgeWarden.Llm]::SetLogger($null)
        [BridgeWarden.Paths]::Root = $script:App
        [BridgeWarden.Llm]::ReloadCache()
    }

    It 'every task returns fallback when off' {
        Set-Env 'LLM_MODE' 'off'
        foreach ($name in @([BridgeWarden.LlmTasks]::TASKS.Keys)) {
            $r = Invoke-Llm $name
            $r.ok | Should -BeTrue
            $r.source | Should -Be 'fallback'
            $r.text | Should -Not -BeNullOrEmpty
        }
    }

    It 'timeout falls through to cache then fallback within limit' {
        Set-Env 'LLM_MODE' 'live'
        Set-Env 'LLM_TIMEOUT_S' '0.3'
        Set-Provider 'live' ([BridgeWardenTests.Fakes]::Sleepy(2000, 'too late'))

        Set-SeedCache 'summarize_conflict' 'cached answer'
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $r = Invoke-Llm 'summarize_conflict'
        $sw.Stop()
        $r.source | Should -Be 'cache'
        $r.text | Should -Be 'cached answer'
        $sw.Elapsed.TotalSeconds | Should -BeLessThan 0.6
        $r.error | Should -Match 'timeout'

        $sw = [Diagnostics.Stopwatch]::StartNew()
        $r = Invoke-Llm 'summarize_escalation'      # nothing cached for this task
        $sw.Stop()
        $r.source | Should -Be 'fallback'
        $r.ok | Should -BeTrue
        $sw.Elapsed.TotalSeconds | Should -BeLessThan 0.6
    }

    It 'cached mode is deterministic' {
        Set-Env 'LLM_MODE' 'cached'
        Set-SeedCache 'summarize_conflict' 'same every time'
        $a = Invoke-Llm 'summarize_conflict'
        $b = Invoke-Llm 'summarize_conflict'
        $a.text | Should -Be 'same every time'
        $b.text | Should -Be 'same every time'
        $a.source | Should -Be 'cache'
        $b.source | Should -Be 'cache'
        # A cache miss is deterministic too: same rule text both times.
        $c = Invoke-Llm 'explain_intent'
        $d = Invoke-Llm 'explain_intent'
        $c.text | Should -Be $d.text
        $c.source | Should -Be 'fallback'
    }

    It 'switching provider to <provider> needs no code change' -ForEach @(
        @{ provider = 'openai'; model = 'gpt-x' }
        @{ provider = 'xai'; model = 'grok-x' }
        @{ provider = 'anthropic'; model = 'claude-x' }
    ) {
        Set-Env 'LLM_MODE' 'live'
        Set-Env 'LLM_PROVIDER' $provider
        Set-Env 'LLM_MODEL' $model
        Set-Provider 'live' ([BridgeWardenTests.Fakes]::Echo())
        $r = Invoke-Llm 'summarize_conflict'
        $r.source | Should -Be 'live'
        $r.provider | Should -Be $provider
        $r.model | Should -Be $model
        $r.text | Should -Be "answer from $provider/$model"
    }

    It 'every call is logged without text or keys' {
        Set-Env 'LLM_MODE' 'off'
        $col = New-Object BridgeWardenTests.Collector
        [BridgeWarden.Llm]::SetLogger($col.Row())
        [void](Invoke-Llm 'summarize_conflict')
        $col.rows | Should -HaveCount 1
        foreach ($k in 'task', 'source', 'provider', 'model', 'latency_ms', 'ok') { $col.rows[0].Keys | Should -Contain $k }
        $col.rows[0].Keys | Should -Not -Contain 'text'
    }

    It 'record mode fills the cache' {
        Set-Env 'LLM_MODE' 'live'
        Set-Env 'LLM_PROVIDER' 'openai'
        Set-Env 'LLM_MODEL' 'gpt-x'
        Set-Env 'LLM_RECORD' '1'
        Set-Provider 'live' ([BridgeWardenTests.Fakes]::Const('recorded answer'))
        [void](Invoke-Llm 'summarize_conflict')          # live answer is written to the cache
        Set-Env 'LLM_MODE' 'cached'
        [BridgeWarden.Llm]::ReloadCache()
        $r = Invoke-Llm 'summarize_conflict'
        $r.source | Should -Be 'cache'
        $r.text | Should -Be 'recorded answer'
    }

    # ------------------------------------------------------------ streaming (docs/things-to-check/streaming-latency-checklist.md)
    It 'local call streams thoughts and content with timings' {
        $sse = Start-FakeProvider @('{"reasoning_content": "weigh CPA. "}', '{"content": "Rejected: "}', '{"content": "CPA too close."}')
        Set-Env 'LLM_MODE' 'local'
        Set-Env 'LLM_REASONING_EFFORT' 'low'
        $col = New-Object BridgeWardenTests.Collector
        $r = Invoke-Llm 'explain_intent' $col.Chunk()
        $r.source | Should -Be 'local'
        $r.text | Should -BeExactly 'Rejected: CPA too close.'
        $r.thoughts | Should -BeExactly 'weigh CPA. '
        $got = @($col.chunks | ForEach-Object { $_[0] + '|' + $_[1] })
        $got | Should -HaveCount 3
        $got[0] | Should -BeExactly 'thought|weigh CPA. '
        $got[1] | Should -BeExactly 'content|Rejected: '
        $got[2] | Should -BeExactly 'content|CPA too close.'
        $seen = $sse.seen[0] | ConvertFrom-Json
        $seen.stream | Should -Be $true
        $seen.reasoning_effort | Should -Be 'low'
        $seen.PSObject.Properties.Name | Should -Not -Contain 'extra_body'
        $r.prompt_chars | Should -BeGreaterThan 0
        $r.first_token_ms | Should -Not -BeLessThan $r.headers_ms
        $r.headers_ms | Should -Not -BeLessThan 0
    }

    It 'invalid reasoning effort is not sent' {
        $sse = Start-FakeProvider @('{"content": "ok"}')
        Set-Env 'LLM_MODE' 'local'
        Set-Env 'LLM_REASONING_EFFORT' 'minimal'      # Gemini answers 400 to this
        Set-Env 'LLM_INCLUDE_THOUGHTS' '1'
        [void](Invoke-Llm 'explain_intent')
        $seen = $sse.seen[0] | ConvertFrom-Json
        $seen.PSObject.Properties.Name | Should -Not -Contain 'reasoning_effort'
        $seen.extra_body.google.thinking_config.include_thoughts | Should -Be $true
    }

    It 'gemini thought parts are kept out of the answer' {
        [void](Start-FakeProvider @('{"content": "plan it", "extra_content": {"google": {"thought": true}}}', '{"content": "Answer."}'))
        Set-Env 'LLM_MODE' 'local'
        $r = Invoke-Llm 'explain_intent'
        $r.text | Should -BeExactly 'Answer.'
        $r.thoughts | Should -BeExactly 'plan it'
    }

    It 'stream events end with done and fallback arrives whole' {
        Set-Env 'LLM_MODE' 'off'
        $events = @([BridgeWarden.Llm]::Stream('explain_intent', (Get-Sample 'explain_intent')))
        $kinds = @($events | ForEach-Object { $_.Key })
        $kinds[0] | Should -Be 'thinking'
        $kinds[-2] | Should -Be 'replace'
        $kinds[-1] | Should -Be 'done'
        $events[-1].Value.source | Should -Be 'fallback'
        $events[-2].Value | Should -BeExactly $events[-1].Value.text
    }

    It 'log row has timings but no text or thoughts' {
        [void](Start-FakeProvider @('{"reasoning": "secret-ish"}', '{"content": "hi"}'))
        Set-Env 'LLM_MODE' 'local'
        $col = New-Object BridgeWardenTests.Collector
        [BridgeWarden.Llm]::SetLogger($col.Row())
        [void](Invoke-Llm 'explain_intent')
        $col.rows[0].Keys | Should -Not -Contain 'text'
        $col.rows[0].Keys | Should -Not -Contain 'thoughts'
        $col.rows[0]['prompt_chars'] | Should -BeGreaterThan 0
    }

    It 'dotenv strips inline comments' {
        # backend/.env copied from .env.example keeps its inline comments; values must not include them.
        # Python patches llm.BACKEND to tmp/backend; here Paths.Root is already the temp folder, so BACKEND is tmp\backend.
        New-Item -ItemType Directory -Force (Join-Path $script:Tmp 'backend') | Out-Null
        $text = "BW_T_NUM=45   # seconds`nBW_T_URL=http://x/v1`t# tab`nBW_T_Q=`"a # b`"`n"
        [IO.File]::WriteAllText((Join-Path $script:Tmp 'backend\.env'), $text, (New-Object Text.UTF8Encoding($false)))
        $names = @('BW_T_NUM', 'BW_T_URL', 'BW_T_Q')
        foreach ($k in $names) { Clear-Env $k }
        try {
            [BridgeWarden.Llm]::LoadDotenv()
            [Environment]::GetEnvironmentVariable('BW_T_NUM') | Should -BeExactly '45'
            [Environment]::GetEnvironmentVariable('BW_T_URL') | Should -BeExactly 'http://x/v1'
            [Environment]::GetEnvironmentVariable('BW_T_Q') | Should -BeExactly 'a # b'
        }
        finally {
            foreach ($k in $names) { Clear-Env $k }
        }
    }
}
