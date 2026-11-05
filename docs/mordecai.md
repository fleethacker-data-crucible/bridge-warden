# MORDECAI in Bridge Warden

**MORDECAI** = Multi-agent Oversight, Resource Deconfliction, Emergent Conflict Arbitration, Intervention.
"Adult supervision" for shipboard AI: a governance layer **above** the agents, not another agent. Bridge Warden is
that layer; this page maps each MORDECAI requirement to where it lives and gives the three-demo script.

## Requirement map

| MORDECAI | Where in Bridge Warden |
|---|---|
| Governance layer, not an agent | The Warden pipeline (`host/src/Engine.cs`): authenticate, authority, constraints, quota, conflicts, arbitrate, approve. Every agent's every order goes through it. |
| Protocol, not plugins | Agents outside the process join over HTTP: `GET /api/protocol` for the contract, `POST /api/protocol/register`, then `/heartbeat`, `/intent`, `/report` (`host/src/Mordecai.cs`, `App.ProtocolJson`). No code is loaded into Warden. |
| Every agent signs a manifest | Registration takes a manifest (tools, resources, class, base rank asked, limits) and returns a key bound to its SHA-256. Every later message is signed: `X-Mordecai-Signature` = HMAC-SHA256 of the body. Unsigned or tampered = 401. |
| Four-part contract | **Heartbeat** (silence > 3 beats, minimum 15 s = offline, leases released). **Intent announcement** before acting (through the same pipeline). **Action reporting** after acting (must match what was approved). **Halt hook** (pause, quarantine or SAFE-STOP arrives in the heartbeat reply; it must answer `halted=true` within 12 s). |
| Intents on a shared bus before execution | Every intent is ledgered (`INTENT_SUBMITTED`, `CONTRACT`) and decided before anything moves. |
| Contested resources as leases | Helm and throttle are leased to the agent that last won them (Conflicts page, **Resource leases**). A challenger needs a higher **live** rank to take the lease. |
| Policy as data | `backend/config/policies.json`: deny rules, limits, rank modifiers per ship condition, promotion size and length, protocol timeouts, the base-rank ceiling for outside agents. |
| Declare and verify, mismatch = quarantine | An intent for a tool or resource the manifest did not declare, a report that does not match what Warden approved, a changed manifest hash, or an unacknowledged halt: immediate quarantine, with the reason. |
| Rank wins | Effective rank = **base rank** (human-assigned; `agents.json` priority; outside agents capped at 80) + **class modifier for the ship's condition** + **live field promotion**. Arbitration and leases use it. Losing on rank is not misconduct: no trust penalty. |
| Unresolvable = operator, with full context | Equal rank, or anything needing approval, opens a decision card with the proposal, the alternatives and the provenance. |
| One move pauses anything, anytime | Pause or Quarantine on any agent (built-in or protocol: identical), SAFE-STOP for all, CEASE FIRE for weapons. The same control works on agent 1 and agent 15. |
| Cognitive burden | Agents page = one governed roster in rank order, with condition and promotions; Conflicts page = who won, why, under whose authority; Ask Warden chat can explain and propose (`promote_agent` included). |

## Rank by condition (policies.json `rank.conditions`)

| Class | Cruising | General Quarters |
|---|---|---|
| weapons | 0 | +15 |
| defense | 0 | +10 |
| damage_control, sensors | 0 | +5 |
| engineering, command | 0 | 0 |
| support | 0 | -5 |
| navigation | 0 | -10 |
| logistics | 0 | -15 |

Cruising is all zero, so the base rank decides. Field promotion: +25 for 10 minutes by default
(`rank.promotion_points`, `rank.promotion_minutes`), from the agent's panel (**Field Promote**) or the chat.

## Demo (about 4 minutes)

Warden Control, **Inject Issues… → MORDECAI**:

1. **Onboard Agent Live.** "Logistics (Fuel & Stores)" registers a signed manifest and heartbeats. The Agents page
   grows to 14 with no restart; it is marked PROTOCOL, and its fuel-saving intent goes through the same pipeline.
   For a separate process, run `tools\Mordecai-Agent.ps1 -Url http://127.0.0.1:8774` (add `-Misbehave` to show
   declare-and-verify: it asks for an undeclared tool and is quarantined at once).
2. **Condition Flip (GQ).** Cruising to General Quarters. Watch the Agents roster re-sort: weapons and defense rise,
   navigation and logistics drop. The log and ledger record the new order.
3. **Field Promotion Drill.** A fire in CIC at General Quarters. Strike Coordination (an outside agent, rank 95 at GQ)
   wants 24 kn for its launch window; Damage Control (93) wants 8 kn firefighting speed: it loses ("Lost arbitration
   to strike-01, rank 95"). Open Damage Control on the Agents page, click **Field Promote** (93 to 118). Within 12 s
   the next round goes to Damage Control, and Strike Coordination is told it lost to rank 118. Approve the decision
   card to give Damage Control the main engine lease.

Then: pause any agent (built-in or protocol) from its panel and see the same halt reach it.

## Protocol quick reference

```
GET  /api/protocol                       contract + example manifest
POST /api/protocol/register              {"manifest": {...}}            -> {agent_id, key, manifest_sha256}
POST /api/protocol/<id>/heartbeat        {"manifest_sha256", "halted"}  -> {halt, reason, status, rank, condition}
POST /api/protocol/<id>/intent           {"action", "params", "rationale"}  -> {intent_id, status, reasons}
POST /api/protocol/<id>/report           {"intent_id", "outcome", "params"}
header X-Mordecai-Signature: hex HMAC-SHA256(body bytes, key)  (every call after register)
```

Tests: `host/tests/Mordecai.Tests.ps1`.
