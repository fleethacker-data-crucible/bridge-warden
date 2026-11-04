# Pester 5 guard: the Fleet look must stay the default. See "Fleet UI is mandatory" in AGENTS.md.
# No browser and no C# compile; reads the frontend sources only. Windows PowerShell 5.1. ASCII only.
# Invoke-Pester host\tests\FleetDefault.Tests.ps1

BeforeAll {
    $script:Frontend = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'frontend'
    $script:Skin = [IO.File]::ReadAllText((Join-Path $script:Frontend 'js\skin.js'))
    $script:Index = [IO.File]::ReadAllText((Join-Path $script:Frontend 'index.html'))
    $script:Control = [IO.File]::ReadAllText((Join-Path $script:Frontend 'control.html'))
}

Describe 'Fleet look is the default (AGENTS.md: Fleet UI is mandatory)' {
    It 'skin.js starts on the fleet skin' {
        $script:Skin | Should -Match "var skin = 'fleet';"
    }

    It 'skin.js lists fleet first in the look order' {
        $script:Skin | Should -Match "var ORDER = \['fleet',"
    }

    It 'index.html links css/fleet-skin.css' {
        $script:Index | Should -Match '<link rel="stylesheet" href="css/fleet-skin\.css">'
    }

    It 'control.html links css/fleet-skin.css' {
        $script:Control | Should -Match '<link rel="stylesheet" href="css/fleet-skin\.css">'
    }
}
