# Watch Handover Guard — Bridge Warden Feature Spec

> **Pitch:** "The ship never forgets what the last watch knew."

## 1. Problem
Watch handovers are a known risk point on the bridge. The incoming officer may not know which agents are restricted,
what conflicts are open, what was overridden and why, or what is about to happen. With many AI agents, that hidden context grows.

## 2. Concept
When the operator changes (existing "Current Operator / switch operator" control), Warden:
1. Builds a **handover briefing** from the ledger and current state (code gathers facts; LLM writes a short readable summary).
2. Enters a **handover window**: temporarily tightens agent autonomy.
3. Requires the incoming officer to **acknowledge** key items before full autonomy returns.

## 3. Briefing contents (facts gathered by code)
| Section | Source |
|---|---|
| Active agents, autonomy levels, anything paused/quarantined/downgraded (and why) | Registry + CONTROL events |
| Open conflicts and pending escalations | Conflict detector + escalation queue |
| Active constraints and temporary restrictions | Policy engine |
| Recent overrides, rollbacks and safe-stops (last watch) | Ledger |
| Current traffic risks (top CPA/TCPA targets) | Collision agent / COP |
| Sensor integrity issues (low trust sources) | Disagreement-as-Signal |
| Oversight capacity trend during last watch | Oversight Capacity Governor |
| Newly approved learned policies | Override Learning |
| Upcoming events (next 30–60 min: waypoints, weather, zones) | Navigation / weather agents |
| Outgoing officer's free-text notes | Handover form |

LLM writes a 5–8 line summary at the top; every line links to its ledger evidence. If the LLM fails, show the structured facts only.

## 4. Handover window (governance response)
- Starts when outgoing officer clicks **Begin Handover**.
- Agents in **act-autonomously** drop to **act-with-approval** for non-routine actions (safety reflexes like collision avoidance within pre-approved envelope still allowed).
- Non-critical escalations are held; critical ones go to **both** officers.
- Ends when incoming officer acknowledges all **critical items** and clicks **Accept Watch**.
- Timeout (e.g. 10 min): escalate to master; keep restricted mode.

## 5. Acknowledgement
- Critical items (quarantined agents, open critical conflicts, untrusted sensors, DANGER/WARNING targets) each need an individual tick.
- Optional quick check: incoming officer answers 1–2 auto-generated questions ("Which agent is quarantined?") — keeps it from becoming a rubber stamp.
- Acknowledgement time feeds the Oversight Capacity Governor (too fast = possible rubber stamp).

## 6. Ledger events
- `HANDOVER_STARTED` (outgoing, incoming, time)
- `HANDOVER_BRIEFING` (snapshot of briefing content + hash)
- `HANDOVER_ITEM_ACK` (item, officer, time)
- `HANDOVER_COMPLETED` / `HANDOVER_TIMEOUT`
Lets Time-Travel Replay show exactly what the incoming officer was told.

## 7. UI
- "Begin Handover" button in the Current Operator panel.
- Full-screen briefing card: summary on top, sections below, critical items with checkboxes.
- Banner during window: "HANDOVER IN PROGRESS — agent autonomy restricted."
- Authority matrix shows temporary restrictions in a distinct colour.

## 8. Demo moment
1. After the storm and rogue agent scenes, switch operator.
2. Briefing appears: "Energy agent downgraded to recommend-only after rollback; AIS trust low; one crossing vessel at WARNING."
3. Authority matrix tightens; banner shows handover in progress.
4. New officer ticks the critical items and accepts; autonomy is restored.

## 9. Tests
- Briefing includes all open critical items from current state.
- Autonomy is restricted during the window and restored exactly after acceptance.
- Safety reflexes still work during handover.
- Timeout escalates and stays restricted.
- LLM failure falls back to structured briefing.

## 10. Limitations
- Briefing quality depends on what's logged; unlogged context (e.g. verbal notes) needs the free-text field.
- Can't guarantee the officer truly understands — the quick check helps but isn't proof.
- Timings are illustrative; real procedures vary by company and flag state.
- Prototype concept, not a certified system.

## 11. Fit with the original plan
Builds on the existing Current Operator panel and switch-operator control, the ledger, and the LLM explain module.
Follows the LLM decision boundary: code gathers facts and enforces the handover window; the LLM only writes the summary.
