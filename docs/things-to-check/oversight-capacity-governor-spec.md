# Oversight Capacity Governor (OCG) — Bridge Warden Feature Spec

> **Pitch:** "Agents only get as much freedom as there's a human able to supervise them."

## 1. Problem
Most multi-agent governance designs treat the human operator as an always-available, unlimited approver.
In reality, human oversight fails under load: alarm flooding, alarm fatigue, slow or rubber-stamp approvals.
An escalation that nobody has capacity to review is not real oversight.

## 2. Concept
Warden models the **operator as a governed, capacity-limited resource** in the system, just like fuel, bandwidth or actuator access.
It continuously estimates the operator's **Oversight Capacity Index (OCI)** and uses it to:
1. Scale agent autonomy up or down automatically.
2. Shape the escalation queue (bundle, defer, prioritise) so critical items always get through.
3. Make the state of human oversight visible and auditable.

## 3. Inputs (observable signals only — no biometrics required)
| Signal | Description | Example source |
|---|---|---|
| `pending_escalations` | Open items awaiting operator decision | Escalation inbox |
| `critical_pending` | Open items at DANGER/critical level | Escalation inbox |
| `median_response_s` | Rolling median time to decide (last N decisions) | Audit ledger |
| `alert_rate_per_min` | Alerts shown to operator, rolling 5-min window | Event bus |
| `approval_without_review` | Approvals faster than a minimum read time (rubber-stamp indicator) | UI timing |
| `timeouts` | Escalations that expired without a decision | Escalation engine |
| `idle_s` | Time since last operator interaction | UI heartbeat |
| (optional) `manual_load` | Operator self-reports "busy" / "available" | Dashboard toggle |

## 4. Oversight Capacity Index (OCI)
Score from 0 (no capacity) to 100 (full capacity). Each signal is normalised to a 0–1 "load" value, then weighted:

```
load_queue    = min(pending_escalations / Q_MAX, 1)          # Q_MAX e.g. 5
load_critical = min(critical_pending / C_MAX, 1)              # C_MAX e.g. 2
load_latency  = min(median_response_s / RT_MAX, 1)            # RT_MAX e.g. 60 s
load_alerts   = min(alert_rate_per_min / AR_MAX, 1)           # AR_MAX e.g. 6/min
load_rubber   = rubber_stamp_ratio                            # fraction of last N approvals under min read time
load_timeout  = min(timeouts_last_10min / TO_MAX, 1)          # TO_MAX e.g. 2

load = 0.25*load_queue + 0.20*load_critical + 0.20*load_latency
     + 0.15*load_alerts + 0.10*load_rubber + 0.10*load_timeout

OCI = round(100 * (1 - load))
```
- Smooth with an exponential moving average (e.g. alpha = 0.3) to avoid jitter.
- All weights and maxima live in YAML policy config.
- If `idle_s` exceeds a threshold (e.g. 120 s) while items are pending, treat operator as **absent** and force OCI to the lowest band.

## 5. Capacity bands and governance response
| Band | OCI | Agent autonomy | Escalation handling |
|---|---|---|---|
| **GREEN** | 70–100 | Normal authority per registry | All escalations shown individually |
| **AMBER** | 40–69 | Act-autonomously agents downgraded to act-with-approval for non-routine actions; tighter safety margins (e.g. CPA thresholds +25%) | Low-priority items bundled into summaries; info alerts muted to a digest |
| **RED** | 15–39 | All agents recommend-only except safety-critical reflexes (e.g. collision avoidance within pre-approved envelope) | Only critical items interrupt; others deferred with auto-expiry rules; LLM writes a 1-line summary per bundle |
| **BLACK** (absent/overloaded) | 0–14 | Conservative safe state: hold course/speed or pre-defined minimal-risk manoeuvre; no new non-safety actions | Audible alarm, escalate to secondary operator / master; everything logged |

Rules:
- **Fast to tighten, slow to relax**: drop a band immediately; only rise after OCI stays above the threshold for e.g. 60 s.
- Safety-critical actions inside a **pre-approved envelope** are never blocked by low OCI — the point is to reduce human burden, not to freeze the ship.
- Every band change is written to the audit ledger with the inputs that caused it.

## 6. Escalation shaping
- **Priority score** per escalation: `severity * urgency (1/TCPA or deadline) * confidence`.
- **Bundling**: group related escalations (same agent, same target, same conflict) into one decision card.
- **Deferral**: non-critical items get an expiry action defined in policy (e.g. "default to most conservative option") instead of silently timing out.
- **Rubber-stamp guard**: if approvals come faster than the minimum read time, Warden flags it and lowers OCI rather than trusting the approval as real oversight.

## 7. Agent-side behaviour
- Agents receive the current band on the event bus.
- Agents are expected to **self-throttle**: fewer, higher-quality intents in AMBER/RED.
- Agents that keep spamming intents during RED are rate-limited and flagged (ties into existing anomaly/quarantine logic).

## 8. UI
- **Oversight Capacity gauge** (0–100, coloured by band) prominently on the dashboard.
- Small sparkline of OCI over the last 15 min.
- Banner on band change: e.g. "Oversight capacity RED — agents restricted to recommend-only. 7 items bundled into 2 decisions."
- Authority matrix visibly updates (cells change colour) when autonomy is tightened.
- "Why?" panel explains which signals drove the change.

## 9. Data model (Pydantic sketch, described)
- `OversightState`: `oci: int`, `band: Literal["GREEN","AMBER","RED","BLACK"]`, `signals: dict[str, float]`, `updated_at: datetime`.
- `BandChange` ledger event: `from_band`, `to_band`, `oci`, `signals`, `reason`, `timestamp`.
- `AutonomyOverride`: `agent_id`, `original_level`, `effective_level`, `cause = "oversight_capacity"`.

## 10. Demo script addition (fits the storm scenario)
1. Calm sailing: gauge GREEN (~90).
2. Storm + engine fault: escalations pile up; operator responds slowly. Gauge slides to AMBER, then RED.
3. Warden announces the change: agents drop to recommend-only, authority matrix changes colour, 7 escalations collapse into 2 bundled decisions.
4. A crossing vessel appears: collision agent still acts inside its pre-approved envelope (shows safety isn't frozen).
5. Operator clears the bundles; after the hold period, the gauge climbs back to GREEN and autonomy is restored.
6. Incident report includes an "oversight timeline" showing when human capacity was limited and how Warden compensated.

## 11. Tests
- Unit: OCI calculation for known signal values; band thresholds; hysteresis (fast down, slow up).
- Scenario: escalation flood drives band to RED within expected time; recovery restores original autonomy levels exactly.
- Safety: collision-avoidance reflex still permitted in RED and BLACK within envelope.
- Rubber-stamp: approvals under minimum read time lower OCI and are flagged.
- Ledger: every band change and autonomy override is recorded with causes.

## 12. Limitations (be upfront in the demo)
- OCI is a **proxy** built from interaction signals, not a measurement of fatigue or attention.
- Weights and thresholds are illustrative and would need validation with real bridge crews (human factors trials).
- Could be gamed (e.g. clicking approve quickly) — partly mitigated by the rubber-stamp guard.
- Real deployment would need to align with bridge alert management standards and crew procedures.
- Prototype concept, not a certified system.

## 13. Why it's different
Most governance designs bound **agents**. OCG also bounds autonomy by the **actual availability of human oversight**,
making "human-in-the-loop" something the system measures and enforces, not just assumes.
