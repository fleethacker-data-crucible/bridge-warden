# Override Learning — Bridge Warden Feature Spec

> **Pitch:** "Every override teaches the ship."

## 1. Concept
Every operator override, rejection, constraint change and rollback is already logged with operator ID and reason.
Override Learning mines those events for **repeated patterns** and proposes a **new or adjusted policy rule**.
The LLM suggests; code validates; a human approves. Nothing changes automatically.

## 2. Inputs (already in the ledger)
- `CONTROL` events: override, reject, constrain, downgrade autonomy, rollback, quarantine.
- Context at the time: agent, action type, parameters, COP snapshot (position, zone, weather, traffic, time of day), policies that fired, operator reason text.

## 3. Pattern detection (deterministic code)
- Group override events by feature buckets: `agent`, `action`, `zone/area grid cell`, `weather band`, `traffic density`, `speed band`, `time of day`.
- Flag a pattern when the same bucket gets **≥ N overrides (e.g. 3) within a window (e.g. 24 h of sim time)** and override ratio for that bucket exceeds a threshold (e.g. > 50% of that agent's intents in that bucket).
- Ignore one-off overrides and overrides during safe-stop.

## 4. Rule proposal (LLM suggests)
- Input to LLM: the pattern summary (structured), example events, operator reason texts, and the existing policy YAML.
- Output (JSON schema): a proposed rule in the **same YAML policy format**, plus a plain-language explanation.

Example:
```yaml
- id: SLOW_IN_FISHING_AREA_A7
  when: action == set_speed and zone == "grid_A7"
  check: params.speed_kn <= 8
  on_fail: escalate
  source: override_learning
  evidence: [ledger_seq 1042, 1187, 1310]
```
"Operator reduced speed in grid A7 three times in 6 hours, citing fishing vessels. Proposed: cap speed at 8 kn there and escalate anything faster."

## 5. Validation (code checks)
- Schema-valid YAML using only allowed fields, actions and agents.
- Cannot **loosen** a safety-critical rule (e.g. MIN_CPA, safe-stop, authority of collision avoidance). Learned rules can only add or tighten constraints, unless a human explicitly edits them.
- No conflict or contradiction with existing rules (run policy engine consistency check).
- **What-If test**: replay the past events in the pattern with the new rule (Time-Travel Replay) and show the effect: how many overrides it would have prevented, any new conflicts/escalations it creates.

## 6. Human approval
- New "Suggested Policies" inbox in the UI.
- Card shows: plain-language summary, YAML diff, evidence events (click to jump on timeline), What-If results.
- Actions: **Approve**, **Edit then approve**, **Reject** (with reason), **Snooze**.
- Rejected suggestions are remembered so the same one isn't proposed again.

## 7. Lifecycle
- Approved rules get `source: override_learning`, approver, date, and evidence links.
- Track rule effectiveness: if operators keep overriding the learned rule itself, suggest retiring it.
- Optional expiry for temporary conditions (e.g. "valid for this voyage").
- Everything logged in the ledger.

## 8. Demo moment
1. During the demo, operator overrides the Navigation agent's speed near fishing vessels a few times.
2. A "Suggested Policy" card appears: "Cap speed at 8 kn in this area?"
3. Show the What-If result: "Would have prevented 3 overrides, no new conflicts."
4. Operator approves; next time, Warden handles it without the operator.

## 9. Tests
- Pattern fires at threshold, not below it.
- Proposals that loosen safety rules are rejected by validation.
- Invalid YAML / unknown fields rejected.
- Approved rule is applied and logged; rejected rule not re-proposed.
- What-If evaluation uses recorded data and never touches live actuators.

## 10. Limitations
- Operators can be wrong; learned rules need human review, which is why nothing is automatic.
- Small sample sizes can create spurious patterns; thresholds are illustrative.
- Learned rules are only as good as the context features captured.
- Prototype concept, not a certified system.

## 11. Fit with the original plan
Builds on existing pieces: ledger CONTROL events with reasons, YAML policy engine, LLM explain module, and Time-Travel Replay. Follows the LLM decision boundary: LLM suggests, code checks, human decides.
