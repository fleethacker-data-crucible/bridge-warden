# Pester 5 checks for BRIDGE-WARDEN-03: keyboard focus survives a live re-render.
# Static source checks plus the Node behaviour test (FocusRestore.node.test.js), which runs only when node is on PATH.
# No C# compile. Windows PowerShell 5.1. ASCII only.  Invoke-Pester src\tests\FocusRestore.Tests.ps1

BeforeDiscovery {
    $script:HaveNode = [bool](Get-Command node -ErrorAction SilentlyContinue)
}

BeforeAll {
    $script:App = [IO.File]::ReadAllText((Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'frontend\js\app.js'))
    $script:Watch = [IO.File]::ReadAllText((Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'frontend\js\watch.js'))
}

Describe 'Focus stability during live refresh (BRIDGE-WARDEN-03)' {
    It 'pauses a live rebuild for Enter and Space the same way it pauses for a pointer press' {
        $script:App | Should -Match 'let pointerDown = false, keyHeld = false'
        $script:App | Should -Match 'function activationKey\(e\) \{ return e\.key === ''Enter'' \|\| e\.key === '' ''; \}'
        $script:App | Should -Match 'if \(!s \|\| pointerDown \|\| keyHeld \|\| ui\.dragging\) return'
    }

    It 'puts keyboard focus back on the same control after the markup is swapped' {
        $script:App | Should -Match 'function setHtml\(el, html\)'
        $script:App | Should -Match 'focusToken\(el, active\)'
        $script:App | Should -Match 'next\.focus\(\{ preventScroll: true \}\)'
    }

    It 'keeps an open review attached to its decision when the list is redrawn' {
        $script:App | Should -Match 'function cardMarker\(el\)'
        $script:App | Should -Match "node\.tagName === 'DETAILS'"
        $script:Watch | Should -Match 'if \(!ui\.sel \|\| !open\.find\(e => e\.id === ui\.sel\)\) ui\.sel = open\.length \? open\[0\]\.id : null'
    }

    It 'runs the behaviour tests (Node)' -Skip:(-not $script:HaveNode) {
        $out = & node --test (Join-Path $PSScriptRoot 'FocusRestore.node.test.js') 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { Write-Host $out }
        $LASTEXITCODE | Should -Be 0
    }
}
