# Bridge Warden — Full Hackathon Plan
### Multi-Agent Governance & Orchestration Layer for Maritime AI Systems

> **One-liner:** Bridge Warden sits between every onboard AI agent and the ship. No agent touches an actuator directly. Every recommendation or action becomes a typed **Intent** that Warden authenticates, checks against authority and safety limits, compares with other agents' intents, resolves or escalates to a human, executes, and logs so it can be reversed.

---

## Table of Contents
1. [Concept & Architecture](#1-concept--architecture)
2. [Dev Machine Setup (What to Install)](#2-dev-machine-setup-what-to-install)
3. [Tech Stack](#3-tech-stack)
4. [Repo Structure](#4-repo-structure)
5. [Data Models](#5-data-models)
6. [Synthetic Data & Scenarios](#6-synthetic-data--scenarios)
7. [Simulated Agents](#7-simulated-agents)
8. [Governance Pipeline](#8-governance-pipeline)
9. [Runtime Monitors](#9-runtime-monitors)
10. [Operator Controls](#10-operator-controls)
11. [Audit, Explainability & LLM Use](#11-audit-explainability--llm-use)
12. [API Spec](#12-api-spec)
13. [UI Design System (matched to reference screenshots)](#13-ui-design-system-matched-to-reference-screenshots)
14. [UI Layout: Window A — Warden Control](#14-ui-layout-window-a--warden-control)
15. [UI Layout: Window B — Bridge Warden Main App](#15-ui-layout-window-b--bridge-warden-main-app)
16. [UI Screens in Detail](#16-ui-screens-in-detail)
17. [Frontend Architecture](#17-frontend-architecture)
18. [Build Timeline & Team Split](#18-build-timeline--team-split)
19. [Test Plan](#19-test-plan)
20. [Demo Plan](#20-demo-plan)
21. [Risks & Mitigations](#21-risks--mitigations)
22. [Stretch Goals](#22-stretch-goals)
23. [Pre-Hackathon Checklist](#23-pre-hackathon-checklist)

---

## 1. Concept & Architecture

```
 Sensors / Simulator ──► Common Operating Picture (COP) ◄── scoped read ── Agents
                                   │                                        │
                                   ▼                                        ▼  Intents
                 ┌──────────────────────── BRIDGE WARDEN ────────────────────────┐
                 │ Registry │ Policy Engine │ Conflict Detector │ Arbiter        │
                 │ Escalation Queue │ Runtime Monitors │ Audit Ledger           │
                 │ Controls: Pause · Constrain · Override · Rollback · Safe-Stop │
                 └──────────────────────────────┬────────────────────────────────┘
                                                ▼
                         Actuator Simulator   +   Operator UI (2 windows)
```

**How it answers the challenge questions:**

| Challenge question | Where it's answered |
|---|---|
| Which agents are active? | Agent registry + heartbeat; Agents page; worker tile grid in Warden Control |
| What can each agent access? | `read_scopes` + `tools` per agent; Authority Matrix page; agent detail drawer |
| What is each agent authorized to do? | `autonomy_level` + `action_limits`; policy engine |
| Where do recommendations conflict? | Conflict detector; Conflicts page |
| How are unsafe interactions detected? | Constraint checks + runtime monitors + anomaly detection |
| How can operators pause/override/constrain/roll back? | Control API + buttons on every screen + global SAFE-STOP |

---

## 2. Dev Machine Setup (What to Install)

Assumes **Windows 10/11** (matches your screenshots). Mac/Linux equivalents are noted where they differ.

### 2.1 Required software

| # | Tool | Version | Purpose | Install (Windows) |
|---|---|---|---|---|
| 1 | **Git** | latest | Source control | `winget install Git.Git` |
| 2 | **Python** | 3.12.x (3.11 OK) | Backend, sim, agents, tests | `winget install Python.Python.3.12` (tick "Add to PATH") |
| 3 | **uv** | latest | Fast Python env + package manager | `winget install astral-sh.uv` (or `pip install uv`) |
| 4 | **Node.js LTS** | 22.x (20.x OK) | Frontend build | `winget install OpenJS.NodeJS.LTS` |
| 5 | **pnpm** | 9.x | Frontend package manager (npm works too) | `npm install -g pnpm` |
| 6 | **VS Code** | latest | Editor | `winget install Microsoft.VisualStudioCode` |
| 7 | **Windows Terminal** | latest | Multi-pane terminals (backend + frontend + logs) | `winget install Microsoft.WindowsTerminal` |
| 8 | **Chrome or Edge** | latest | Running the UI, DevTools | Already installed / `winget install Google.Chrome` |
| 9 | **LLM API key** | — | LLM explanations and summaries | From the team credential vault (`tools/creds/pull-creds.ps1`); set `LLM_PROVIDER` / `LLM_MODEL` in `.env` |

### 2.2 Recommended software

| Tool | Purpose | Install |
|---|---|---|
| **DB Browser for SQLite** | Inspect the audit ledger | `winget install DBBrowserForSQLite.DBBrowserForSQLite` |
| **Bruno** or **Postman** | Hit the API manually during dev | `winget install Bruno.Bruno` |
| **OBS Studio** | Record the backup demo video | `winget install OBSProject.OBSStudio` |
| **ShareX** or Snipping Tool | Screenshots for slides | `winget install ShareX.ShareX` |
| **Docker Desktop** *(optional)* | Only if you add Redis or want one-command run | `winget install Docker.DockerDesktop` |
| **Rust + Tauri CLI** *(optional)* | Only if wrapping the UI as a real desktop app like your screenshots | `winget install Rustlang.Rustup` then `cargo install tauri-cli` |
| **Figma** (free, web) *(optional)* | Quick wireframes | Browser |

> Desktop look without Tauri: open the UI with `msedge --app=http://localhost:5173` to get a chromeless window like your screenshots. Zero extra installs.

### 2.3 VS Code extensions

- Python (ms-python.python) + Pylance
- Ruff (charliermarsh.ruff): lint + format
- ESLint, Prettier
- Tailwind CSS IntelliSense
- Playwright Test for VS Code
- YAML (redhat.vscode-yaml): policy/scenario files
- REST Client or Thunder Client (alternative to Bruno)
- GitLens (optional)
- AI coding assistant of choice (optional, AI pair-programming)

### 2.4 Python packages (`backend/pyproject.toml`)

```
fastapi            uvicorn[standard]    pydantic>=2      pydantic-settings
pyyaml             aiosqlite            litellm          shapely   # zone geometry
numpy              python-dotenv        orjson
# dev
pytest  pytest-asyncio  httpx  ruff  hypothesis  # property tests for policies
```

### 2.5 Frontend packages (`frontend/package.json`)

```
react  react-dom  react-router-dom  zustand  @tanstack/react-query
tailwindcss  @tailwindcss/vite  clsx  lucide-react   # icons similar to screenshots
leaflet  react-leaflet  recharts  date-fns
@fontsource/exo-2  @fontsource/jetbrains-mono       # fonts bundled = works offline
# dev
vite  typescript  @vitejs/plugin-react  vitest  @testing-library/react
@playwright/test  eslint  prettier
```

### 2.6 Verify the install

```powershell
git --version
python --version        # 3.12.x
uv --version
node --version          # v22.x
pnpm --version
code --version
```

### 2.7 First-run commands

```powershell
git clone <repo> bridge-warden; cd bridge-warden
# backend
cd backend; uv sync; copy .env.example .env   # set LLM_PROVIDER, LLM_MODEL + key (pull-creds.ps1)
uv run uvicorn main:app --reload --port 8000
# frontend (new terminal pane)
cd frontend; pnpm install; pnpm dev            # http://localhost:5173
# tests
cd backend; uv run pytest
cd frontend; pnpm exec playwright install chromium; pnpm test:e2e
```

### 2.8 Offline readiness (venue Wi-Fi is often bad)

- Fonts bundled via `@fontsource` (no Google Fonts call).
- Map tiles: download an offline tile set for the demo area, or use a plain dark SVG sea background + GeoJSON zones. Do not depend on a live tile server.
- `LLM_MODE=cached` env flag: serves pre-generated explanations from `fixtures/llm_cache.json`.
- `pnpm build` once and serve `dist/` from FastAPI as a fallback.
- Keep a phone hotspot available.

### 2.9 Hardware notes

- 16 GB RAM is comfortable; 8 GB works.
- A second monitor during the demo: Window A (Warden Control) on one, Window B (main app) on the other. If only one screen, use Windows Snap side by side.
- HDMI/USB-C adapter for the venue projector.

---

## 3. Tech Stack

| Layer | Choice | Why |
|---|---|---|
| Backend | Python 3.12 + FastAPI + asyncio | Fast to build, async tick loop |
| Event bus | In-process asyncio pub/sub | No infrastructure |
| Models | Pydantic v2 | Typed messages; JSON Schema for free |
| Storage | SQLite (append-only, hash-chained ledger) + in-memory state | Zero setup |
| Policies | YAML rules + small Python evaluator | Readable for judges |
| LLM | Any LLM provider via `explain.py` wrapper (structured JSON output) | Rationale, conflict summaries, incident reports |
| Frontend | React + TypeScript + Vite + Tailwind | Fast, matches the look easily |
| State | Zustand (live stream) + React Query (REST) | Simple |
| Realtime | WebSocket `/ws` | Push all events to the UI |
| Map | React-Leaflet with custom dark styling (or SVG) | COP display |
| Charts | Recharts | Sparklines, metrics |
| Tests | pytest, Vitest, Playwright | Unit → scenario → end-to-end |

---

## 4. Repo Structure

```
bridge-warden/
├── backend/
│   ├── main.py                    # app, WS, startup, tick loop
│   ├── settings.py                # env config (LLM_MODE, TICK_HZ, TIME_SCALE)
│   ├── models/
│   │   ├── agent.py               # AgentIdentity, AgentStatus
│   │   ├── intent.py              # Intent, Decision, IntentStatus
│   │   ├── state.py               # COP, OwnShip, Contact, Environment, Zone
│   │   └── events.py              # LedgerEvent, Alert, WsMessage
│   ├── core/
│   │   ├── bus.py                 # pub/sub
│   │   ├── registry.py            # identity, heartbeat, authority
│   │   ├── cop.py                 # shared state + scoped views
│   │   ├── predictor.py           # 1-step / N-step effect simulation
│   │   ├── policy.py              # YAML policy evaluation
│   │   ├── conflicts.py           # conflict detection
│   │   ├── arbiter.py             # priority ladder / merging
│   │   ├── escalation.py          # human approval queue + timeouts
│   │   ├── monitors.py            # watchdog + anomaly detection
│   │   ├── ledger.py              # hash-chained audit log
│   │   ├── control.py             # pause/constrain/override/rollback/safe-stop
│   │   └── explain.py             # LLM wrapper + cache
│   ├── agents/  base.py navigation.py collision.py energy.py
│   │            engine_health.py weather_routing.py rogue.py
│   ├── sim/     world.py generator.py scenario_runner.py
│   │   └── scenarios/  calm.yaml crossing.yaml storm_fault.yaml rogue.yaml full_demo.yaml
│   ├── policies/ authority.yaml constraints.yaml escalation.yaml
│   ├── fixtures/ llm_cache.json zones.geojson
│   └── tests/   unit/ scenario/ property/
├── frontend/
│   └── src/
│       ├── app/                   # router, layout shells
│       ├── windows/control/       # Window A
│       ├── windows/main/          # Window B pages
│       ├── components/            # Badge, Button, StatTile, Drawer, LogConsole, ...
│       ├── stores/                # zustand slices
│       ├── api/                   # REST + WS client
│       ├── styles/tokens.css
│       └── tests/
├── e2e/                           # Playwright specs
├── docs/ demo-script.md  slides/
└── README.md
```

---

## 5. Data Models

```python
class AgentIdentity(BaseModel):
    id: str                         # "nav-01"
    name: str                       # "Navigation Agent"
    role: Literal["navigation","collision","energy","engine","weather","rogue"]
    version: str
    vendor: str                     # flavor: shows multi-vendor reality
    status: Literal["idle","active","escalated","paused","quarantined","offline"]
    autonomy_level: int             # 0 advise · 1 recommend · 2 act-with-approval · 3 act
    priority: int
    read_scopes: list[str]          # "cop.ownship", "cop.contacts", "sensor.radar", ...
    tools: list[str]                # "set_heading", "set_speed", "shed_load", ...
    action_limits: dict             # {"set_heading": {"max_delta_deg": 15}}
    resource_quota: dict            # {"intents_per_min": 30, "power_kw": 50}
    trust_score: float              # 0–100, drops on violations
    heartbeat_at: datetime
    description: str

class Intent(BaseModel):
    id: UUID
    agent_id: str
    kind: Literal["recommend","act"]
    action: str
    params: dict
    target_resource: str            # "rudder" | "main_engine" | "power_bus" | "route"
    objective: str
    rationale: str
    confidence: float
    created_at: datetime
    expires_at: datetime
    reversible: bool
    inverse: dict | None
    status: Literal["pending","approved","rejected","escalated","executed","rolled_back","expired"]
    pipeline: dict[str, Literal["pass","fail","skip","pending"]]   # drives the UI stepper

class Decision(BaseModel):
    intent_id: UUID
    outcome: Literal["approved","rejected","escalated","merged"]
    reasons: list[str]
    policy_hits: list[str]
    conflict_ids: list[str]
    decided_by: str                 # "warden" | "operator:<name>" | "timeout-default"
    at: datetime

class Conflict(BaseModel):
    id: str
    type: Literal["resource","objective","oscillation","cumulative"]
    severity: Literal["low","medium","high","critical"]
    intent_ids: list[UUID]
    agent_ids: list[str]
    summary: str
    resolution: str | None
    status: Literal["open","auto_resolved","escalated","resolved"]

class LedgerEvent(BaseModel):
    seq: int
    ts: datetime
    type: str                       # INTENT_SUBMITTED, DECISION, EXECUTED, ALERT, CONTROL, ...
    actor: str
    payload: dict
    prev_hash: str
    hash: str
```

---

## 6. Synthetic Data & Scenarios

Because no dummy data exists yet, `sim/generator.py` produces it all, deterministically from a **seed**:

| Stream | How it's generated |
|---|---|
| Own-ship | Kinematics at 1 Hz × `TIME_SCALE` (default 10) |
| AIS / radar contacts | Spawned randomly or by scenario; CPA/TCPA computed each tick |
| Weather | Slow random walk + scripted storm events |
| Engine telemetry | Temperature, RPM, vibration; injectable faults |
| Power bus | Generator capacity vs. loads |
| Zones | GeoJSON: traffic separation scheme, shallow water, emission control area |

**Scenario file format:**

```yaml
name: full_demo
seed: 42
time_scale: 10
duration_s: 360
events:
  - t: 0    { phase: "Calm transit" }
  - t: 30   { spawn_contact: {id: C-12, bearing: 45, range_nm: 4, course: 270, speed: 14} }
  - t: 90   { weather: {wind_kts: 35, visibility_nm: 1} }
  - t: 110  { engine_fault: {type: overheat, severity: 0.6} }
  - t: 160  { rogue_agent: {id: energy-01, behavior: [out_of_scope, spam]} }
  - t: 240  { phase: "Recovery" }
```

**Adapter rule:** real data from the organizers goes through `sim/adapters/<source>.py` into the same COP schema. Nothing else changes.

---

## 7. Simulated Agents

| ID | Agent | Goal | Intents | Built-in tension |
|---|---|---|---|---|
| nav-01 | Navigation | Follow route / keep ETA | set_heading, set_speed | Wants speed |
| col-01 | Collision Avoidance | Keep CPA ≥ 1 nm (COLREGs) | set_heading, set_speed | Fights Nav |
| nrg-01 | Energy Optimizer | Minimize fuel | set_speed, shed_load | Fights ETA & Engine |
| eng-01 | Engine Health | Protect machinery | cap_rpm, request_cooldown | Fights everyone |
| wx-01 | Weather Routing | Avoid heavy weather | reroute | Fights zones/ETA |
| rogue | (Energy, compromised) | Misbehaves on cue | rudder commands, spam, drift | Proves governance |

- Rule-based core logic → deterministic demos.
- The LLM writes `rationale` text (cached in demo mode).
- Optional: one "real LLM agent" producing structured Intents, to show Warden also governs non-deterministic AI.
- Each agent: asyncio task, reads **only** its scoped COP view (`cop.view(scopes)`), sends a heartbeat every 2 s.

---

## 8. Governance Pipeline

Each Intent passes these 8 stages. The status of each stage is stored in `intent.pipeline` and drawn as the **stepper** in the UI (same style as the Import→Update stepper in your screenshot).

| # | Stage | Fails when | Result on fail |
|---|---|---|---|
| 1 | **Authenticate** | Unknown, paused, quarantined, or stale heartbeat | Reject |
| 2 | **Authority** | Tool not granted; params beyond limits; `act` above autonomy | Reject or downgrade to recommend |
| 3 | **Constraints** | Predicted effect breaks CPA, zone, engine or power limits | Reject / escalate |
| 4 | **Quota** | Rate or resource budget exceeded | Reject + anomaly flag |
| 5 | **Conflicts** | Clashes with pending/active intents | Go to arbitration |
| 6 | **Arbitrate** | Equal priority or irreversible | Escalate |
| 7 | **Approve** | Human approval required | Wait (timeout → safe default) |
| 8 | **Execute** | Actuator error | Alert + auto-rollback |

**Priority ladder:** Safety of life > Collision > Machinery protection > Regulatory > Weather > Schedule > Efficiency.

**Conflict types:**
- *Resource:* same target, incompatible values (speed 16 vs 8).
- *Objective:* predicted outcomes oppose (ETA vs fuel).
- *Oscillation:* A↔B flip-flops ≥ 3 times in 60 s.
- *Cumulative:* each fine alone; simulated together they break a constraint.

**Policy examples (`policies/constraints.yaml`):**

```yaml
- id: MIN_CPA
  when: action in [set_heading, set_speed]
  check: predicted.min_cpa_nm >= 1.0
  on_fail: reject
  severity: critical
- id: ENERGY_NO_PROPULSION_CONTROL
  agent_role: energy
  deny_targets: [rudder]
  on_fail: reject
  severity: high
- id: BIG_TURN_NEEDS_HUMAN
  when: action == set_heading and abs(delta) > 30
  on_fail: escalate
- id: IRREVERSIBLE_NEEDS_HUMAN
  when: not reversible
  on_fail: escalate
```

---

## 9. Runtime Monitors

Run every tick, independent of the pipeline (they catch what slips through):

- **Constraint watchdog:** live CPA, zone proximity, engine temp, power margin.
- **Agent anomalies:** intent-rate spike, repeated rejections, scope-violation attempts, stale heartbeat, low-confidence streak, action-distribution drift (z-score).
- **Emergent behavior:** oscillation, livelock (mutual overrides), too many agents on one actuator.
- **Auto-containment:** 3 critical violations in 60 s → quarantine; trust score drops; alert raised.

---

## 10. Operator Controls

| Control | Effect | Confirmation in UI |
|---|---|---|
| Pause / Resume agent | Stop intake of its intents | Single click |
| Constrain | Tighten limits live | Modal with sliders |
| Downgrade autonomy | e.g. act → recommend | Dropdown |
| Override | Operator intent at top priority | Modal form |
| Approve / Reject | Escalation queue | Card buttons |
| Rollback | Apply inverse of last N actions (agent or time window) | Modal preview of what will be undone |
| Quarantine | Isolate agent, drop its pending intents | Red button + confirm |
| **SAFE-STOP** | Pause all agents, hold heading, safe speed, alert | **Hold 2 s** (like "Hold Shift" on your Stop Server button) |

Every control writes a `CONTROL` ledger event with operator name + reason.

---

## 11. Audit, Explainability & LLM Use

- **Ledger:** append-only SQLite; each row has `hash = sha256(prev_hash + payload)`. A "Verify chain" button proves it's untouched.
- **Why? panel:** for any intent: what the agent saw (scoped COP snapshot), rationale, each pipeline stage result, policy hits, conflicts, who decided.
- **LLM uses** (all go through `explain.py` with cache + timeout + fallback text):
  1. Agent rationale text.
  2. Conflict summary for escalation cards.
  3. "Explain last 5 minutes" incident report.
  4. Natural-language question box: "Why did we slow down at 14:32?" answered from ledger rows.
- **Replay:** re-run scenario + ledger to reproduce any incident.

---

## 12. API Spec

```
GET    /api/summary                    header KPIs
GET    /api/agents                     list
GET    /api/agents/{id}                detail + metrics
POST   /api/agents/{id}/pause | resume | quarantine | release
PATCH  /api/agents/{id}/limits         constrain
PATCH  /api/agents/{id}/autonomy
GET    /api/cop
GET    /api/intents?status=&agent=
GET    /api/intents/{id}/explain
GET    /api/conflicts?status=
GET    /api/escalations
POST   /api/escalations/{id}/approve | reject
POST   /api/control/override
POST   /api/control/rollback           {agent_id?, since?, count?}
POST   /api/control/safe-stop | resume-all
GET    /api/ledger?from=&to=&type=
GET    /api/ledger/verify
GET    /api/policies
POST   /api/scenarios/{name}/start | pause | reset
POST   /api/inject/{fault}
POST   /api/ask                        natural-language question
WS     /ws                             all live events
```

**WebSocket message envelope:**

```json
{ "type": "intent.decided", "ts": "2026-10-25T19:41:20Z", "data": { ... } }
```
Types: `cop.tick`, `agent.status`, `intent.submitted`, `intent.decided`, `intent.executed`, `conflict.opened`, `conflict.resolved`, `escalation.opened`, `escalation.closed`, `alert`, `control`, `ledger.append`, `summary`.

---

## 13. UI Design System (matched to reference screenshots)

### 13.1 What the screenshots establish

| Observed in screenshots | Apply as |
|---|---|
| Near-black UI, black title bar, thin dividers | Dark-only theme, layered surfaces |
| Square rounded logo tile with 2 initials ("DN") | "BW" tile, red in Control window, blue in main app (same as yours) |
| App name + small grey path/version under it | "Bridge Warden" + `v.26_1025 · Vessel: MV Demo` |
| Right-aligned KPI stats in header | Active Agents · Pending Intents · Open Conflicts · Escalations |
| Big green "ONLINE" status words | Warden Core / Sim Engine status |
| Numbered worker tiles with Idle/Reading/Writing legend | **Agent tiles** with Idle/Active/Escalated/Quarantined legend |
| Full-width green "Stop Server (Hold Shift…)" | Full-width red **SAFE-STOP (Hold 2s)** |
| Blue caps section label + 2-column blue buttons | "GOVERNANCE ACTIONS" |
| Orange caps section label + orange buttons | "SCENARIO ACTIONS" (demo/dev tools) |
| Monospace log, timestamp column, colored text, green highlighted row | Live ledger console; critical rows highlighted |
| Sidebar "Menu" with icons, blue active pill, collapsible "+" groups | Same sidebar |
| Top toolbar: blue primary + grey secondary button | "New Policy" / "Export" etc. |
| Table with grey uppercase headers; rows as rounded dark cards | Agents/Intents/Conflicts tables |
| Outlined, tinted uppercase badges (green/blue/purple/yellow) | Status/role/autonomy/severity badges |
| Colored % status (red 5%, orange 25%) | Trust score coloured by level |
| Right-side detail drawer: badge + breadcrumb, title, slug + UUID in mono, icon key/value grid in 2 columns, Description + Edit | Agent / Intent / Conflict detail drawer |
| Circle stepper 1–6 with labels | 8-stage governance pipeline stepper |
| Row of stat tiles (Disk Used, Rows…) | Intents/min, Approved, Rejected, Escalated, Rollbacks |
| Button row: orange primary, grey, grey, red Delete | Override (orange), Constrain, Rollback (grey), Quarantine (red) |
| Tabs with blue underline ("Activity") + log | Activity · Intents · Policy Hits · Explain |

### 13.2 Design tokens (`styles/tokens.css`)

```css
:root {
  /* surfaces */
  --bg-titlebar:   #000000;
  --bg-app:        #1b1b1b;   /* main background */
  --bg-panel:      #1f1f1f;   /* sidebar / left panel */
  --bg-row:        #2b2b2b;   /* table row cards */
  --bg-row-hover:  #333333;
  --bg-tile:       #232323;   /* stat tiles */
  --border:        #333333;
  --border-strong: #444444;

  /* text */
  --text:          #f2f2f2;
  --text-muted:    #9a9a9a;
  --text-dim:      #6e6e6e;

  /* brand / actions */
  --blue:          #3b82f6;   /* primary buttons, active nav, tabs */
  --blue-hover:    #2f6fd6;
  --green:         #3fae6a;   /* ONLINE, approve, highlight row */
  --orange:        #e8892b;   /* dev/scenario actions, Override */
  --red:           #e5484d;   /* SAFE-STOP, Quarantine, critical */
  --grey-btn:      #3a3a3a;   /* secondary buttons */

  /* badge tints (fg / border / bg 15%) */
  --badge-green:   #4cc38a;
  --badge-blue:    #5aa2ff;
  --badge-purple:  #b58cf0;
  --badge-yellow:  #e2b33c;
  --badge-red:     #ff6b6b;

  /* log colors */
  --log-pink:      #ff6fa3;   /* system messages (as in screenshot) */
  --log-cyan:      #4dd0e1;   /* engine/agent messages */
  --log-yellow:    #f5c542;   /* warnings */

  --radius-sm: 4px;  --radius: 6px;  --radius-lg: 8px;
  --font-ui:   "Exo 2", "Segoe UI", sans-serif;
  --font-mono: "JetBrains Mono", "Consolas", monospace;
}
```

> Font note: the screenshots use a geometric, slightly squared sans. **Exo 2** (or **Saira Semi Condensed**) is the closest free match. Logs use a narrow mono; JetBrains Mono or Share Tech Mono.

### 13.3 Type scale

| Use | Size / weight |
|---|---|
| App title | 20px / 700 |
| Page/drawer title | 22px / 700 |
| Section label (caps, coloured) | 13px / 700, letter-spacing 0.08em |
| Table header | 11px / 600, caps, muted |
| Body / row | 14px / 500–600 |
| KPI value | 18px / 700 |
| KPI label | 12px / 400 muted |
| Big status ("ONLINE") | 18px / 700 green |
| Log | 12px mono |
| Mono IDs (slug/UUID) | 11px mono muted |

### 13.4 Core components (build these first)

| Component | Spec |
|---|---|
| `LogoTile` | 40×40, radius 6, bg red or blue, white 2-letter initials |
| `HeaderStat` | label (muted 12px) above value (18px bold), right-aligned |
| `Button` | variants: `primary` (blue), `success` (green), `warn` (orange), `danger` (red), `secondary` (grey); height 32; radius 4; bold 14px; optional subtitle line (like "Hold Shift For Restart") |
| `HoldButton` | fills a progress bar left→right during the hold; fires at 100%; used for SAFE-STOP |
| `Badge` | uppercase 11px bold, 1px coloured border, 15% tinted bg, radius 4, padding 2×8 |
| `StatusWord` | 18px bold: ONLINE green, DEGRADED yellow, OFFLINE red |
| `AgentTile` | 22×22 numbered square, radius 3, colour by state + legend |
| `SectionLabel` | coloured caps label |
| `TableRowCard` | rounded 6, `--bg-row`, 44px tall, selected = 1px blue border |
| `Drawer` | right side, 50% width (min 560px), close ×, header with badge + breadcrumb |
| `KVGrid` | 2 column pairs; icon + muted label; bold value; "+" icon for lists |
| `Stepper` | numbered circles joined by line; colour by stage result; small status text above label |
| `StatTile` | bordered box, label / big value / small muted subtext |
| `Tabs` | blue text + 2px blue underline for active |
| `LogConsole` | mono, column headers, auto-scroll toggle, highlight rules, filter box |
| `Modal` | dark panel, title, body, footer buttons right |
| `Toast` | bottom-right, coloured left bar |
| `CountdownRing` | for escalation timeouts |

### 13.5 Colour meaning (keep consistent everywhere)

| Colour | Meaning |
|---|---|
| Green | Online, approved, executed, healthy |
| Blue | Primary navigation/actions, info, "recommend" |
| Yellow | Pending, escalated, warning |
| Orange | Operator override, scenario/demo tools, medium severity |
| Red | Rejected, quarantined, critical, SAFE-STOP |
| Purple | Policy / regulatory items |
| Grey | Idle, paused, expired |

Never rely on colour alone: every state also has a text label.

---

## 14. UI Layout: Window A — Warden Control

*Modelled on your "Data Noodler Server" window.* This is the **engine room** view: system health, agent tiles, global controls and the live ledger.

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ [BW] Bridge Warden Control                       Active   Pending   Open       Esc.  │
│      MV Demo · scenario: full_demo · seed 42     Agents   Intents   Conflicts  ations│
│                                                    5        3         1          1   │
├──────────────────────────┬───────────────────────────────────────────────────────────┤
│ Current Operator         │ TIMESTAMP           TYPE      AGENT   OUTCOME  MESSAGE    │
│ christopher_staggs       │ 2026-10-25 19:41:19 · Warden started. 5 agents registered │
│ Watch: Bridge (OOW)      │ 2026-10-25 19:41:20 · Policies loaded (14 rules)          │
│                          │ 2026-10-25 19:41:21 · nav-01 INTENT set_speed 16 kn       │
│ Uptime      Sim Time     │ 2026-10-25 19:41:21 · DECISION approved (warden)          │
│ 0:04:12     14:32:10     │ ███ 2026-10-25 19:42:03 · CONFLICT col-01 vs nav-01 ███   │
│                          │ 2026-10-25 19:42:04 · ARBITRATED → col-01 wins (safety)   │
│ Warden Core   Sim Engine │ ...                                                       │
│ ONLINE        ONLINE     │                                                           │
│                          │                                                           │
│ Agents                   │                                                           │
│ ■Idle ■Active ■Esc ■Quar │                                                           │
│ [1][2][3][4][5][6]       │                                                           │
│                          │                                                           │
│ ┌──────────────────────┐ │                                                           │
│ │      SAFE-STOP       │ │                                                           │
│ │  (Hold 2s to engage) │ │                                                           │
│ └──────────────────────┘ │                                                           │
│                          │                                                           │
│ GOVERNANCE ACTIONS       │                                                           │
│ [Pause All ][Resume All] │                                                           │
│ [Rollback..][Clear Alert]│                                                           │
│ [Policies  ][Verify Lgr] │                                                           │
│ [Export Ledger]          │                                                           │
│                          │                                                           │
│ SCENARIO ACTIONS         │                                                           │
│ [Load Scen.][Start/Pause]│                                                           │
│ [Inject Fault][Spawn Rog]│                                                           │
│ [Open Main App]          │                                                           │
└──────────────────────────┴───────────────────────────────────────────────────────────┘
```

### 14.1 Left panel (width 365px, bg `--bg-panel`, padding 20)

| Block | Content | Behaviour |
|---|---|---|
| Current Operator | Name (bold 18) + watch role | Click → switch operator (for demo of approvals by role) |
| Uptime / Sim Time | 2-column | Live |
| Warden Core / Sim Engine | `StatusWord` | Goes red if WS drops or tick stalls > 3 s |
| Agents | Legend + `AgentTile` grid numbered 1..N | Hover = tooltip (id, role, status, trust); click = open agent drawer in Window B; tile pulses yellow on escalation; red when quarantined |
| SAFE-STOP | Full-width red `HoldButton`, 54px tall | Hold 2 s → confirm toast; turns green "RESUME OPERATIONS (Hold 2s)" when engaged (mirrors your Stop/Start toggle) |
| GOVERNANCE ACTIONS (blue label) | 2-column blue buttons | See table below |
| SCENARIO ACTIONS (orange label) | 2-column orange buttons | Demo and testing tools |

**Governance actions:**

| Button | Action |
|---|---|
| Pause All | Pause every agent (without safe-stop manoeuvre) |
| Resume All | Resume all non-quarantined agents |
| Rollback… | Modal: choose agent / last N / time window, preview list, confirm |
| Clear Alerts | Acknowledge all alerts (logged) |
| Policies | Opens Policies page in Window B |
| Verify Ledger | Runs hash-chain check → green "Chain valid (1,284 events)" toast |
| Export Ledger | Downloads JSON/CSV |

**Scenario actions:**

| Button | Action |
|---|---|
| Load Scenario | Dropdown of YAML scenarios + seed |
| Start / Pause Sim | Toggle |
| Inject Fault | Menu: engine overheat, sensor dropout, GPS drift, comms loss |
| Spawn Rogue | Turns rogue behaviour on for a chosen agent |
| Open Main App | Opens Window B |

### 14.2 Right panel: Live ledger console

- Columns: `TIMESTAMP  TYPE  AGENT  OUTCOME  MESSAGE` (grey caps header like `METHOD CODE SIZE MS PATH`).
- Colours: system = pink, agent intents = cyan, approvals = green, warnings = yellow, rejections/critical = red.
- **Highlighted row** (green background, like "SERVER STARTED"): safe-stop engaged, scenario start, conflict resolved by operator.
- **Red highlighted row:** quarantine, critical constraint breach.
- Auto-scroll with "pause scroll" on mouse hover; filter box (type/agent); max 2,000 rows in DOM (virtualised).

### 14.3 Header

- Left: `LogoTile` (red, "BW"), title, muted subtitle line (vessel · scenario · seed).
- Right: 4 `HeaderStat`s. Pending Intents turns yellow > 5; Open Conflicts red > 0; Escalations pulses when > 0.

---

## 15. UI Layout: Window B — Bridge Warden Main App

*Modelled on your "Data Noodler (SMPG)" window.* This is the **bridge officer's** view: agents, decisions, approvals and the operating picture.

### 15.1 Shell

```
┌────────────────────────────────────────────────────────────────────────────────────┐
│ [BW] Bridge Warden (MV Demo)                      ⚠ 1 Escalation   ● LIVE  14:32:10│
│      v.26_1025                                                                     │
├───────────────┬────────────────────────────────────────────────────────────────────┤
│ Menu        < │  [Primary Action] [Secondary]                    🔍 filter         │
│ ▣ Overview    │  ─────────────────────────────────────────────────────────────     │
│ ▣ Agents      │  PAGE CONTENT (table / map / cards)    │  DETAIL DRAWER (on select)│
│ ▣ Intents     │                                        │                           │
│ ▣ Conflicts  1│                                        │                           │
│ ▣ Escalations1│                                        │                           │
│ ▣ Authority   │                                        │                           │
│ ▣ Ledger      │                                        │                           │
│               │                                        │                           │
│ Policies  ˅ + │                                        │                           │
│   Constraints │                                        │                           │
│   Authority   │                                        │                           │
│   Escalation  │                                        │                           │
│               │                                        │                           │
│ Vessel    ˅ + │                                        │                           │
│   MV Demo     │                                        │                           │
│               │                                        │                           │
│ Help      ˅   │                                        │                           │
│ ▣ Warden Ctrl │                                        │                           │
│ ▣ Ask Warden  │                                        │                           │
└───────────────┴────────────────────────────────────────────────────────────────────┘
```

- Sidebar width 248px; "Menu" title 20px bold with collapse `<`.
- Active item: blue filled pill, white text. Icons: lucide (`Compass`, `Bot`, `Send`, `Swords`, `BellRing`, `Grid3x3`, `ScrollText`, `Shield`, `Ship`, `Server`, `MessageCircleQuestion`).
- Count badges (red pill) on Conflicts/Escalations.
- Collapsible groups with `˅` and `+` exactly like Teams / Workspace.
- Header right: escalation alert chip (click → Escalations), LIVE indicator (green dot; grey "REPLAY" in replay mode), sim clock.

### 15.2 Drawer pattern (shared by Agents, Intents, Conflicts)

When a row is selected the table compresses to fewer columns (like your 3rd screenshot) and the drawer opens on the right:

```
┌────────────────────────────────────────────────────────────────┐
│ [ACTIVE]  MV Demo / agents / col-01                          × │
│                                                                │
│ Collision Avoidance Agent                                      │
│ col-01  ·  4f2a9c1e-77b0-4d1e-9a10-3c8e2b6f0d21                │
│                                                                │
│ 👤 Role        COLLISION         ⚑ Autonomy   [ACT · L3]       │
│ ⊕ Read Scopes  + cop.ownship     🛠 Tools      + set_heading    │
│                + cop.contacts                  + set_speed      │
│                + sensor.radar                                   │
│ ⏱ Heartbeat    0.8 s ago         🛡 Trust      92%              │
│ ⚖ Limits       Δhdg ≤ 30°        📦 Quota      12/30 per min    │
│               Δspd ≤ 6 kn                                       │
│ 📅 Registered  Oct 25, 19:41     🏷 Vendor     SeaSense v2.3    │
│                                                                │
│ Description                                            [Edit]  │
│ Maintains COLREGs-compliant CPA ≥ 1 nm with all contacts.      │
│ ───────────────────────────────────────────────────────────────│
│ Last intent pipeline: set_speed 8 kn                           │
│ (1)──(2)──(3)──(4)──(5)──(6)──(7)──(8)                          │
│ Auth Authz Cnstr Quota Confl Arbit Apprv Exec                   │
│ ───────────────────────────────────────────────────────────────│
│ [Intents/min 4] [Approved 31] [Rejected 2] [Escalated 1] [Rb 0] │
│ ───────────────────────────────────────────────────────────────│
│ [Override] [Constrain] [Downgrade] [Rollback] [Pause] [Quarant.]│
│   orange      grey        grey       grey      grey     red     │
│ ───────────────────────────────────────────────────────────────│
│ Activity | Intents | Policy Hits | Explain                     │
│ 2026-10-25 14:23:59  col-01: CPA 0.6 nm with C-12 → slow to 8 kn│
│ ...                                                            │
└────────────────────────────────────────────────────────────────┘
```

**Stepper colour rules:** pass = green ring, fail = red ring, escalated = yellow ring, pending = grey ring with spinner, skip = dim. The small text above each label shows the result ("Passed", "Failed", "Waiting"), just as "Changed" appears above each step in your screenshot.

---

## 16. UI Screens in Detail

### 16.1 Overview (Common Operating Picture)
Layout: map 65% left, right column 35%.

- **Map:** dark sea background; own-ship as a blue triangle with heading line + 10-min predicted track; contacts as markers with CPA rings (green ≥ 2 nm, yellow 1–2, red < 1); GeoJSON zones (shallow = red hatched, TSS = blue outline, ECA = purple outline); a dashed line for any **pending intent's predicted track** (hover shows which agent proposed it).
- **Right column:**
  - Own-ship card (heading, speed, RPM, engine temp, power margin, fuel rate) using `StatTile`s.
  - Environment card (wind, sea state, visibility).
  - "Now happening" feed: last 10 decisions.
  - Mini conflict list with severity badges.
- **Top toolbar:** `[Pause Sim]` (blue), `[Replay]` (grey), time-scale dropdown (1×, 5×, 10×).

### 16.2 Agents
Toolbar: `[Register Agent]` (blue), `[Quarantined]` (grey filter toggle, like your "Trash" button).

| Column | Content |
|---|---|
| AGENT NAME | Bold name |
| ID | Mono muted |
| ROLE | Badge (blue) |
| AUTONOMY | Badge: ADVISE grey / RECOMMEND blue / ACT-W-APPROVAL yellow / ACT green |
| STATUS | Badge: ACTIVE green / IDLE grey / ESCALATED yellow / PAUSED grey / QUARANTINED red |
| TRUST | Coloured % (≥ 80 green, 50–79 orange, < 50 red), like your Status column |
| INTENTS/MIN | Right-aligned number + tiny sparkline |
| LAST ACTION | Muted text |
| HEARTBEAT | Relative time; red if stale |

Select row → agent drawer (section 15.2).

### 16.3 Intents
Toolbar: `[Submit Manual Intent]` (blue = operator override), status filter chips (All · Pending · Approved · Rejected · Escalated · Executed · Rolled back).

Columns: TIME · AGENT · ACTION · PARAMS (mono) · TARGET · CONFIDENCE · STATUS badge · DECIDED BY.
Drawer: intent title "set_speed → 8 kn", stepper big and central, KV grid (objective, rationale, confidence, reversible, inverse, expires), policy hits list with purple badges, linked conflicts, **Explain** tab (LLM narrative + "what the agent saw" snapshot), buttons `[Approve]` green, `[Reject]` red (if escalated), `[Roll back]` grey (if executed).

### 16.4 Conflicts
Card list rather than a table (more readable):

```
┌──────────────────────────────────────────────────────────────────────┐
│ [HIGH] [RESOURCE]   main_engine speed           opened 14:31:52  ⏵   │
│ nav-01 wants 16 kn (ETA)   ⟷   col-01 wants 8 kn (CPA 0.6 nm)        │
│ Resolution: AUTO → col-01 (Collision > Schedule)          [Details]  │
└──────────────────────────────────────────────────────────────────────┘
```
Drawer: side-by-side comparison of the two intents (predicted CPA, ETA delta, fuel delta per option), a small chart of the predicted outcomes, the LLM summary, buttons `[Accept A]` `[Accept B]` `[Merge (conservative)]` `[Escalate]`.

### 16.5 Escalations (Approval inbox)
- Card per escalation, oldest first. The colour of the left border shows severity.
- Each card: title, LLM summary (2–3 lines), options as buttons, **countdown ring** with "Safe default in 0:45 → Hold course, reduce to 10 kn".
- Keyboard: `A` approve recommended, `R` reject, `↑/↓` move.
- After decision: card slides to "Resolved" tab with who decided and when.

### 16.6 Authority Matrix
Grid: rows = agents, columns = sensors/data scopes + tools.
Cells: `R` read (blue), `Rc` recommend (yellow), `A` act (green), blank = none. Red outlined cell = attempted violation in last 10 min.
Click cell → edit permission (logged). Toggle "Show attempted violations".

### 16.7 Ledger
Full-page `LogConsole` with filters (time range, type, agent, outcome), `[Verify Chain]` (blue), `[Export]` (grey), `[Incident Report]` (orange → LLM summary of selected range in a modal, downloadable as markdown).

### 16.8 Policies
List grouped by file (Constraints / Authority / Escalation). Each row: ID (mono), description, severity badge, on_fail badge, hit count, enabled toggle. Drawer shows YAML (read-only by default; Edit enables a code editor + "Validate" button).

### 16.9 Ask Warden
Chat box: "Why did we slow down at 14:32?" Answers cite ledger rows (clickable seq numbers). Uses the ledger as context only, no actions.

### 16.10 Global UI behaviours
- **Toasts** on: escalation opened, quarantine, safe-stop, ledger verify.
- **SAFE-STOP banner:** when engaged, a full-width red bar under the header in both windows: "SAFE-STOP ENGAGED by christopher_staggs at 14:35:02 · All agents paused · [Resume (hold)]".
- **Connection lost:** grey overlay "Reconnecting to Warden…" with auto-retry.
- **Keyboard:** `Ctrl+K` command palette (jump to agent, pause agent, open escalation); `Esc` closes drawer.
- **Responsiveness:** designed for 1440×900+ desktop; drawer becomes full-screen under 1100px.
- **Accessibility:** text labels on all colour states; focus rings in blue; min contrast 4.5:1.

---

## 17. Frontend Architecture

### 17.1 Routing
```
/control                     Window A
/                            → /overview
/overview  /agents/:id?  /intents/:id?  /conflicts/:id?
/escalations  /authority  /ledger  /policies/:id?  /ask
```
Opening both windows: `msedge --app=http://localhost:5173/control` and `msedge --app=http://localhost:5173/`.

### 17.2 State
- `useLiveStore` (Zustand): cop, agents map, intents map (last 500), conflicts, escalations, alerts, ledger ring buffer, connection status, safeStop flag.
- WS client: single connection, exponential reconnect, on reconnect → refetch `/api/summary`, `/api/agents`, `/api/escalations`.
- REST via React Query for detail fetches and mutations; mutations are optimistic only for pause/resume.

### 17.3 Performance
- Throttle `cop.tick` rendering to 4 fps on the map.
- Virtualise log console and long tables.
- Memoise rows by id + version.

### 17.4 Folder structure (frontend/src)
```
components/  Badge.tsx Button.tsx HoldButton.tsx StatTile.tsx Drawer.tsx KVGrid.tsx
             Stepper.tsx Tabs.tsx LogConsole.tsx AgentTile.tsx HeaderStat.tsx Modal.tsx
windows/control/  ControlWindow.tsx LeftPanel.tsx ActionsGrid.tsx
windows/main/     Shell.tsx Sidebar.tsx pages/*.tsx drawers/*.tsx
stores/  live.ts ui.ts
api/     ws.ts rest.ts types.ts   # types generated from backend JSON Schema
```
Generate TS types from Pydantic: `uv run python scripts/export_schema.py` → `pnpm dlx json-schema-to-typescript`.

---

## 18. Build Timeline & Team Split

**Roles (3–4 people):**
- **A: Backend core** (pipeline, policy, conflicts, ledger, API)
- **B: Sim & agents** (world, generator, scenarios, agents, LLM)
- **C: Frontend** (design system, Window A, Window B)
- **D: Demo & QA** (scenarios tuning, tests, slides, video), or split between B and C if 3 people

**Assumes 36 hours; scale proportionally.**

| Hours | A: Backend | B: Sim/Agents | C: Frontend | D: Demo/QA |
|---|---|---|---|---|
| 0–2 | Repo, schemas, bus, WS stub | World kinematics, seeded generator | Vite + Tailwind + tokens + fonts | Install check on all laptops; story/pitch |
| 2–6 | Registry, ledger (hash chain) | nav, col, nrg agents | Components: Button, Badge, StatTile, LogConsole | Fixture scenarios; API collection in Bruno |
| 6–10 | Authority + constraint checks, predictor | Scenario runner, CPA/TCPA | Window A complete with live log | pytest scaffolding |
| 10–14 | Conflict detection, arbiter | eng, wx agents | Shell + sidebar + Agents table + drawer | Scenario tests |
| 14–18 | Escalation queue + timeouts, controls | Rogue agent, fault injection | Stepper, Intents page, Conflicts page | Playwright smoke test |
| 18–22 | Rollback, safe-stop, monitors, quarantine | LLM rationale + summaries + cache | Escalations inbox, map Overview | First full demo run-through |
| 22–26 | Explain endpoint, ask endpoint | Tune `full_demo.yaml` | Authority matrix, Ledger, Policies | Fix list from run-through |
| 26–30 | Hardening, error handling | LLM cached mode, fallbacks | Polish, SAFE-STOP banner, toasts | Slides; record backup video |
| 30–33 | **Feature freeze** → bugs only | | | Rehearse ×3, time it |
| 33–36 | Final rehearsal, pack, sleep if possible | | | |

**MVP cut line** (must work if everything else fails): registry → authority check → conflict detection → escalation approve/reject → pause + safe-stop → ledger → Window A + Agents table + Escalations inbox.

---

## 19. Test Plan

### 19.1 Unit tests (pytest)
| Area | Example tests |
|---|---|
| Authority | Energy agent `set_heading` → rejected; heading delta 40° with limit 15° → rejected; L1 agent `act` → downgraded to recommend |
| Constraints | Intent producing CPA 0.5 nm → rejected with `MIN_CPA`; turning into shallow zone → rejected |
| Quota | 31st intent in a minute → rejected + anomaly |
| Conflicts | Two speed intents 16 vs 8 → resource conflict; A/B flip 3× in 60 s → oscillation; two OK intents that together breach power → cumulative |
| Arbiter | Collision vs Navigation → Collision wins; equal priority → escalated |
| Escalation | Timeout fires safe default; approve executes; reject logs |
| Rollback | Execute 3 actions, roll back 2 → state equals state after action 1 |
| Ledger | Chain verifies; editing one row → verify fails at that seq |
| Safe-stop | All agents paused, pending intents dropped, speed set to safe value |

### 19.2 Property tests (hypothesis)
- For random intents: **no executed action ever violates a critical constraint**.
- Rollback(execute(x)) returns the original state for every reversible action.
- The ledger always verifies after random event sequences.

### 19.3 Scenario tests (deterministic, seeded)
Each scenario YAML has expected outcomes:
```yaml
expect:
  - at_least: {conflict.type: resource, count: 1}
  - agent_status: {nrg-01: quarantined, by_t: 200}
  - never: {cpa_nm_below: 1.0}
  - escalations_opened: ">=1"
```
`uv run pytest tests/scenario -k full_demo` runs headless at 100× speed.

### 19.4 Frontend tests
- **Vitest + Testing Library:** Badge colours by status, Stepper colours, HoldButton fires only after 2 s, LogConsole highlight rules.
- **Playwright E2E** (backend in test mode with `full_demo` at 20×):
  1. Window A loads; both statuses ONLINE; 5 agent tiles.
  2. Conflict appears in Conflicts page within N seconds.
  3. Escalation card appears → click Approve → intent shows EXECUTED.
  4. Rogue agent → tile turns red; Agents table shows QUARANTINED.
  5. Rollback modal → confirm → ledger shows ROLLBACK rows.
  6. Hold SAFE-STOP → banner appears in both windows.
  7. Verify Ledger → "Chain valid" toast.
- Visual check: Playwright screenshots of each page compared against the reference look (manual review, not pixel tests).

### 19.5 Chaos / robustness tests
- Kill the WS connection → UI shows reconnecting, recovers state.
- LLM timeout / no internet → cached text shown, no UI hang.
- Agent crashes (task exception) → heartbeat stale → OFFLINE badge + alert.
- 10× agents (spawn 50 dummy agents) → UI stays responsive (tests scale story).

### 19.6 Demo rehearsal tests (human)
- Run the full demo 3× end to end with a timer.
- Run once on projector resolution (often 1280×720 or 1920×1080) and check readability from the back of the room: bump font scale if needed (`html { font-size: 112% }` toggle).
- Run once with Wi-Fi off.

---

## 20. Demo Plan

### 20.1 Setup before going on stage
1. Laptop on power, notifications off (Windows Focus), display scaling 100–125%.
2. Backend running with `LLM_MODE=cached` (switch to live only if the network is solid).
3. Window A on left half / screen 1, Window B on right half / screen 2.
4. `full_demo` loaded, paused at t=0, seed 42.
5. Backup video open in a background tab/player.
6. Second team member ready as "operator" if you want two voices.

### 20.2 Demo script (≈ 6 minutes)

| Time | What you say | What you click / show | What proves the challenge |
|---|---|---|---|
| 0:00–0:40 | Problem: many AI systems onboard, each fine alone, unpredictable together. "We didn't build another agent. We built the referee." | Architecture slide (1 slide) | Framing |
| 0:40–1:30 | "Here's every AI on board, what it can see, and what it's allowed to do." | Window A agent tiles → Window B Agents table → open col-01 drawer → Authority Matrix | Q1, Q2, Q3 |
| 1:30–2:30 | Start scenario. Crossing vessel appears. Nav wants 16 kn, Collision wants 8 kn. | Start Sim → Overview map shows CPA ring turning red + dashed predicted tracks → Conflicts page → card auto-resolved → drawer stepper + LLM explanation | Q4, explainability |
| 2:30–3:30 | Storm + engine overheat: Weather, Engine and Energy all pull different ways; together they'd overload power. Equal priority → human decides. | Escalation chip pulses → Escalations inbox → read summary, show countdown + safe default → click Approve | Cumulative conflict, HITL |
| 3:30–4:30 | "Now an agent goes wrong." Energy agent tries to steer and floods requests. | Spawn Rogue → log shows red REJECTED rows → tile turns red → QUARANTINED badge; Authority Matrix shows red attempted-violation cells | Q5, containment |
| 4:30–5:15 | "Undo what it did." | Rollback… → choose nrg-01 last 3 → preview → confirm → ledger ROLLBACK rows; Downgrade nrg-01 to RECOMMEND | Q6 rollback/constrain |
| 5:15–5:40 | "And if all else fails, one control stops everything." | Hold SAFE-STOP → red banner in both windows | Q6 safe-stop |
| 5:40–6:00 | "Everything is auditable." | Verify Ledger → "Chain valid"; Incident Report → LLM summary of the whole run | Accountability |
| Close | Value: vendor-neutral, works with any agent that speaks the Intent schema; humans stay in charge. | Final slide | |

### 20.3 Backup paths
| Failure | Fallback |
|---|---|
| Scenario timing off | Scenario actions → Inject manually (each beat has a button) |
| LLM slow/down | Cached mode (pre-filled text) |
| Map tiles fail | SVG sea background (default) |
| Laptop/app crash | Play the recorded video and narrate over it |
| Projector resolution ugly | Font-scale toggle; collapse sidebar |

### 20.4 Expected judge questions (prepare answers)
- *How does a real agent plug in?* It publishes Intents in our JSON schema over HTTP/WS; Warden is the only path to actuators (adapter per vendor).
- *What if Warden fails?* Fail-safe: actuators keep the last approved state, agents can't act, bridge alarm; Warden itself can run redundant (hot standby).
- *Isn't this slower?* The pipeline is in-memory, milliseconds; only escalations wait on humans, and they have timeouts with safe defaults.
- *Why trust the LLM?* LLM only explains and summarises; all decisions come from deterministic policies.
- *Regulations?* Policies can be tagged to COLREGs rules and IMO MASS code concepts; the ledger supports incident investigation.
- *Scale?* Show the 50-agent chaos test.

### 20.5 Recording the backup video
- OBS: 1920×1080, 30 fps, display capture, mic on.
- Record the full script once clean; keep under 6 minutes.
- Export MP4; keep a copy on a USB stick and in the cloud.

---

## 21. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| LLM latency/outage | Rule-based decisions; LLM text cached; 3 s timeout |
| Organizer data arrives in odd format | Adapter layer into COP schema |
| Scope creep | MVP cut line; feature freeze at hour 30 |
| UI polish eats time | Build the component kit first; every page reuses it |
| Live demo breaks | Deterministic seed, manual injection buttons, backup video |
| Team laptops differ | Section 2 install check at hour 0; one "demo laptop" chosen early |

---

## 22. Stretch Goals

- Open **Intent protocol spec** (JSON Schema published in repo) for vendor-neutral integration.
- **Shielding:** precomputed safe action envelope so unsafe actions are blocked mathematically.
- **What-if preview:** operator sees predicted 10-minute outcome before approving.
- **Trust-based autonomy:** autonomy level rises/falls automatically with trust score.
- **Multi-vessel:** two ships, two Wardens negotiating a crossing.
- **Tauri wrapper** for a true desktop app look like your screenshots.

---

## 23. Pre-Hackathon Checklist

**Software (each team laptop)**
- [ ] Git, Python 3.12, uv, Node 22, pnpm, VS Code + extensions installed (§2)
- [ ] `pnpm exec playwright install chromium` done
- [ ] LLM API key pulled from the vault and tested with a one-line script
- [ ] DB Browser for SQLite, Bruno, OBS installed on the demo laptop

**Repo prep (allowed? check hackathon rules on pre-written code)**
- [ ] Empty repo with folder structure, `.env.example`, README
- [ ] Design tokens + fonts downloaded
- [ ] Offline map background prepared

**Demo laptop**
- [ ] HDMI/USB-C adapters, charger, phone hotspot
- [ ] Focus mode on, desktop clean, browser bookmarks bar hidden
- [ ] Two Edge app windows tested side by side at projector resolution

**Team**
- [ ] Roles assigned (A/B/C/D)
- [ ] Pitch story agreed (problem → roster → conflict → human decision → rogue → rollback → safe-stop → audit)
- [ ] Who presents, who clicks
