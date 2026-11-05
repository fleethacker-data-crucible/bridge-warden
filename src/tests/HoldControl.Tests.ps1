# Pester 5 checks for BRIDGE-WARDEN-02: keyboard equivalent for the hold-to-stop/resume button.
# Static source checks plus the Node behaviour test (HoldControl.node.test.js), which runs only when node is on PATH.
# No C# compile. Windows PowerShell 5.1. ASCII only.  Invoke-Pester src\tests\HoldControl.Tests.ps1

BeforeDiscovery {
    $script:HaveNode = [bool](Get-Command node -ErrorAction SilentlyContinue)
}

BeforeAll {
    $script:Frontend = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'frontend'
    $script:Html = [IO.File]::ReadAllText((Join-Path $script:Frontend 'control.html'))
    $script:Js = [IO.File]::ReadAllText((Join-Path $script:Frontend 'js\control.js'))
    $script:Css = [IO.File]::ReadAllText((Join-Path $script:Frontend 'css\styles.css'))
}

Describe 'Hold button keyboard equivalent (BRIDGE-WARDEN-02)' {
    It 'keeps the hold control a native button with a hint and a polite live region' {
        $script:Html | Should -Match '<button type="button" class="hold-btn[^"]*" id="hold" aria-describedby="hold-hint"'
        $script:Html | Should -Match 'id="hold-hint"'
        $script:Html | Should -Match 'id="hold-status"[^>]*role="status"[^>]*aria-live="polite"'
    }

    It 'handles keydown, keyup, blur and click on the hold button' {
        foreach ($ev in 'keydown', 'keyup', 'blur', 'click') {
            $script:Js | Should -Match "btn\.addEventListener\('$ev'"
        }
    }

    It 'uses the same 2 second hold for pointer and keyboard and ignores key repeat' {
        $script:Js | Should -Match 'const HOLD_MS = 2000'
        $script:Js | Should -Match 'e\.repeat'
    }

    It 'shows a focus ring and keeps the visible label' {
        $script:Css | Should -Match '\.hold-btn:focus-visible\s*\{[^}]*outline'
        $script:Html | Should -Match '<span class="sub">Hold 2s to engage</span>'
    }

    It 'runs the behaviour tests (Node)' -Skip:(-not $script:HaveNode) {
        $out = & node --test (Join-Path $PSScriptRoot 'HoldControl.node.test.js') 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { Write-Host $out }
        $LASTEXITCODE | Should -Be 0
    }
}
