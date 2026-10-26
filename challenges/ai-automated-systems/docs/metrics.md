# Metrics — Bridge Warden

Targets are hypotheses until measured. Record how each number was measured. Baselines come from a scripted "no Warden" run of the same scenario (separate agent consoles, no conflict detection) and from Sailor/SME interviews where possible.

## Deckplate impact
| Metric | Baseline (today) | With Warden | How measured | Status |
|---|---|---|---|---|
| Closest approach to a contact in the demo | n/a | ≥ 1.0 nm always (min observed ~1.3 nm) | `test_full_scenario_safety_properties` | Measured (simulation) |
| Consoles / alert streams the watchstander monitors | 1 per AI system (5 in demo) | 1 | Count | Target |
| Time to notice a conflict between agents | TBD | Same tick (≤ 1 s) | Scenario log timestamps | Measured (simulation) |
| Time from conflict to decision | TBD | TBD | Escalation open → approve/reject timestamps | To measure |
| Time to answer "which agent did that and why?" | TBD (manual log search) | < 30 s ("Why?" panel) | Stopwatch, timed by a tester | To measure |
| Time to stop all agents | TBD (one by one) | 2 s (SAFE-STOP hold) | Browser test | Measured (first run) |
| Unsafe actions reaching the actuators in the rogue scenario | All attempted | 0 out-of-bounds actions executed; contained in ~3 s | Ledger + `test_full_scenario_safety_properties` | Measured (simulation) |

## Edge budget (Section 6 of the plan)
| Metric | Budget | Measured | Hardware | Status |
|---|---|---|---|---|
| Device power draw | ≤ 25 W | | | To measure |
| Backend CPU (average) | ≤ 1 core | ~0.01 cores | Dev laptop, demo profile | Measured (first run) |
| Backend RAM | ≤ 500 MB | ~57–60 MB | Dev laptop, demo profile | Measured (first run) |
| Intent message size | ≤ 1 KB | 246 B max | Dev laptop | Measured (first run) |
| UI stream rate | ≤ 50 KB/s | ~10 KB/s per window | Dev laptop, demo profile | Measured (first run); trimmed after 2 windows hit ~47 KB/s |
| External network bytes (core loop) | 0 | 0 (no outbound calls in code) | Any | By design; confirm with network unplugged |
| Fresh install → running demo | < 15 min | | | To measure |
