# AI automated systems: Bridge Warden

- **Challenge (official text):** [challenge.md](challenge.md)
- **Our project:** Bridge Warden, a governance and orchestration layer that sits between onboard AI agents and the ship.
- **Plan:** [docs/plan.md](docs/plan.md)

## Run it
Needs Python 3.12+ and [uv](https://docs.astral.sh/uv/). From this folder:
```powershell
.\run.ps1          # demo profile; opens Warden Control and the main app at http://localhost:8000
.\run.ps1 -Edge    # low-power edge profile
```
Then click **Start Operations** in Warden Control. The sim runs at 3x real time in *ops normal*; the presenter drives events with the **Inject** buttons (contact, overheat, storm, rogue). Every decision gives the operator 2 minutes, and a **periodic system check** asks the operator to confirm operations every 2 minutes (two misses → automatic SAFE-STOP). Use **SAFE-STOP** (hold 2 s) at any time. Presenter script: [docs/demo-script.md](docs/demo-script.md). For the scripted hands-off run, set `$env:SCENARIO='full_demo'` first.

## Test it
```powershell
cd backend; uv run pytest
```

## What's in it (first run)
| Path | What |
|---|---|
| `backend/warden/engine.py` | Governance core: 8-stage pipeline, conflicts, arbitration, escalations, monitors, operator controls |
| `backend/warden/agents.py` | 5 simulated agents (rule-based), each limited to its scoped view of the COP |
| `backend/warden/world.py` | Synthetic world: own ship, contacts, CPA/TCPA, engine, weather, scenario runner |
| `backend/warden/ledger.py` | Append-only, hash-chained audit ledger (SQLite) |
| `backend/warden/resources.py` | Edge budget meter (CPU, RAM, UI stream, intent size) |
| `backend/config/` | `agents.yaml` (registry) and `policies.yaml`: add an agent or rule without code |
| `backend/scenarios/full_demo.yaml` | The demo storyline |
| `frontend/` | `control.html` (Warden Control) and `index.html` (main app); no build step |
