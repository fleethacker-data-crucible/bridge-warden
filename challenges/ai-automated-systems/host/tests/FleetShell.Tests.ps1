# Pester 5 guard: the shared Fleet UI is the default look. See "Fleet UI is mandatory" in AGENTS.md.
# No browser and no C# compile; reads the frontend sources only. Windows PowerShell 5.1. ASCII only.
# Invoke-Pester host	ests\FleetShell.Tests.ps1

BeforeAll {
    $script:Frontend = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'frontend'
    $script:Theme = [IO.File]::ReadAllText((Join-Path $script:Frontend 'js/theme.js'))
    $script:Index = [IO.File]::ReadAllText((Join-Path $script:Frontend 'index.html'))
    $script:Control = [IO.File]::ReadAllText((Join-Path $script:Frontend 'control.html'))
}

Describe 'Fleet UI is the default look' {
    It 'both windows load the shared fleet.css and the app layer' {
        foreach ($page in @($script:Index, $script:Control)) {
            $page | Should -Match '<link rel="stylesheet" href="vendor/fleet/fleet\.css">'
            $page | Should -Match '<link rel="stylesheet" href="css/fleet-ui\.css">'
            $page | Should -Match 'data-app="bridge-warden"'
        }
    }

    It 'the vendored fleet.css is present and is the Fleet UI file' {
        $css = [IO.File]::ReadAllText((Join-Path $script:Frontend 'vendor/fleet/fleet.css'))
        $css | Should -Match 'Fleet UI v0\.2'
        $css | Should -Match '--fl-accent'
    }

    It 'theme.js starts on Night watch (dark) when nothing is saved' {
        $script:Theme | Should -Match "v : 'dark'; } catch"
        $script:Theme | Should -Match "bridgewarden-theme"
    }

    It 'Settings offers the Appearance choice and a gear control opens it' {
        $script:Index | Should -Match 'id="gear"'
        $script:Index | Should -Match 'data-page="settings"'
    }

    It 'the main window carries a persistent simulation banner and a typed safe-stop' {
        $script:Index | Should -Match 'class="fl-simbar"'
        $script:Index | Should -Match 'id="stop-phrase"'
    }
}
