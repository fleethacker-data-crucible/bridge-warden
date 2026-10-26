# CDAO Maximus APIs - launcher + native PowerShell server engine
# Reads ../config.json, starts the chosen engine, and opens a centered Chrome app window.
#   "engine": "deno"   -> runs src/server.js with deno.exe
#   "engine": "native" -> serves from this script (PowerShell HttpListener)
# Everything shuts down automatically when the Chrome window is closed.

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path $PSScriptRoot -Parent
$Root = Join-Path $PSScriptRoot 'html'
$SecretsPath = Join-Path $PSScriptRoot 'secrets.json'

# --- Settings from config.json (with defaults) ---
$Settings = [pscustomobject]@{ engine = 'deno'; port = 8080; window = [pscustomobject]@{ width = 1500; height = 950 }; denoPath = '' }
$SettingsPath = Join-Path $ProjectRoot 'config.json'
if (Test-Path $SettingsPath) {
    $loaded = Get-Content $SettingsPath -Raw | ConvertFrom-Json
    foreach ($p in $loaded.PSObject.Properties) { $Settings | Add-Member -NotePropertyName $p.Name -NotePropertyValue $p.Value -Force }
}
$Engine = ([string]$Settings.engine).ToLower()
$Port = [int]$Settings.port
$Width = [int]$Settings.window.width
$Height = [int]$Settings.window.height

# --- Find a free port starting at $Port ---
function Test-PortFree([int]$p) {
    try {
        $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $p)
        $l.Start(); $l.Stop(); return $true
    } catch { return $false }
}
while (-not (Test-PortFree $Port)) { $Port++ }

# --- Deno engine ---
function Find-Deno {
    $candidates = @(
        [string]$Settings.denoPath,
        (Join-Path $ProjectRoot 'bin\deno.exe'),
        (Join-Path $env:USERPROFILE '.deno\bin\deno.exe')
    )
    $found = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($found) { return $found }
    $cmd = Get-Command deno.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { return $cmd.Source }
    return $null
}

$DenoProc = $null
if ($Engine -eq 'deno') {
    $Deno = Find-Deno
    if (-not $Deno) {
        Write-Warning 'deno.exe not found - falling back to the native engine. Set "denoPath" in config.json.'
        $Engine = 'native'
    }
}

if ($Engine -eq 'deno') {
    $Prefix = "http://127.0.0.1:$Port/"
    $denoArgs = @(
        'run', '--quiet', '--no-prompt',
        '--allow-net',
        "--allow-read=`"$PSScriptRoot`"",
        "--allow-write=`"$SecretsPath`"",
        '--allow-run=powershell.exe',
        "`"$(Join-Path $PSScriptRoot 'server.js')`"",
        $Port, "`"$Root`"", "`"$SecretsPath`""
    )
    $DenoProc = Start-Process -FilePath $Deno -ArgumentList $denoArgs -NoNewWindow -PassThru
    # Wait until it answers before opening Chrome
    $ready = $false
    for ($i = 0; $i -lt 50 -and -not $DenoProc.HasExited; $i++) {
        try { Invoke-RestMethod "$($Prefix)api/health" -TimeoutSec 1 | Out-Null; $ready = $true; break } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $ready) { throw "Deno engine failed to start ($Deno)" }
} else {
    $Prefix = "http://localhost:$Port/"
    $Listener = [System.Net.HttpListener]::new()
    $Listener.Prefixes.Add($Prefix)
    $Listener.Start()
    Write-Host "CDAO Maximus server (native) running at $Prefix (serving $Root)"
}

# ================= Native engine =================

$MimeTypes = @{
    '.html' = 'text/html; charset=utf-8'
    '.css'  = 'text/css; charset=utf-8'
    '.js'   = 'application/javascript; charset=utf-8'
    '.json' = 'application/json; charset=utf-8'
    '.svg'  = 'image/svg+xml'
    '.png'  = 'image/png'
    '.jpg'  = 'image/jpeg'
    '.jpeg' = 'image/jpeg'
    '.gif'  = 'image/gif'
    '.ico'  = 'image/x-icon'
    '.woff' = 'font/woff'
    '.woff2'= 'font/woff2'
    '.txt'  = 'text/plain; charset=utf-8'
}

function Send-Response($ctx, [int]$status, [byte[]]$body, [string]$type) {
    $res = $ctx.Response
    $res.StatusCode = $status
    $res.ContentType = $type
    $res.Headers.Add('Cache-Control', 'no-store')
    $res.ContentLength64 = $body.Length
    $res.OutputStream.Write($body, 0, $body.Length)
    $res.OutputStream.Close()
}

function Send-Json($ctx, $obj, [int]$status = 200) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($obj | ConvertTo-Json -Depth 10))
    Send-Response $ctx $status $bytes 'application/json; charset=utf-8'
}

# --- Secrets: API key is DPAPI-encrypted in secrets.json (gitignored) and kept in memory ---
. (Join-Path $PSScriptRoot 'secrets.ps1')
$script:ApiKey = $null

function Get-ApiKey {
    if ($null -ne $script:ApiKey) { return $script:ApiKey }
    $script:ApiKey = ''
    if (Test-Path $SecretsPath) {
        $s = Get-Content $SecretsPath -Raw | ConvertFrom-Json
        if ($s.apiKeyProtected) {
            try { $script:ApiKey = Unprotect-Secret $s.apiKeyProtected }
            catch { Write-Warning 'Could not decrypt the saved API key (different Windows user?). Paste it again in Settings.' }
        } elseif ($s.apiKey) {
            Set-ApiKey ([string]$s.apiKey)   # migrate an old plain-text key
        }
    }
    return $script:ApiKey
}

function Set-ApiKey([string]$key) {
    $script:ApiKey = $key.Trim()
    $data = if ($script:ApiKey) { @{ apiKeyProtected = (Protect-Secret $script:ApiKey) } } else { @{} }
    $data | ConvertTo-Json | Set-Content -Path $SecretsPath -Encoding UTF8
}

# Replace the key (raw and URL-encoded) with *** so it never reaches the browser or log
function Hide-ApiKey([string]$text) {
    $key = Get-ApiKey
    if (-not $text -or $key.Length -lt 4) { return $text }
    return $text.Replace($key, '***').Replace([Uri]::EscapeDataString($key), '***')
}

function Read-JsonBody($ctx) {
    $reader = [System.IO.StreamReader]::new($ctx.Request.InputStream, [System.Text.Encoding]::UTF8)
    $text = $reader.ReadToEnd(); $reader.Close()
    if ([string]::IsNullOrWhiteSpace($text)) { return [pscustomobject]@{} }
    return $text | ConvertFrom-Json
}

function Get-KeyStatus {
    $key = [string](Get-ApiKey)
    $preview = if ($key.Length -gt 8) { $key.Substring(0, 4) + '...' + $key.Substring($key.Length - 4) } elseif ($key) { '****' } else { '' }
    return @{ hasKey = [bool]$key; keyPreview = $preview }
}

# --- Proxy: browser -> server -> external API (avoids CORS, injects {{API_KEY}}) ---
if ($Engine -eq 'native') {
    Add-Type -AssemblyName System.Net.Http
    $Http = [System.Net.Http.HttpClient]::new()
    $Http.Timeout = [TimeSpan]::FromSeconds(60)
}

function Invoke-Proxy($ctx) {
    $req = Read-JsonBody $ctx
    $key = [string](Get-ApiKey)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $url = ([string]$req.url).Replace('{{API_KEY}}', [Uri]::EscapeDataString($key))
        $method = if ($req.method) { ([string]$req.method).ToUpper() } else { 'GET' }
        $msg = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($method), $url)
        $contentType = 'application/json'
        if ($req.headers) {
            foreach ($h in $req.headers.PSObject.Properties) {
                $val = ([string]$h.Value).Replace('{{API_KEY}}', $key)
                if ($h.Name -ieq 'Content-Type') { $contentType = $val; continue }
                [void]$msg.Headers.TryAddWithoutValidation($h.Name, $val)
            }
        }
        if ($null -ne $req.body -and $method -ne 'GET') {
            $bodyText = if ($req.body -is [string]) { $req.body } else { $req.body | ConvertTo-Json -Depth 20 }
            $msg.Content = [System.Net.Http.StringContent]::new($bodyText, [System.Text.Encoding]::UTF8)
            $msg.Content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse($contentType)
        }
        $resp = $Http.SendAsync($msg).GetAwaiter().GetResult()
        $text = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $sw.Stop()
        Send-Json $ctx @{
            ok          = $resp.IsSuccessStatusCode
            status      = [int]$resp.StatusCode
            statusText  = $resp.ReasonPhrase
            ms          = $sw.ElapsedMilliseconds
            contentType = [string]$resp.Content.Headers.ContentType
            body        = (Hide-ApiKey $text)
        }
    } catch {
        $sw.Stop()
        $err = $_.Exception
        while ($err.InnerException) { $err = $err.InnerException }
        Send-Json $ctx @{ ok = $false; status = 0; statusText = 'Network error'; ms = $sw.ElapsedMilliseconds; error = (Hide-ApiKey $err.Message); body = '' }
    }
}

# --- API routes: add hackathon endpoints here (mirror them in server.js) ---
function Invoke-Api($ctx) {
    $path = $ctx.Request.Url.AbsolutePath.ToLower()
    $method = $ctx.Request.HttpMethod
    switch ($path) {
        '/api/health' { Send-Json $ctx @{ status = 'ok'; engine = 'native'; time = (Get-Date).ToString('o') }; return $true }
        '/api/shutdown' { Send-Json $ctx @{ status = 'stopping' }; $script:Running = $false; return $true }
        '/api/config' {
            if ($method -eq 'POST') {
                $body = Read-JsonBody $ctx
                Set-ApiKey ([string]$body.apiKey)
            }
            Send-Json $ctx (Get-KeyStatus); return $true
        }
        '/api/proxy' { Invoke-Proxy $ctx; return $true }
    }
    return $false
}

function Invoke-Static($ctx) {
    $rel = [Uri]::UnescapeDataString($ctx.Request.Url.AbsolutePath).TrimStart('/')
    if ([string]::IsNullOrEmpty($rel)) { $rel = 'index.html' }
    $full = [System.IO.Path]::GetFullPath((Join-Path $Root $rel))
    if (-not $full.StartsWith([System.IO.Path]::GetFullPath($Root), [StringComparison]::OrdinalIgnoreCase)) {
        Send-Response $ctx 403 ([System.Text.Encoding]::UTF8.GetBytes('Forbidden')) 'text/plain'; return
    }
    if (Test-Path $full -PathType Container) { $full = Join-Path $full 'index.html' }
    if (Test-Path $full -PathType Leaf) {
        $ext = [System.IO.Path]::GetExtension($full).ToLower()
        $type = if ($MimeTypes.ContainsKey($ext)) { $MimeTypes[$ext] } else { 'application/octet-stream' }
        Send-Response $ctx 200 ([System.IO.File]::ReadAllBytes($full)) $type
    } else {
        Send-Response $ctx 404 ([System.Text.Encoding]::UTF8.GetBytes("Not found: $rel")) 'text/plain'
    }
}

# ================= Chrome app window =================

Add-Type -AssemblyName System.Windows.Forms
$Area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$X = [Math]::Max(0, $Area.X + [int](($Area.Width - $Width) / 2))
$Y = [Math]::Max(0, $Area.Y + [int](($Area.Height - $Height) / 2))

$ChromeCandidates = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe"
)
$Chrome = $ChromeCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

# Dedicated profile so size/position flags are honored and we can detect window close
$ProfileDir = Join-Path $env:LOCALAPPDATA 'CDAO_Maximus\ChromeProfile'
$ChromeProc = $null
if ($Chrome) {
    $chromeArgs = @(
        "--app=$Prefix",
        "--window-size=$Width,$Height",
        "--window-position=$X,$Y",
        "--user-data-dir=`"$ProfileDir`"",
        '--no-first-run',
        '--no-default-browser-check'
    )
    $ChromeProc = Start-Process -FilePath $Chrome -ArgumentList $chromeArgs -PassThru
} else {
    Write-Warning 'Chrome not found - opening default browser instead.'
    Start-Process $Prefix
}

function Test-ChromeOpen {
    $procs = Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" -ErrorAction SilentlyContinue
    return [bool]($procs | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($ProfileDir) } | Select-Object -First 1)
}

# ================= Main loop =================

$script:Running = $true
$LastChromeCheck = (Get-Date).AddSeconds(5)   # grace period while Chrome starts
try {
    if ($Engine -eq 'native') { $pending = $Listener.GetContextAsync() }
    while ($script:Running) {
        if ($Engine -eq 'native') {
            if ($pending.Wait(500)) {
                $ctx = $pending.Result
                $pending = $Listener.GetContextAsync()
                try {
                    Write-Host ("{0} {1}" -f $ctx.Request.HttpMethod, $ctx.Request.Url.AbsolutePath)
                    if (-not (Invoke-Api $ctx)) { Invoke-Static $ctx }
                } catch {
                    Write-Warning $_
                    try { Send-Response $ctx 500 ([System.Text.Encoding]::UTF8.GetBytes("$_")) 'text/plain' } catch {}
                }
            }
        } else {
            Start-Sleep -Milliseconds 500
            if ($DenoProc.HasExited) { Write-Host 'Deno engine exited.'; $script:Running = $false }
        }
        # chrome.exe may hand off to an existing instance and exit at once, so look for
        # any process using our profile instead of tracking the launcher process.
        if ($ChromeProc -and ((Get-Date) - $LastChromeCheck).TotalSeconds -ge 3) {
            $LastChromeCheck = Get-Date
            if (-not (Test-ChromeOpen)) { Write-Host 'Chrome window closed.'; $script:Running = $false }
        }
    }
} finally {
    if ($Listener) { $Listener.Stop(); $Listener.Close() }
    if ($DenoProc -and -not $DenoProc.HasExited) { Stop-Process -Id $DenoProc.Id -Force }
    Write-Host 'Server stopped.'
}
