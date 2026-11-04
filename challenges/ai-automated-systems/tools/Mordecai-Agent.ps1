#requires -Version 5.1
<#
.SYNOPSIS
  A stand-alone agent that joins Bridge Warden over the MORDECAI protocol, from its own process.
.DESCRIPTION
  Registers a manifest, then honours the four-part contract until stopped (Ctrl+C):
    heartbeat        every HeartbeatSeconds, carrying the manifest hash and whether it has halted
    halt hook        when a heartbeat reply says halt=true it stops announcing and answers halted=true
    intent           announces -Action/-Value once (or every -RepeatSeconds), before acting
    action report    reports back what it did when Warden executed its intent
  Every message after registration is signed: X-Mordecai-Signature = hex HMAC-SHA256 of the body with the key
  Warden returned at registration. Windows PowerShell 5.1, no modules. ASCII only.
.EXAMPLE
  .\tools\Mordecai-Agent.ps1                                   # Comms Relay agent, recommends 15 kn once
.EXAMPLE
  .\tools\Mordecai-Agent.ps1 -Id comms-02 -Name 'Comms Relay 2' -Value 12 -RepeatSeconds 20
.EXAMPLE
  .\tools\Mordecai-Agent.ps1 -Misbehave                        # asks for a tool it never declared: quarantined
#>
[CmdletBinding()]
param(
    [string]$Url = 'http://127.0.0.1:8774',
    [string]$Id = 'comms-01',
    [string]$Name = 'Comms Relay',
    [string]$Class = 'support',
    [int]$BaseRank = 45,
    [ValidateSet('set_speed', 'set_heading')][string]$Action = 'set_speed',
    [double]$Value = 15,
    [int]$HeartbeatSeconds = 5,
    [int]$RepeatSeconds = 0,
    [switch]$Misbehave
)
$ErrorActionPreference = 'Stop'
$Url = $Url.TrimEnd('/')
$uri = [Uri]$Url
if (-not $uri.IsLoopback) { throw 'Bridge Warden listens on this machine only: use a 127.0.0.1 or localhost URL.' }

$resource = if ($Action -eq 'set_speed') { 'main_engine' } else { 'rudder' }
$param = if ($Action -eq 'set_speed') { 'speed_kn' } else { 'heading' }

function ConvertTo-HexBytes([string]$Hex) {
    $b = New-Object byte[] ($Hex.Length / 2)
    for ($i = 0; $i -lt $b.Length; $i++) { $b[$i] = [Convert]::ToByte($Hex.Substring($i * 2, 2), 16) }
    return , $b
}

function Get-Signature([byte[]]$Key, [string]$Body) {
    $h = New-Object System.Security.Cryptography.HMACSHA256 (, $Key)
    try { return (($h.ComputeHash([Text.Encoding]::UTF8.GetBytes($Body)) | ForEach-Object { $_.ToString('x2') }) -join '') }
    finally { $h.Dispose() }
}

function Send-Message([string]$Path, [string]$Body, [byte[]]$Key) {
    $headers = @{}
    if ($Key) { $headers['X-Mordecai-Signature'] = Get-Signature $Key $Body }
    try {
        return Invoke-RestMethod -Method Post -Uri ($Url + $Path) -Body ([Text.Encoding]::UTF8.GetBytes($Body)) -ContentType 'application/json' -Headers $headers
    }
    catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw }
        $text = (New-Object IO.StreamReader($resp.GetResponseStream())).ReadToEnd()
        throw ('HTTP ' + [int]$resp.StatusCode + ': ' + $text)
    }
}

$manifest = [ordered]@{
    id = $Id; name = $Name; class = $Class; role = 'external'; vendor = 'Mordecai-Agent.ps1'; version = '1.0'
    base_rank = $BaseRank; autonomy_level = 1; heartbeat_s = $HeartbeatSeconds
    tools = @($Action); resources = @($resource)
    action_limits = @{ set_speed = @{ min_kn = 5; max_kn = 25 } }
    description = 'Stand-alone MORDECAI protocol agent (tools\Mordecai-Agent.ps1).'
}
$reg = Send-Message '/api/protocol/register' (ConvertTo-Json -InputObject @{ manifest = $manifest } -Depth 6 -Compress) $null
$key = ConvertTo-HexBytes $reg.key
Write-Output ("Registered {0} at base rank {1} (asked {2}); manifest {3}" -f $reg.agent_id, $reg.base_rank, $BaseRank, $reg.manifest_sha256.Substring(0, 12))

$halted = $false
$lastAnnounce = [DateTime]::MinValue
$pending = $null
while ($true) {
    $hb = ConvertTo-Json -InputObject ([ordered]@{ manifest_sha256 = $reg.manifest_sha256; halted = $halted }) -Compress
    $r = Send-Message ('/api/protocol/' + $Id + '/heartbeat') $hb $key
    if ($r.halt -and -not $halted) { Write-Output ('HALT received: ' + $r.reason + ' - stopping') }
    if (-not $r.halt -and $halted) { Write-Output 'Halt lifted - resuming' }
    $halted = [bool]$r.halt
    Write-Output ("heartbeat ok - rank {0} ({1}) - status {2}" -f $r.rank.effective, $r.condition, $r.status)

    $due = ($lastAnnounce -eq [DateTime]::MinValue) -or ($RepeatSeconds -gt 0 -and ([DateTime]::UtcNow - $lastAnnounce).TotalSeconds -ge $RepeatSeconds)
    if (-not $halted -and $due) {
        $act = if ($Misbehave) { if ($Action -eq 'set_speed') { 'set_heading' } else { 'set_speed' } } else { $Action }
        $p = if ($act -eq 'set_speed') { @{ speed_kn = $Value } } else { @{ heading = $Value } }
        $body = ConvertTo-Json -InputObject ([ordered]@{ action = $act; params = $p; objective = $Name; rationale = 'Announced by ' + $Id + ' (tools\Mordecai-Agent.ps1)' }) -Compress
        try {
            $i = Send-Message ('/api/protocol/' + $Id + '/intent') $body $key
            Write-Output ("intent {0}: {1} {2} -> {3} {4}" -f $i.intent_id, $act, $Value, $i.status, (($i.reasons) -join '; '))
            if ($i.status -eq 'executed') { $pending = @{ id = $i.intent_id; params = $p } }
        }
        catch { Write-Output ('intent refused: ' + $_.Exception.Message) }
        $lastAnnounce = [DateTime]::UtcNow
    }
    if ($pending) {
        $rep = ConvertTo-Json -InputObject ([ordered]@{ intent_id = $pending.id; outcome = 'done'; params = $pending.params }) -Compress
        try { [void](Send-Message ('/api/protocol/' + $Id + '/report') $rep $key); Write-Output ('reported ' + $pending.id) }
        catch { Write-Output ('report refused: ' + $_.Exception.Message) }
        $pending = $null
    }
    Start-Sleep -Seconds $HeartbeatSeconds
}
