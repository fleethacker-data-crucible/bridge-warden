# Shared setup for the Pester 5 tests of the C# host. Windows PowerShell 5.1. ASCII only.
# Dot-source from BeforeAll:  . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
# Ports backend\tests\conftest.py and sim_harness.py: compiles host\src once per session, points Paths.Root at the
# app folder, keeps the LLM agent off and operator data (lessons, ledgers) in a temp folder, never backend\data.

$script:App = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:HostDir = Join-Path $script:App 'host'

# A type can only be added once per session, so compile only when it is missing.
if (-not ('BridgeWarden.World' -as [type])) {
    . (Join-Path $script:HostDir 'Build.ps1')
    $bwSources = @(Get-ChildItem (Join-Path $script:HostDir 'src') -Recurse -Filter *.cs | ForEach-Object { $_.FullName })
    Add-Type -Path $bwSources -CompilerParameters (New-BwCompilerParameters)
}
[BridgeWarden.Paths]::Root = $script:App

# Test doubles that run on worker threads (a PowerShell script block can't: no runspace there).
if (-not ('BridgeWardenTests.Fakes' -as [type])) {
    Add-Type -ReferencedAssemblies 'System.dll', 'System.Core.dll' -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace BridgeWardenTests
{
    public static class Fakes
    {
        public static Action<string, string, string> NoEmit() { return delegate(string a, string b, string c) { }; }

        // A stand-in for FairleadKey.Reader (Credential Manager), safe on worker threads. null = no key installed.
        public static Func<string> Key(string key) { return delegate { return key; }; }

        // lambda prompt, s: text
        public static Func<object, Dictionary<string, object>, string> Const(string text)
        {
            return delegate(object p, Dictionary<string, object> s) { return text; };
        }

        // lambda prompt, s: time.sleep(n) or text
        public static Func<object, Dictionary<string, object>, string> Sleepy(int ms, string text)
        {
            return delegate(object p, Dictionary<string, object> s) { Thread.Sleep(ms); return text; };
        }

        // lambda prompt, s: f"answer from {s['provider']}/{s['model']}"
        public static Func<object, Dictionary<string, object>, string> Echo()
        {
            return delegate(object p, Dictionary<string, object> s) { return "answer from " + s["provider"] + "/" + s["model"]; };
        }

        // local_chat(messages, s): records messages[0]["content"], answers replies in turn (repeatLast: the last one
        // forever; otherwise it throws when they run out, like next() on an exhausted iterator).
        public static Func<object, Dictionary<string, object>, string> Chat(string[] replies, bool repeatLast, List<string> prompts)
        {
            int[] n = { 0 };
            return delegate(object messages, Dictionary<string, object> s)
            {
                List<object> m = (List<object>)messages;
                lock (prompts) prompts.Add(Convert.ToString(((Dictionary<string, object>)m[0])["content"]));
                int i;
                lock (n) { i = n[0]; n[0]++; }
                if (i >= replies.Length)
                {
                    if (!repeatLast) throw new InvalidOperationException("StopIteration");
                    i = replies.Length - 1;
                }
                return replies[i];
            };
        }
    }


    // Collects on_chunk(kind, text) chunks and logger rows from any thread.
    public sealed class Collector
    {
        public readonly List<string[]> chunks = new List<string[]>();
        public readonly List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        public Action<string, string> Chunk() { return delegate(string k, string t) { lock (chunks) chunks.Add(new string[] { k, t }); }; }
        public Action<Dictionary<string, object>> Row() { return delegate(Dictionary<string, object> r) { lock (rows) rows.Add(r); }; }
    }

    // A local OpenAI-compatible server whose /chat/completions streams fixed chunks as SSE (the httpx MockTransport).
    public sealed class FakeSse : IDisposable
    {
        readonly HttpListener _l = new HttpListener();
        readonly string[] _chunks;
        readonly bool _fairlead;
        public readonly List<string> seen = new List<string>();
        public readonly List<string> auth = new List<string>();     // Authorization header of each model call
        public readonly List<string> labels = new List<string>();   // X-Fairlead-Label header of each model call

        // fairlead = answer /__fleethacker/id as Fairlead does; otherwise 404 there, like any non-FleetHacker server.
        public FakeSse(int port, string[] chunkJson) : this(port, chunkJson, false) { }

        public FakeSse(int port, string[] chunkJson, bool fairlead)
        {
            _chunks = chunkJson;
            _fairlead = fairlead;
            _l.Prefixes.Add("http://localhost:" + port + "/");
            _l.Start();
            Thread th = new Thread(Serve);
            th.IsBackground = true;
            th.Start();
        }

        void Serve()
        {
            while (_l.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _l.GetContext(); } catch (Exception) { return; }
                try
                {
                    string body = new StreamReader(ctx.Request.InputStream, Encoding.UTF8).ReadToEnd();
                    string outText;
                    if (ctx.Request.Url.AbsolutePath == "/__fleethacker/id")
                    {
                        if (!_fairlead) { ctx.Response.StatusCode = 404; ctx.Response.OutputStream.Close(); continue; }
                        ctx.Response.ContentType = "application/json";
                        outText = "{\"tool\":\"fairlead\",\"version\":\"test\",\"pid\":1}";
                    }
                    else if (ctx.Request.Url.AbsolutePath.EndsWith("/models"))
                    {
                        ctx.Response.ContentType = "application/json";
                        outText = "{\"data\": [{\"id\": \"fake-model\"}]}";
                    }
                    else
                    {
                        lock (seen)
                        {
                            seen.Add(body);
                            auth.Add(ctx.Request.Headers["Authorization"] ?? "");
                            labels.Add(ctx.Request.Headers["X-Fairlead-Label"] ?? "");
                        }
                        StringBuilder sb = new StringBuilder();
                        foreach (string c in _chunks) sb.Append("data: {\"choices\": [{\"delta\": " + c + "}]}\n\n");
                        sb.Append("data: [DONE]\n\n");
                        ctx.Response.ContentType = "text/event-stream";
                        outText = sb.ToString();
                    }
                    byte[] b = new UTF8Encoding(false).GetBytes(outText);
                    ctx.Response.ContentLength64 = b.Length;
                    ctx.Response.OutputStream.Write(b, 0, b.Length);
                    ctx.Response.OutputStream.Close();
                }
                catch (Exception) { }
            }
        }

        public void Dispose()
        {
            try { _l.Stop(); _l.Close(); } catch (Exception) { }
        }
    }
}
'@
}

# conftest.py: the LLM agent is off unless a test opts in; operator feedback stays out of backend\data.
if (-not $env:LLM_AGENT) { [Environment]::SetEnvironmentVariable('LLM_AGENT', 'off') }
if (-not $env:WARDEN_DATA) {
    $wd = Join-Path ([IO.Path]::GetTempPath()) ('warden-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $wd | Out-Null
    [Environment]::SetEnvironmentVariable('WARDEN_DATA', $wd)
}

# sim_harness.FakeClock starts here; Py.FakeTime > 0 freezes time.time() at that value.
$script:FakeClockStart = 1800000000.0

function New-BwTempDir {
    $d = Join-Path ([IO.Path]::GetTempPath()) ('bw-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
    New-Item -ItemType Directory -Force $d | Out-Null
    return $d
}

# Function output comes back wrapped in a PSObject; a Dictionary<string, object> must hold the bare value.
function Get-BwBase($Value) {
    if ($null -eq $Value) { return $null }
    return , $Value.PSObject.BaseObject
}

# Python literal -> the shapes the C# port expects: dict -> Dictionary<string, object> (key order kept),
# list -> List<object>, numbers -> double (as Json.Parse gives them), strings and bools unchanged.
function ConvertTo-BwObject($Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [System.Collections.IDictionary]) {
        $d = New-Object 'System.Collections.Generic.Dictionary[string,object]'
        foreach ($k in $Value.Keys) { $d[[string]$k] = Get-BwBase (ConvertTo-BwObject $Value[$k]) }
        return , $d
    }
    if ($Value -is [string]) { return $Value }
    if ($Value -is [bool]) { return $Value }
    if ($Value -is [System.Collections.IEnumerable]) {
        $l = New-Object 'System.Collections.Generic.List[object]'
        foreach ($x in $Value) { $l.Add((Get-BwBase (ConvertTo-BwObject $x))) }
        return , $l
    }
    if ($Value -is [int] -or $Value -is [long] -or $Value -is [double] -or $Value -is [decimal] -or $Value -is [single]) {
        return [double]$Value
    }
    return $Value
}

# Intent(agent_id=..., action=..., params=..., target_resource=..., **extra)
function New-BwIntent([string]$Agent, [string]$Action, $Params, [string]$Target, [hashtable]$Extra = @{}) {
    $it = New-Object BridgeWarden.Intent($Agent, $Action, (ConvertTo-BwObject $Params), $Target)
    foreach ($k in $Extra.Keys) {
        $v = $Extra[$k]
        if ($v -is [System.Collections.IDictionary]) { $v = ConvertTo-BwObject $v }
        $it.$k = $v
    }
    return $it
}

# warden.process([a, b, ...])
function Invoke-BwProcess($Warden, [object[]]$Intents) {
    $batch = New-Object 'System.Collections.Generic.List[BridgeWarden.Intent]'
    foreach ($i in $Intents) { $batch.Add($i) }
    $Warden.Process($batch)
}

function Invoke-BwEvent($World, [System.Collections.IDictionary]$Event) {
    [void]$World.ApplyEvent((ConvertTo-BwObject $Event))
}

function New-BwSet([string[]]$Items) {
    $s = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($i in $Items) { [void]$s.Add($i) }
    return , $s
}

# Private members the Python tests reach into (rt._busy, warden._proposal, ledger._db).
function Get-BwPrivate($Object, [string]$Name) {
    $f = $Object.GetType().GetField($Name, [Reflection.BindingFlags]'NonPublic, Instance')
    return $f.GetValue($Object)
}
function Invoke-BwPrivate($Object, [string]$Name, [object[]]$Arguments) {
    $m = $Object.GetType().GetMethod($Name, [Reflection.BindingFlags]'NonPublic, Instance')
    return $m.Invoke($Object, $Arguments)
}

# The pytest `setup` fixture: World, a ledger in a temp folder, Warden with provenance checks off.
function New-BwSetup([double]$TimeScale = 10.0) {
    $world = New-Object BridgeWarden.World($TimeScale)
    $ledger = New-Object BridgeWarden.Ledger((Join-Path (New-BwTempDir) 'l.db'))
    $warden = New-Object BridgeWarden.Warden([BridgeWarden.Paths]::Config, $ledger, $world, [BridgeWardenTests.Fakes]::NoEmit())
    # These tests check other governance rules with hand-built intents; provenance has its own tests.
    $warden.policies['require_llm_provenance'] = $false
    return [pscustomobject]@{ Warden = $warden; World = $world; Ledger = $ledger }
}

# sim_harness.run(seconds, tmp): the full demo scenario headless on the fake clock (one second per tick).
function Invoke-BwSim([int]$Seconds) {
    $world = New-Object BridgeWarden.World(10.0)
    $ledger = New-Object BridgeWarden.Ledger((Join-Path (New-BwTempDir) 'ledger.db'))
    $warden = New-Object BridgeWarden.Warden([BridgeWarden.Paths]::Config, $ledger, $world, [BridgeWardenTests.Fakes]::NoEmit())
    $sc = [BridgeWarden.Scenario]::Named('full_demo')
    $sc.running = $true
    for ($n = 0; $n -lt $Seconds; $n++) {
        if ([BridgeWarden.Py]::FakeTime -gt 0) { [BridgeWarden.Py]::FakeTime += 1 }
        foreach ($ev in $sc.Due(1.0)) {
            if ($ev.ContainsKey('rogue_agent')) { $warden.agents[[string]$ev['rogue_agent']['id']].rogue = $true }
            else { [void]$world.ApplyEvent($ev) }
        }
        $world.Step()
        $warden.Step()
    }
    return [pscustomobject]@{ Warden = $warden; World = $world; Ledger = $ledger }
}

function Get-HostileId($World) {
    foreach ($c in $World.contacts.Values) { if ($c.kind -eq 'hostile') { return $c.id } }
    throw 'no hostile contact'
}
