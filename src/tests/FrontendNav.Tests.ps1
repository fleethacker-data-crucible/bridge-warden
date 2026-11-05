# Pester 5 static checks for BRIDGE-WARDEN-01: primary navigation must be keyboard operable.
# No browser and no C# compile; reads the frontend sources only. Windows PowerShell 5.1. ASCII only.
# Invoke-Pester src\tests\FrontendNav.Tests.ps1

BeforeAll {
    $script:Frontend = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'frontend'
    $script:Html = [IO.File]::ReadAllText((Join-Path $script:Frontend 'index.html'))
    $script:App = [IO.File]::ReadAllText((Join-Path $script:Frontend 'js\app.js'))
    $script:Css = [IO.File]::ReadAllText((Join-Path $script:Frontend 'css\styles.css'))
    $nav = [regex]::Match($script:Html, '(?s)<nav\b[^>]*>.*?</nav>')
    $script:NavHtml = $nav.Value
    $script:NavItems = @([regex]::Matches($script:NavHtml, '(?s)<(\w+)\b[^>]*\bclass="nav-item[^"]*"[^>]*>'))
}

Describe 'Primary navigation (BRIDGE-WARDEN-01)' {
    It 'is a labelled nav landmark with ten destinations' {
        $script:NavHtml | Should -Match '^<nav\b[^>]*aria-label="[^"]+"'
        $script:NavItems.Count | Should -Be 10
    }

    It 'uses native buttons, never div or span, for every destination' {
        foreach ($m in $script:NavItems) {
            $m.Groups[1].Value | Should -Be 'button'
            $m.Value | Should -Match 'type="button"'
            $m.Value | Should -Match 'data-page="\w+"'
        }
    }

    It 'marks exactly the active destination with aria-current' {
        $current = @($script:NavItems | Where-Object { $_.Value -match 'aria-current="page"' })
        $current.Count | Should -Be 1
        $current[0].Value | Should -Match 'class="nav-item active"'
    }

    It 'hides decorative icons from assistive technology' {
        $script:NavHtml | Should -Not -Match '<span class="icon">'
    }

    It 'keeps aria-current in step with the active class and announces the page change' {
        $script:App | Should -Match "setAttribute\('aria-current', 'page'\)"
        $script:App | Should -Match "removeAttribute\('aria-current'\)"
        $script:Html | Should -Match 'id="page-status"[^>]*aria-live="polite"'
        $script:App | Should -Match "\`$\('page-status'\)"
    }

    It 'shows a visible focus ring and keeps the old look (no native button chrome)' {
        $script:Css | Should -Match '\.nav-item:focus-visible\s*\{[^}]*outline'
        $rule = [regex]::Match($script:Css, '\.nav-item\s*\{[^}]*\}').Value
        $rule | Should -Match 'border:\s*0'
        $rule | Should -Match 'background:\s*none'
        $rule | Should -Match 'font:\s*inherit'
    }

    It 'still reaches every destination through the existing page switch' {
        foreach ($m in $script:NavItems) {
            $page = [regex]::Match($m.Value, 'data-page="(\w+)"').Groups[1].Value
            $script:App | Should -Match "\b$page\s*:\s*page\w+"
        }
    }
}
