# Pester 5 checks for the launch page (frontend\js\welcome.js, css\welcome.css): both windows load it, it opens
# once and remembers it was closed, and it can be reopened. Reads the frontend sources only; no browser.
# Windows PowerShell 5.1. ASCII only.  Invoke-Pester host\tests\Welcome.Tests.ps1

BeforeAll {
    $script:Frontend = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'frontend'
    $script:Js = [IO.File]::ReadAllText((Join-Path $script:Frontend 'js\welcome.js'))
    $script:Index = [IO.File]::ReadAllText((Join-Path $script:Frontend 'index.html'))
    $script:Control = [IO.File]::ReadAllText((Join-Path $script:Frontend 'control.html'))
}

Describe 'Launch page' {
    It 'both pages load the script and the stylesheet' {
        foreach ($page in @($script:Index, $script:Control)) {
            $page | Should -Match '<script src="js/welcome\.js" defer></script>'
            $page | Should -Match '<link rel="stylesheet" href="css/welcome\.css">'
        }
    }

    It 'opens only until it has been closed once' {
        $script:Js | Should -Match "KEY = 'bw-welcome'"
        $script:Js | Should -Match 'if \(!seen\(\)\) open\(null\)'
        $script:Js | Should -Match "localStorage\.setItem\(KEY, 'seen'\)"
    }

    It 'is an accessible dialog that Escape closes' {
        $script:Js | Should -Match 'role="dialog" aria-modal="true"'
        $script:Js | Should -Match "e\.key === 'Escape'"
    }

    It 'can be reopened from the header' {
        $script:Js | Should -Match "btn\.textContent = 'Welcome'"
    }

    It 'the emblem it shows ships with the frontend' {
        $script:Js | Should -Match 'img/emblem-760\.png'
        Test-Path (Join-Path $script:Frontend 'img\emblem-760.png') | Should -BeTrue
    }
}
