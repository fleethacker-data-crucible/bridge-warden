# Forward Plan Conflict Detection — "Warden sees the argument coming"

## 1. Purpose
Right now Warden only checks actions agents ask for **now**. This feature has agents also share what they **plan** to do over the next 10–15 minutes. Warden checks those plans against each other and against the predicted world, and warns about conflicts **before** they happen.

Example banner: *"In 8 min: Weather (reduce to 10 kn for squall) will clash with Navigation (increase to 15 kn to make the pilot window)."*

## 2. What already exists (don't rebuild)
- Predicted effect of each pending intent (10-min predicted track).
- Dashed predicted-track lines on the map.
- Cumulative-conflict check across pending intents.
- Conflict types: resource, objective, oscillation, cumulative.
- Priority ladder and arbitration.

This feature **reuses** all of these. It only adds a forward time horizon.

## 3. New concept: the Plan
A Plan is a list of **future** intents. It's a forecast, not a request. Plans never execute and never touch actuators.

```json
{
  "plan_id": "plan-nav-01-0042",
  "agent_id": "nav-01",
  "issued_at": "2026-10-26T14:30:00Z",
  "horizon_min": 15,
  "steps": [
    {"at_offset_min": 4,  "action": "set_heading", "params": {"deg": 95}, "reason": "Waypoint WP7", "confidence": 0.9},
    {"at_offset_min": 8,  "action": "set_speed",   "params": {"kn": 15},   "reason": "Pilot window 15:10", "confidence": 0.8}
  ],
  "conditions": ["valid while ETA slack < 10 min"]
}
```

Rules:
- One active plan per agent. A new plan replaces the old one.
- Plans expire at `issued_at + horizon_min`, or earlier if the agent republishes.
- Agents republish every 60 s or when their view changes a lot.
- Plans are optional. An agent with no plan is just treated as "no forecast".
- Rule-based agents generate plans in code (they already know their next waypoint, next weather cell, etc.). If the LLM agent makes plans, it outputs structured JSON that gets validated (see llm-decision-boundary.md).

## 4. Forward check (deterministic code, no LLM)
Runs every 10–15 s, and whenever a plan changes.

1. **Build the timeline.** Merge all plan steps onto a time axis from now to now + 15 min, in 30 s slices.
2. **Simulate forward.** Use the existing prediction model to project own ship, using the planned steps, and dead-reckon the contacts.
3. **Check each slice** with the same checks the live pipeline uses:
   - Resource: two plans set the same target to incompatible values within a 2-min window.
   - Objective: plans push outcomes in opposite directions (ETA vs fuel, speed vs weather).
   - Constraint: the combined projection breaks MIN_CPA, a zone, engine or power limits.
   - Authority: a planned step would fail the Authority stage (catch it early).
   - Oscillation: plans would make one target flip back and forth.
4. **Score each predicted conflict.**
   - `time_to_conflict` (minutes until it happens)
   - `severity` (from the priority ladder and the constraint hit)
   - `confidence` = lowest step confidence × a factor that shrinks the further ahead it is (e.g. 1.0 at 0 min, 0.6 at 15 min)
5. **De-duplicate.** The same pair, target and type inside a 2-min window counts as one conflict. Update it instead of raising a new one.

## 5. What happens when one is found
Graded by time and severity:

| Time to conflict | Severity | Response |
|---|---|---|
| > 10 min | any | Show in the **Upcoming** list only. No alert. |
| 5–10 min | medium/high | Upcoming plus a soft notification. Ask both agents to replan. |
| < 5 min | high/critical | Treated like a live conflict: run arbitration now and pre-stage the winner. Escalate to the human if arbitration can't decide. |
| any | critical (CPA/zone) | Alert right away and show the predicted track on the map. |

**Replan request:** Warden sends both agents a `PLAN_CONFLICT` notice with the other side's constraint (e.g. "speed must stay ≤ 10 kn from T+6 to T+12"). Agents may republish. If the conflict goes away, it's closed as **resolved by replan**, which is the best outcome and gets its own count.

**Pre-arbitration:** For conflicts close in time, Warden runs the normal arbitration ahead of time and records the expected winner. When the real intents arrive, the decision is already made and explained, so there's no last-second scramble.

Important: forward conflicts **never** block or approve anything on their own. The real intent still goes through all 8 pipeline stages when it's submitted.

## 6. Interaction with other features
- **Oversight capacity governor:** forward warnings let Warden spread escalations out before they pile up. In AMBER/RED it pre-arbitrates earlier so the human gets fewer urgent prompts.
- **Disagreement as signal:** if two agents that normally agree start publishing conflicting plans, pass that to the integrity check.
- **Time-travel replay:** plans are logged, so replay can show "Warden predicted this 8 minutes before it happened."
- **Watch handover:** include upcoming predicted conflicts in the briefing.
- **LLM:** may only write the plain-language summary of a predicted conflict. All detection and scoring is code.

## 7. Ledger events
- `PLAN_PUBLISHED` (agent, plan hash, horizon)
- `PLAN_EXPIRED` / `PLAN_REPLACED`
- `FORWARD_CONFLICT_DETECTED` (id, agents, target, type, time_to_conflict, severity, confidence)
- `FORWARD_CONFLICT_UPDATED`
- `FORWARD_CONFLICT_RESOLVED` (reason: replan / pre_arbitrated / expired / became_live)
- `PLAN_CONFLICT_NOTICE_SENT`

Track a metric: **predicted vs actual**, meaning what share of predicted conflicts actually happened. That keeps the forecasts honest and makes a good demo stat.

## 8. UI
- **Upcoming Conflicts panel** (Overview right column, under the live conflict list): rows like `in 8 min · Weather ⟷ Navigation · speed · HIGH · 72% conf`, with a countdown.
- **Timeline strip** under the map: each agent gets a lane for the next 15 min, plan steps show as dots, and predicted conflicts show as red bars across lanes.
- **Map:** hover a forward conflict to see a dotted future track to the conflict point, with a marker labelled "T+8".
- Clear visual difference: forward items are **outlined / dashed**, live items are **solid**.

## 9. Config (policies/forward.yaml)
```yaml
forward_check:
  horizon_min: 15
  slice_s: 30
  run_every_s: 10
  plan_republish_s: 60
  confidence_decay_at_horizon: 0.6
  min_confidence_to_show: 0.4
  bands:
    notify_min: 10
    pre_arbitrate_min: 5
  dedupe_window_min: 2
```

## 10. Demo script (≈60 s)
1. Navigation publishes a plan to speed up to 15 kn at T+8 for the pilot window.
2. Weather publishes a plan to slow to 10 kn at T+7 for a squall.
3. Upcoming panel shows: "in 8 min · Weather ⟷ Navigation · speed · HIGH". The timeline strip shows the red bar.
4. Warden sends replan notices. Navigation replans, speeding up now (T+1 to T+5) to bank time, then accepts 10 kn through the squall.
5. Conflict closes as **resolved by replan**. Narration: "Without forward checking, this would have been a last-second fight at T+8. Warden sorted it out 8 minutes early."
6. Optional: replay the same scenario with forward checking turned off to show the late, urgent escalation instead.

## 11. Tests
- Two plans, same target, incompatible values 1 min apart → one conflict, correct time_to_conflict.
- Same values → no conflict.
- Combined plans breaking MIN_CPA at T+9 → critical, alerted right away.
- Plan replaced so the conflict goes away → RESOLVED (replan).
- Plan expires → conflict closes as expired.
- Confidence decay: an identical conflict at T+2 vs T+14 gives higher confidence at T+2.
- Plans never create actuator commands (assert no execute calls).
- Deterministic: same inputs and seed give the same conflicts (for replay).

## 12. Limits
- Forecasts rely on agents' plans being honest and current. A low-trust agent's plan gets its confidence scaled down by its trust score.
- Contacts are dead-reckoned and may change course.
- The horizon is short on purpose. Past 15 min it's mostly noise.

## 13. Build order
MVP: plan schema + publish, forward check (resource + constraint types), Upcoming panel, ledger events, demo scenario.
Stretch: timeline strip, pre-arbitration, replan negotiation, predicted-vs-actual metric.
