# AI automated systems: Bridge Warden

- **Challenge (official text):** [challenge.md](challenge.md)
- **Our project:** Bridge Warden, a governance and orchestration layer that sits between onboard AI agents and the ship.
- **Plan:** [docs/plan.md](docs/plan.md)

## Run it
Nothing to install. Windows PowerShell 5.1 compiles the C# in `src\src` with `Add-Type` (C# 5, framework assemblies only) and serves the UI on loopback. From this folder:
```powershell
.\Launch_Bridge_Warden.bat                 # or .\src\run.ps1; opens Warden Control and the main app (port 8774, or 8775 if busy; 8776 belongs to Fairlead)
.\src\run.ps1 -Edge           # low-power edge profile
powershell -NoProfile -STA -ExecutionPolicy Bypass -File .\src\Start-BridgeWarden.ps1   # developer run: host directly, visible console
powershell -NoProfile -STA -ExecutionPolicy Bypass -File .\src\Start-BridgeWarden.ps1 -CheckOnly   # source rules + compile only
```
Then click **Start Operations** in Warden Control. The sim runs in *ops normal*; the presenter drives events from **Inject Issues…** (a column per picture, plus **Warden Tests**: compromised AI commander, rogue lookout, AI model outage). Every decision gives the operator 2 minutes, and a **periodic system check** asks the operator to confirm operations every 2 minutes (two misses → automatic SAFE-STOP). Use **SAFE-STOP** (hold 2 s) at any time. Presenter script: [docs/demo-script.md](docs/demo-script.md). For the scripted hands-off run, set `$env:SCENARIO='full_demo'` first.

AI calls default to local phoenix-lai at `http://127.0.0.1:1234/v1` (model `phoenixlai`); with no model there, the deciders show the model error, urgent alerts get the lookout's doctrine option by reflex (marked REFLEX, still checked by Warden), and explanations use rule text. Settings: `backend\.env` (from `backend\.env.example`) or the AI Model Server box in Warden Control. The model is loaded into memory at start, on **Apply** and when a model is picked in that box (status shown under it), so the deciders never wait on a model swap; once it loads, any agent showing NO MODEL is cleared. Port block 8774-8775 is provisional until edge-ops `PORTS.md` lists Bridge Warden (8776 is Fairlead's). The host answers `/__fleethacker/id` and `/__fleethacker/shutdown`.

**MORDECAI governance (multi-agent oversight):** agents outside the process join live over a signed protocol (manifest, heartbeat, intent, report, halt hook); rank = human base rank + ship-condition modifier + operator field promotion decides contested actuators (leases); a manifest mismatch is an immediate quarantine. Demos: Warden Control, Inject Issues, MORDECAI. Details and demo script: [docs/mordecai.md](docs/mordecai.md); stand-alone agent: `tools\Mordecai-Agent.ps1`.

**Through Fairlead (the Fleet's model router):** set the endpoint to `fairlead` (**Use Fairlead** in the AI Model Server box, or `LLM_BASE_URL=fairlead` in `backend\.env`). Bridge Warden then finds Fairlead wherever it runs: from the address Fairlead records in its CLI access file (`%LOCALAPPDATA%\Fairlead\fairlead-cli.json`; only its `url`, `gateway` and `pid` are read, never the token), or an older Fairlead's `logs\fairlead-server-*.json` in the folder `FAIRLEAD_HOME` names, else `http://127.0.0.1:8776/v1`, re-checked every 30 s, so a Fairlead restart on another port is picked up. A fixed URL works too. Bridge Warden recognizes Fairlead from its `/__fleethacker/id` and follows Fairlead's client rules: the key is the one typed in the AI Model Server box (saved as `LLM_API_KEY` in `backend\.env`), else the one Fairlead installed in Windows Credential Manager (`Fairlead/BridgeWarden`, Fairlead's **Clients** page, **Install for an app**), else none (**Clear key**), every request carries `X-Fairlead-Label: BridgeWarden`, and a blank model asks for Fairlead's `local` route. Point that route at phoenix-lai in Fairlead (Providers, then Routes) and Bridge Warden reaches phoenix-lai through Fairlead.

Ship it as one text file: `.\Package_App.ps1` writes `Bridge_Warden.package`; `.\Unpack.ps1` rebuilds the folders on the other machine.

## Test it
Pester 5 tests in `src\tests` (install once: `Install-Module Pester -MinimumVersion 5.0 -Scope CurrentUser`). There is no CI workflow in this repo; run them locally:
```powershell
Invoke-Pester src\tests
```

## What's in it
| Path | What |
|---|---|
| `host/Start-BridgeWarden.ps1` | Host: PS 5.1 check, source rules, compile, port block, browser windows |
| `host/Build.ps1` | Source rules (network and native code allowed in one named file each) and compiler settings |
| `host/src/Engine.cs` | Governance core: 8-stage pipeline, conflicts, arbitration, escalations, monitors, operator controls |
| `host/src/NavCell.cs` | Every COP: C# lookouts raise plain-English alerts with ready-made options (NAV: collision, weather, route, defense, strike, undersea; Systems: power re-routing, patch cables, engine, fuel; DC: casualties and teams); one model call per picture (TAO, EOOW, DCO) picks; reflex fallback if it is late; Warden checks every order and the deciders learn from its blocks |
| `host/src/LlmAgent.cs`, `AgentTools.cs` | Shared model-call plumbing (provenance registry, action parsing, lessons) and the free-form model path a compromised agent uses in the Warden Tests; the calculators the lookouts use |
| `host/src/World.cs`, `ShipSystems.cs` | Synthetic world: own ship, contacts, CPA/TCPA, weather, threats, power plant, damage control, scenario runner |
| `host/src/Ledger.cs` | Append-only, hash-chained audit ledger (Windows' built-in SQLite) |
| `host/src/Llm.cs`, `LlmClient.cs`, `LlmTasks.cs`, `Explain.cs` | The one AI wrapper: modes, cache, fallback text, streaming |
| `host/src/App.cs`, `WebServer.cs` | API routes, tick loop, WebSocket feed, loopback HTTP server |
| `host/src/Resources.cs` | Edge budget meter (CPU, RAM, UI stream, intent size) |
| `backend/config/` | `agents.json` (registry) and `policies.json`: add an agent or rule without code |
| `backend/scenarios/` | Scenario storylines (`full_demo.json`, `firefight.json`, `ops_normal.json`) |
| `backend/skills/` | The deciders' guides and glossaries (`nav_tao.md`, `eoow.md`, `dc_officer.md`) sent with every decision; `_common.md` and `energy.md` for the red-team path |
| `frontend/` | `control.html` (Warden Control) and `index.html` (main app); no build step |
