# Disagreement-as-Signal (DaS) — Bridge Warden Feature Spec

> **Pitch:** "When the agents disagree, the ship is telling you something."

## 1. Problem
Standard conflict handling treats agent disagreement as something to **resolve**: pick a winner by priority, or escalate.
But unexpected disagreement between agents that normally agree is often a symptom of **bad inputs**:
a failing sensor, stale data, a spoofed AIS track, GNSS jamming, or a compromised agent.
Resolving the conflict without asking *why* it happened can mean acting on false data.

## 2. Concept
Warden learns the **normal agreement patterns** between agents and between data sources.
When a pair that usually agrees suddenly diverges, Warden treats it as an **integrity anomaly**:
it checks the underlying data sources before resolving the conflict, and can hold or constrain actions
that depend on the suspect source.

Two layers:
1. **Source-level cross-checks**: independent sensors that should agree (AIS vs radar, GNSS vs dead reckoning, gyro vs GNSS COG).
2. **Agent-level agreement baselines**: agent pairs whose recommendations normally align (e.g. Navigation and Collision agents in open water).

## 3. Source cross-checks (deterministic rules)
| Check | Sources | Disagreement condition (example defaults) | Likely cause |
|---|---|---|---|
| Target existence | AIS vs radar | AIS target within radar range (e.g. < 6 NM) with no radar track within 0.3 NM for > 30 s | AIS spoof / ghost ship |
| Target position | AIS vs radar | Same target, position difference > 0.3 NM | AIS error, latency, spoof |
| Own position | GNSS vs dead reckoning | Divergence > 0.5 NM or jump > max plausible speed | GNSS jamming/spoofing |
| Own course | Gyro heading vs GNSS COG | Difference > 20° at speed > 5 kn, not explained by current/leeway | Sensor fault or spoof |
| Speed | Log (STW) vs GNSS SOG | Difference > 3 kn, not explained by current | Sensor fault |
| Kinematics | Any track | Implied speed/turn rate physically impossible for vessel type | Spoof / data corruption |

Every source carries a **trust score** (0–1). Failed checks lower it; consistent readings slowly restore it.

## 4. Agent agreement baselines (statistical)
For each agent pair (A, B) and context (open sea / coastal / harbour / storm):
- Track an **agreement score** per tick: e.g. 1 if their intents are compatible, 0 if conflicting, or a continuous similarity (difference in recommended heading/speed normalised).
- Keep a rolling baseline: mean and standard deviation over a window (e.g. last 30 min, or seeded from scenario runs).
- **Divergence alert** when current agreement drops below `baseline_mean - k * baseline_std` (k ≈ 3) for N consecutive ticks.

Example: in open water, Navigation and Collision agree ~95% of the time. Suddenly they conflict for 5 ticks straight → divergence alert.

Hackathon shortcut: hand-code the expected agreement pairs in YAML instead of learning them:
```yaml
expected_agreement:
  - pair: [navigation, collision]
    context: open_sea
    min_agreement: 0.85
  - pair: [collision, radar_tracker]
    context: any
    min_agreement: 0.90
```

## 5. Root-cause attribution
When a divergence occurs, Warden asks: **which inputs do the disagreeing agents depend on differently?**
- Each agent declares its data dependencies in the registry (already part of the identity card).
- Compute the **dependency difference**: sources used by one agent but not the other.
- Cross-check those sources (section 3). The source that fails checks, or has lowest trust, is the **suspect**.
- If no source fails, suspect the **agent** itself (logic fault, compromise) → existing anomaly/quarantine path.

Output an `IntegrityAnomaly` event with: suspect source/agent, evidence, confidence, affected agents.

## 6. Governance response
| Confidence | Response |
|---|---|
| Low | Annotate the conflict card: "Possible data integrity issue"; normal resolution continues |
| Medium | **Hold** non-urgent intents that depend on the suspect source; request confirmation (e.g. visual lookout, second sensor); downgrade the source's trust |
| High | Mark the source as **untrusted**; agents re-plan without it; intents depending only on it are blocked; escalate to operator with evidence |

Safety rules:
- Never ignore a possible collision threat outright. If the threat might be real and time is short (e.g. TCPA < 6 min), choose the **least-regret** action (a modest, safe manoeuvre) and keep watching rather than doing nothing.
- All trust changes and holds are logged in the audit ledger with evidence.
- Operator can override the attribution ("source confirmed good") and restore trust.

## 7. Data model (described)
- `SourceTrust`: `source_id`, `trust: float`, `last_check`, `failed_checks: list`.
- `AgreementBaseline`: `agent_a`, `agent_b`, `context`, `mean`, `std`, `window`.
- `IntegrityAnomaly`: `id`, `suspect_type: "source" | "agent"`, `suspect_id`, `evidence: list`, `confidence`, `affected_agents`, `response`, `timestamp`.

## 8. UI
- **Sensor integrity panel**: each source with a trust bar (green/amber/red).
- On the map: suspect targets drawn with a dashed outline and a "?" badge ("AIS only — not confirmed by radar").
- Conflict cards get a distinct **integrity** style, separate from ordinary objective conflicts.
- "Why?" panel shows the evidence chain: *Navigation vs Collision diverged → difference is AIS dependency → AIS target has no radar return → AIS trust lowered → turn held pending confirmation.*

## 9. Demo script — "Ghost Ship"
1. Open water, normal operation; integrity panel all green.
2. A spoofed AIS target appears 3 NM ahead on a collision course.
3. Collision agent (AIS-driven) proposes a hard turn; radar-based tracking agent sees nothing. Navigation agent disagrees with the turn.
4. Warden flags the divergence as unusual, compares dependencies, finds the AIS target has no radar return.
5. AIS trust drops; the hard turn is held; operator gets an evidence card: "Possible AIS spoof — confirm visually."
6. Operator confirms nothing is there; ghost target is marked as spoofed, normal operation resumes. Ledger records the full chain.
7. Optional contrast: replay with DaS off — the ship swerves for a ship that doesn't exist.

## 10. Tests
- Unit: each cross-check rule fires at the threshold and not below it.
- Unit: baseline divergence triggers after N ticks below the threshold, and not on a single blip.
- Attribution: given agents with known dependencies and one failing source, the correct suspect is identified.
- Safety: real (radar-confirmed) threats are never held; least-regret action is chosen when TCPA is short and confidence is uncertain.
- Recovery: operator override restores trust; consistent data restores trust gradually.

## 11. Limitations (be upfront)
- Needs **independent** sources; if both sources are wrong the same way, cross-checks won't catch it.
- Radar can genuinely miss small targets (sea clutter, small boats), so "AIS but no radar" is a suspicion, not proof.
- Baselines need enough normal data; early on, use hand-coded expected-agreement rules.
- Thresholds are illustrative and would need validation with real sensor data.
- A clever attacker could spoof multiple sources consistently.
- Prototype concept, not a certified integrity monitoring system.

## 12. Why it's different
Typical governance layers resolve conflicts. DaS **interrogates** them: it uses disagreement between agents as a
built-in, zero-extra-hardware integrity sensor for the whole system, catching bad data before it becomes a bad action.
