# Time-Travel Replay & What-If — Bridge Warden Feature Spec (Possible Feature)

> **Pitch:** "Rewind the ship, see exactly what every agent knew, and test what would have happened if the rules were different."

## 1. Concept
Because every event already goes into the append-only, hash-chained audit ledger, and scenarios are deterministic,
Warden can rebuild the full system state at any moment in the past. A timeline scrubber lets the operator:
1. **Rewind** to any point (e.g. 3 hours ago) and see the world, agents, intents, decisions and operator actions as they were.
2. **Replay** forward at adjustable speed.
3. **Fork** a what-if branch from any moment with changed settings and compare outcomes side by side.

## 2. Modes
| Mode | Description |
|---|---|
| **Live** | Normal operation. |
| **Replay** | Read-only reconstruction from the ledger. No actions can reach actuators. |
| **What-If (Fork)** | Re-runs the simulation from a chosen moment with changes (policies, thresholds, agent on/off, operator decisions, feature toggles such as DaS or OCG). Fully sandboxed. |

Replay and What-If must be visually unmistakable from Live (e.g. sepia/blue tint, persistent "REPLAY" banner) and must never send commands to the real ship.

## 3. How state reconstruction works
- **Event sourcing**: system state = initial snapshot + all ledger events applied in order.
- **Snapshots**: save a full state snapshot every N minutes (e.g. 5) so rewinding doesn't replay from the very beginning.
- To jump to time T: load the latest snapshot before T, then apply events up to T.
- **Determinism requirements**: fixed random seeds per scenario, simulated clock (not wall clock), LLM outputs recorded in the ledger (replay uses recorded text, never calls the LLM again).
- Verify integrity by checking the ledger hash chain before replay; flag any tampering.

## 4. What-If forking
- Pick a moment on the timeline → "Fork from here".
- Change one or more parameters, for example:
  - Disable a feature (DaS off, OCG off).
  - Change thresholds (CPA warning distance, OCI bands).
  - Remove or quarantine an agent earlier.
  - Swap an operator decision (approve instead of reject).
- Run the fork forward (faster than real time) using the same recorded external inputs (sensor data, other vessels).
- Note: once the fork's own ship behaves differently, other vessels' recorded tracks may no longer be realistic. Flag divergence and keep forks short (minutes to an hour).

## 5. Comparison view
- Split screen or overlay: **Actual** vs **What-If** tracks on the map.
- Diff panel: decisions that changed, conflicts that appeared/disappeared, minimum CPA reached, fuel used, number of escalations, oversight capacity.
- One-line LLM summary: e.g. "Without DaS, the ship turned 40° for a spoofed target and lost 6 minutes."

## 6. UI
- Timeline bar along the bottom with markers for key events (conflicts, escalations, band changes, integrity anomalies, safe-stops).
- Click a marker to jump straight there.
- Play / pause / speed (1x, 4x, 16x) / step one tick.
- Filters: show only one agent's events, only escalations, etc.
- "What did this agent know?" panel: inputs, trust scores and the intent it produced at that moment.

## 7. Uses
- **Incident investigation**: reconstruct exactly why something happened.
- **Accountability**: show who decided what (agent or human), and when.
- **Policy tuning**: test new thresholds against real past situations before deploying them.
- **Training**: replay real events for crew training.
- **Demo**: the "with vs without" contrast for features like DaS and OCG.

## 8. Data model (described)
- `Snapshot`: `id`, `sim_time`, `state_blob`, `ledger_seq`, `hash`.
- `ReplaySession`: `id`, `start_time`, `current_time`, `speed`, `mode`.
- `Fork`: `id`, `parent_time`, `changes: dict`, `result_summary`, `created_by`.

## 9. Demo idea
1. After the Ghost Ship scenario, drag the timeline back to just before the spoofed AIS target appeared.
2. Fork with DaS disabled and run at 8x.
3. Split screen: in the fork, the ship swerves hard for a ship that doesn't exist; in reality, Warden held the turn.
4. Diff panel shows the difference in course, time lost and risk created.

## 10. Tests
- Rebuilding state at time T from snapshot + events matches the live state recorded at T.
- Replay of an unchanged fork produces exactly the same outcome as the original (determinism check).
- Replay/What-If modes cannot emit actuator commands.
- Tampered ledger is detected before replay.

## 11. Limitations
- What-if results depend on the simulation; real-world outcomes could differ.
- Other vessels' recorded behaviour doesn't react to a forked ship, so long forks become less realistic.
- Storage grows with history; needs retention rules for long voyages.
- Prototype concept, not a certified voyage data recorder.

## 12. Hackathon scope
- **MVP**: rewind and replay from the ledger with a timeline scrubber and event markers.
- **Stretch**: what-if forking with a feature toggle and split-screen comparison.
